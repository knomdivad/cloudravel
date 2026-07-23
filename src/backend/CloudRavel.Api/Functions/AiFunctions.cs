using System.Net;
using System.Text.Json;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.AI;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

/// <summary>
/// AI-assisted analysis endpoint. Talks to any OpenAI-compatible chat
/// completions endpoint (configurable base URL + API key + model — see
/// OpenAI:BaseUrl/ApiKey/Model) with strict tool calling.
///
/// The AI agent operates under these constraints:
///   - Can only retrieve data via defined tools
///   - All tool calls execute against the tenant-scoped database (RLS enforced)
///   - AI cannot fabricate resource states or findings
///   - The ONLY write path is propose_remediation, which respects the tenant's
///     approval gate — the model never executes changes directly
///   - All responses must cite which tool provided each data point
/// </summary>
public sealed class AiFunctions
{
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IChangeRepository _changeRepo;
    private readonly IRecommendationRepository _recRepo;
    private readonly IAnomalyRepository _anomalyRepo;
    private readonly IIncidentRepository _incidentRepo;
    private readonly IRemediationRepository _remediationRepo;
    private readonly IRemediationService _remediationService;
    private readonly ICloudAccountRepository _cloudAccountRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ISystemSettingsRepository _systemSettings;
    private readonly IConfiguration _config;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<AiFunctions> _logger;

    public AiFunctions(
        IInventoryRepository inventoryRepo,
        IChangeRepository changeRepo,
        IRecommendationRepository recRepo,
        IAnomalyRepository anomalyRepo,
        IIncidentRepository incidentRepo,
        IRemediationRepository remediationRepo,
        IRemediationService remediationService,
        ICloudAccountRepository cloudAccountRepo,
        ITenantRepository tenantRepo,
        ISystemSettingsRepository systemSettings,
        IConfiguration config,
        ILogger<AiFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _inventoryRepo = inventoryRepo;
        _changeRepo = changeRepo;
        _recRepo = recRepo;
        _anomalyRepo = anomalyRepo;
        _incidentRepo = incidentRepo;
        _remediationRepo = remediationRepo;
        _remediationService = remediationService;
        _cloudAccountRepo = cloudAccountRepo;
        _tenantRepo = tenantRepo;
        _systemSettings = systemSettings;
        _config = config;
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <summary>Treat an empty/whitespace setting value as unset (null), so it falls through to the env-var default.</summary>
    private static string? Nz(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// POST /api/ai/query
    /// Accepts a natural language query and answers it using tool calling
    /// against the platform's authoritative data stores. The optional 'mode' field
    /// selects the persona: analyst (default) | operations | security | cost.
    /// </summary>
    [Function("AiQuery")]
    public async Task<HttpResponseData> Query(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ai/query")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var request = await req.ReadFromJsonAsync<AiQueryRequest>();
        if (request == null || string.IsNullOrWhiteSpace(request.Query))
        {
            var badReq = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse { Code = "INVALID_QUERY", Message = "Query is required." });
            return badReq;
        }

        _logger.LogInformation("AI query for tenant {TenantId}: {Query}", tenantId, request.Query);

        // Any OpenAI-compatible endpoint: the official OpenAI API by default,
        // or a self-hosted/compatible server via a configured base URL. Settings
        // configured at runtime through the system-admin UI (system_settings +
        // secret store) take precedence over the OpenAI:* env vars, so the key/
        // URL/model can be changed without a restart (the client is built here,
        // per-request). Env vars remain the fallback for headless deployments.
        var settings = await _systemSettings.GetAllAsync();
        var baseUrl = Nz(settings.GetValueOrDefault(SystemSettingKeys.OpenAiBaseUrl)) ?? _config["OpenAI:BaseUrl"];
        var model = Nz(settings.GetValueOrDefault(SystemSettingKeys.OpenAiModel)) ?? _config["OpenAI:Model"] ?? "gpt-4o-mini";

        string? apiKey = null;
        var secretName = Nz(settings.GetValueOrDefault(SystemSettingKeys.OpenAiApiKeySecretName));
        if (secretName != null && _secretStore != null)
            apiKey = await _secretStore.GetSecretAsync(secretName);
        apiKey ??= _config["OpenAI:ApiKey"];

        if (string.IsNullOrEmpty(apiKey))
        {
            var notConfigured = req.CreateCorsResponse(HttpStatusCode.ServiceUnavailable);
            await notConfigured.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "AI_NOT_CONFIGURED",
                Message = "The AI model is not configured. A system administrator can set the OpenAI endpoint, key, and model under Admin → System Settings."
            });
            return notConfigured;
        }

        var clientOptions = string.IsNullOrEmpty(baseUrl)
            ? new OpenAIClientOptions()
            : new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        var chatClient = client.GetChatClient(model);

        // Build native tool definitions for the SDK
        var toolDefinitions = BuildChatTools();

        var toolInvocations = new List<AiToolInvocationDto>();

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(AiSystemPrompts.ForMode(request.Mode)),
            new UserChatMessage(request.Query)
        };

        var options = new ChatCompletionOptions { ToolChoice = ChatToolChoice.CreateAutoChoice() };
        foreach (var tool in toolDefinitions)
            options.Tools.Add(tool);

        // Tool-calling loop: the model may invoke multiple tools iteratively
        const int maxIterations = 10;

        for (var i = 0; i < maxIterations; i++)
        {
            var result = await chatClient.CompleteChatAsync(messages, options);
            var completion = result.Value;

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                // Add the assistant message with tool calls to the conversation
                messages.Add(new AssistantChatMessage(completion));

                // Execute each tool call
                foreach (var toolCall in completion.ToolCalls)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _logger.LogDebug("AI tool call: {Tool}({Args})", toolCall.FunctionName, toolCall.FunctionArguments);

                    var toolResult = await ExecuteToolAsync(tenantId, toolCall.FunctionName, toolCall.FunctionArguments.ToString());
                    sw.Stop();

                    toolInvocations.Add(new AiToolInvocationDto
                    {
                        ToolName = toolCall.FunctionName,
                        Arguments = toolCall.FunctionArguments.ToString(),
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });

                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                }
                continue;
            }

            // No tool call — this is the final answer
            var response = req.CreateCorsResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new AiQueryResponse
            {
                Response = completion.Content[0].Text,
                ToolsUsed = toolInvocations,
                Usage = new AiUsageDto
                {
                    PromptTokens = completion.Usage.InputTokenCount,
                    CompletionTokens = completion.Usage.OutputTokenCount,
                    TotalTokens = completion.Usage.TotalTokenCount
                }
            });
            return response;
        }

        // If we reached max iterations, return what we have
        var fallback = req.CreateCorsResponse(HttpStatusCode.OK);
        await fallback.WriteAsJsonAsync(new AiQueryResponse
        {
            Response = "I was unable to complete the analysis within the allowed number of tool calls. Please try a more specific question.",
            ToolsUsed = toolInvocations,
            Usage = new AiUsageDto()
        });
        return fallback;
    }

    /// <summary>
    /// Builds ChatTool definitions from our AiToolDefinitions for the OpenAI SDK.
    /// </summary>
    private static List<ChatTool> BuildChatTools()
    {
        var tools = new List<ChatTool>();
        foreach (var def in AiToolDefinitions.GetToolDefinitions())
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var (name, param) in def.Parameters)
            {
                properties[name] = new Dictionary<string, string>
                {
                    ["type"] = param.Type,
                    ["description"] = param.Description
                };
                if (param.Required)
                    required.Add(name);
            }

            var schema = BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties,
                required,
                additionalProperties = false
            });

            tools.Add(ChatTool.CreateFunctionTool(def.Name, def.Description, schema));
        }
        return tools;
    }

    /// <summary>
    /// Executes a tool call and returns the result as a JSON string.
    /// All tool executions go through tenant-scoped repositories (RLS enforced).
    /// </summary>
    private async Task<string> ExecuteToolAsync(Guid tenantId, string toolName, string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson) ?? new();

            return toolName switch
            {
                "get_inventory_snapshot" => await ExecuteGetInventorySnapshot(tenantId, args),
                "get_inventory_diff" => await ExecuteGetInventoryDiff(tenantId, args),
                "get_resource_changes" => await ExecuteGetResourceChanges(tenantId, args),
                "get_resource_detail" => await ExecuteGetResourceDetail(tenantId, args),
                "get_advisor_recommendations" => await ExecuteGetAdvisorRecommendations(tenantId, args),
                "get_policy_compliance" => await ExecuteGetPolicyCompliance(tenantId, args),
                "get_defender_findings" => await ExecuteGetDefenderFindings(tenantId, args),
                "get_tenant_summary" => await ExecuteGetTenantSummary(tenantId),
                "search_resources" => await ExecuteSearchResources(tenantId, args),
                "get_operations_summary" => await ExecuteGetOperationsSummary(tenantId),
                "get_anomalies" => await ExecuteGetAnomalies(tenantId, args),
                "get_incidents" => await ExecuteGetIncidents(tenantId, args),
                "get_remediation_actions" => await ExecuteGetRemediationActions(tenantId, args),
                "get_remediation_playbooks" => await ExecuteGetRemediationPlaybooks(args),
                "propose_remediation" => await ExecuteProposeRemediation(tenantId, args),
                "get_cloud_accounts" => await ExecuteGetCloudAccounts(tenantId),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool execution failed: {Tool}", toolName);
            return JsonSerializer.Serialize(new { error = $"Tool execution failed: {ex.Message}" });
        }
    }

    private async Task<string> ExecuteGetInventorySnapshot(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        long? snapshotId = args.TryGetValue("snapshot_id", out var sid) ? sid.GetInt64() : null;
        var resourceType = args.TryGetValue("resource_type", out var rt) ? rt.GetString() : null;
        var subscriptionId = args.TryGetValue("subscription_id", out var sub) ? sub.GetString() : null;
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        var resources = await _inventoryRepo.GetResourcesAsync(tenantId, snapshotId, resourceType, subscriptionId, limit: limit);
        var total = await _inventoryRepo.GetResourceCountAsync(tenantId, snapshotId);

        return JsonSerializer.Serialize(new
        {
            total_resources = total,
            returned = resources.Count,
            resources = resources.Select(r => new
            {
                r.ResourceId,
                r.ResourceType,
                r.ResourceName,
                r.Location,
                r.SkuName,
                r.SkuTier,
                r.Tags,
                r.IdentityType,
                r.SubscriptionId,
                r.ResourceGroup
            })
        });
    }

    private async Task<string> ExecuteGetInventoryDiff(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var fromId = args["from_snapshot_id"].GetInt64();
        long? toId = args.TryGetValue("to_snapshot_id", out var tid) ? tid.GetInt64() : null;

        var fromResources = await _inventoryRepo.GetResourcesAsync(tenantId, fromId, limit: 10000);

        var effectiveToId = toId;
        if (effectiveToId == null)
        {
            var latest = await _inventoryRepo.GetLatestSnapshotAsync(tenantId);
            effectiveToId = latest?.SnapshotId;
        }

        var toResources = effectiveToId.HasValue
            ? await _inventoryRepo.GetResourcesAsync(tenantId, effectiveToId, limit: 10000)
            : new List<Core.Models.InventoryResource>();

        var fromIds = fromResources.Select(r => r.ResourceId).ToHashSet();
        var toIds = toResources.Select(r => r.ResourceId).ToHashSet();

        var added = toResources.Where(r => !fromIds.Contains(r.ResourceId)).Select(r => new { r.ResourceId, r.ResourceType, r.ResourceName });
        var removed = fromResources.Where(r => !toIds.Contains(r.ResourceId)).Select(r => new { r.ResourceId, r.ResourceType, r.ResourceName });

        return JsonSerializer.Serialize(new
        {
            from_snapshot_id = fromId,
            to_snapshot_id = effectiveToId,
            added_count = added.Count(),
            removed_count = removed.Count(),
            added,
            removed
        });
    }

    private async Task<string> ExecuteGetResourceChanges(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var hours = args.TryGetValue("hours", out var h) ? Math.Min(h.GetInt32(), 336) : 24;
        var resourceId = args.TryGetValue("resource_id", out var rid) ? rid.GetString() : null;
        var classificationStr = args.TryGetValue("classification", out var cls) ? cls.GetString() : null;
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        Core.Models.ChangeClassification? classification = null;
        if (!string.IsNullOrEmpty(classificationStr) && Enum.TryParse<Core.Models.ChangeClassification>(classificationStr, true, out var c))
            classification = c;

        var from = DateTime.UtcNow.AddHours(-hours);
        var changes = await _changeRepo.GetChangesAsync(tenantId, from, null, resourceId, classification, limit: limit);

        return JsonSerializer.Serialize(new
        {
            window_hours = hours,
            total_changes = changes.Count,
            changes = changes.Select(ch => new
            {
                ch.ChangeId,
                ch.ResourceId,
                ch.ResourceType,
                change_type = ch.ChangeType.ToString(),
                ch.DetectedAt,
                ch.ActorName,
                ch.ActorType,
                ch.ClientType,
                classification = ch.Classification.ToString(),
                severity = ch.Severity?.ToString(),
                changed_properties = ch.ChangedProperties?.Select(p => new { p.Path, p.Before, p.After })
            })
        });
    }

    private async Task<string> ExecuteGetResourceDetail(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var resourceId = args["resource_id"].GetString()!;
        var resource = await _inventoryRepo.GetResourceByIdAsync(tenantId, resourceId);
        if (resource == null)
            return JsonSerializer.Serialize(new { error = $"Resource '{resourceId}' not found in current inventory." });

        return JsonSerializer.Serialize(new
        {
            resource.ResourceId,
            resource.ResourceType,
            resource.ResourceName,
            resource.Location,
            resource.SkuName,
            resource.SkuTier,
            resource.SkuCapacity,
            resource.Tags,
            resource.IdentityType,
            resource.IdentityPrincipalIds,
            resource.PropertiesJson,
            resource.NetworkingJson,
            resource.SecurityConfigJson
        });
    }

    private async Task<string> ExecuteGetAdvisorRecommendations(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var category = args.TryGetValue("category", out var cat) ? cat.GetString() : null;
        var statusStr = args.TryGetValue("status", out var st) ? st.GetString() : null;
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        Core.Models.RecommendationLifecycle? status = null;
        if (!string.IsNullOrEmpty(statusStr) && Enum.TryParse<Core.Models.RecommendationLifecycle>(statusStr, true, out var s))
            status = s;

        var recs = await _recRepo.GetAdvisorRecommendationsAsync(tenantId, category, status, limit: limit);

        return JsonSerializer.Serialize(new
        {
            total = recs.Count,
            recommendations = recs.Select(r => new
            {
                r.RecommendationId,
                r.ResourceId,
                r.Category,
                r.Impact,
                r.Title,
                r.Description,
                r.RemediationAction,
                r.EstimatedSavings,
                r.Currency,
                status = r.LifecycleStatus.ToString(),
                r.FirstSeenAt,
                r.LastSeenAt
            })
        });
    }

    private async Task<string> ExecuteGetPolicyCompliance(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var complianceState = args.TryGetValue("compliance_state", out var cs) ? cs.GetString() : "NonCompliant";
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        var records = await _recRepo.GetPolicyComplianceAsync(tenantId, complianceState, limit: limit);

        return JsonSerializer.Serialize(new
        {
            compliance_state_filter = complianceState,
            total = records.Count,
            policies = records.Select(r => new
            {
                r.PolicyName,
                r.PolicyDefinitionId,
                r.ResourceId,
                r.ComplianceState,
                r.Category,
                r.LastEvaluatedAt
            })
        });
    }

    private async Task<string> ExecuteGetDefenderFindings(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var severity = args.TryGetValue("severity", out var sev) ? sev.GetString() : null;
        var status = args.TryGetValue("status", out var st) ? st.GetString() : "Unhealthy";
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        var findings = await _recRepo.GetDefenderFindingsAsync(tenantId, severity, status, limit: limit);

        return JsonSerializer.Serialize(new
        {
            severity_filter = severity,
            status_filter = status,
            total = findings.Count,
            findings = findings.Select(f => new
            {
                f.FindingId,
                f.ResourceId,
                f.AssessmentName,
                f.Severity,
                f.Status,
                f.Description,
                f.RemediationSteps,
                f.Categories,
                f.FirstSeenAt,
                f.LastSeenAt
            })
        });
    }

    private async Task<string> ExecuteGetTenantSummary(Guid tenantId)
    {
        var resourceCount = await _inventoryRepo.GetResourceCountAsync(tenantId);
        var resourceTypes = await _inventoryRepo.GetResourceTypeSummaryAsync(tenantId);
        var latestSnapshot = await _inventoryRepo.GetLatestSnapshotAsync(tenantId);
        var recentChanges = await _changeRepo.GetRecentChangesAsync(tenantId, 24, 0);
        var advisorRecs = await _recRepo.GetAdvisorRecommendationsAsync(tenantId, status: Core.Models.RecommendationLifecycle.Active, limit: 500);
        var defenderFindings = await _recRepo.GetDefenderFindingsAsync(tenantId, status: "Unhealthy", limit: 500);
        var policyNonCompliant = await _recRepo.GetPolicyComplianceAsync(tenantId, "NonCompliant", limit: 500);

        var totalSavings = advisorRecs.Where(r => r.Category == "Cost" && r.EstimatedSavings.HasValue).Sum(r => r.EstimatedSavings!.Value);

        return JsonSerializer.Serialize(new
        {
            total_resources = resourceCount,
            last_snapshot_at = latestSnapshot?.CompletedAt,
            resource_type_breakdown = resourceTypes.Take(20).Select(r => new { r.ResourceType, r.Count }),
            changes_last_24h = recentChanges.Count,
            open_advisor_recommendations = advisorRecs.Count,
            estimated_annual_savings_usd = totalSavings,
            open_defender_findings = defenderFindings.Count,
            critical_defender_findings = defenderFindings.Count(f => f.Severity == "Critical"),
            high_defender_findings = defenderFindings.Count(f => f.Severity == "High"),
            non_compliant_policies = policyNonCompliant.Count
        });
    }

    // ------------------------------------------------------------------
    // AIOps tools
    // ------------------------------------------------------------------

    private async Task<string> ExecuteGetOperationsSummary(Guid tenantId)
    {
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        var openAnomalies = await _anomalyRepo.GetAnomaliesAsync(tenantId, Core.Models.AnomalyStatus.Open, limit: 200);
        var incidents = await _incidentRepo.GetIncidentsAsync(tenantId, limit: 100);
        var pendingApprovals = await _remediationRepo.GetPendingApprovalCountAsync(tenantId);
        var recentActions = await _remediationRepo.GetActionsAsync(tenantId, limit: 50);
        var accounts = await _cloudAccountRepo.GetByTenantAsync(tenantId);

        var now = DateTime.UtcNow;
        var active = incidents.Where(i => i.Status is Core.Models.IncidentStatus.Open
            or Core.Models.IncidentStatus.Acknowledged or Core.Models.IncidentStatus.Mitigated).ToList();

        return JsonSerializer.Serialize(new
        {
            auto_remediation_mode = tenant?.AutoRemediationMode.ToString(),
            monitoring_enabled = tenant?.AiOpsMonitoringEnabled,
            open_anomalies = openAnomalies.Count,
            open_anomalies_by_severity = openAnomalies.GroupBy(a => a.Severity.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            open_incidents = active.Count,
            sla_breached_incidents = active.Count(i => i.SlaDueAt.HasValue && i.SlaDueAt < now),
            pending_approvals = pendingApprovals,
            remediations_last_7d = recentActions.Count(a => a.CreatedAt >= now.AddDays(-7) && a.Status == Core.Models.RemediationStatus.Succeeded),
            cloud_accounts = accounts.Select(a => new { provider = a.Provider.ToString(), a.ExternalId, status = a.Status.ToString() })
        });
    }

    private async Task<string> ExecuteGetAnomalies(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var status = args.TryGetValue("status", out var st) && Enum.TryParse<Core.Models.AnomalyStatus>(st.GetString(), true, out var s)
            ? s : Core.Models.AnomalyStatus.Open;
        Core.Models.AnomalySeverity? severity = args.TryGetValue("severity", out var sev)
            && Enum.TryParse<Core.Models.AnomalySeverity>(sev.GetString(), true, out var sv) ? sv : null;
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        var anomalies = await _anomalyRepo.GetAnomaliesAsync(tenantId, status, severity, limit: limit);

        return JsonSerializer.Serialize(new
        {
            total = anomalies.Count,
            anomalies = anomalies.Select(a => new
            {
                a.Id,
                kind = a.Kind.ToString(),
                severity = a.Severity.ToString(),
                status = a.Status.ToString(),
                provider = a.Provider.ToString(),
                a.Title,
                a.Description,
                a.ResourceId,
                a.MetricName,
                observed = a.ObservedValue,
                baseline_mean = a.BaselineMean,
                z_score = a.Score,
                a.DetectedAt,
                a.IncidentId,
                details = a.DetailsJson
            })
        });
    }

    private async Task<string> ExecuteGetIncidents(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        Core.Models.IncidentStatus? status = args.TryGetValue("status", out var st)
            && Enum.TryParse<Core.Models.IncidentStatus>(st.GetString(), true, out var s) ? s : null;
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        var incidents = await _incidentRepo.GetIncidentsAsync(tenantId, status, limit: limit);
        var now = DateTime.UtcNow;

        return JsonSerializer.Serialize(new
        {
            total = incidents.Count,
            incidents = incidents.Select(i => new
            {
                i.Id,
                i.Title,
                severity = i.Severity.ToString(),
                status = i.Status.ToString(),
                i.Source,
                i.CreatedAt,
                i.SlaDueAt,
                sla_breached = i.SlaDueAt.HasValue && i.SlaDueAt < now &&
                               i.Status is Core.Models.IncidentStatus.Open
                                   or Core.Models.IncidentStatus.Acknowledged
                                   or Core.Models.IncidentStatus.Mitigated,
                anomaly_count = i.AnomalyCount,
                remediation_count = i.RemediationCount,
                summary = i.SummaryMarkdown
            })
        });
    }

    private async Task<string> ExecuteGetRemediationActions(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        Core.Models.RemediationStatus? status = args.TryGetValue("status", out var st)
            && Enum.TryParse<Core.Models.RemediationStatus>(st.GetString(), true, out var s) ? s : null;
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 200) : 50;

        var actions = await _remediationRepo.GetActionsAsync(tenantId, status, limit: limit);

        return JsonSerializer.Serialize(new
        {
            total = actions.Count,
            actions = actions.Select(a => new
            {
                a.Id,
                a.PlaybookKey,
                provider = a.Provider.ToString(),
                a.ResourceId,
                a.Title,
                a.Reason,
                status = a.Status.ToString(),
                risk = a.RiskLevel.ToString(),
                a.RequestedBy,
                approval_mode = a.ApprovalMode,
                a.ApprovedBy,
                a.CreatedAt,
                a.CompletedAt,
                error = a.ErrorMessage
            })
        });
    }

    private async Task<string> ExecuteGetRemediationPlaybooks(Dictionary<string, JsonElement> args)
    {
        Core.Models.CloudProvider? provider = args.TryGetValue("provider", out var p)
            && Enum.TryParse<Core.Models.CloudProvider>(p.GetString(), true, out var pv) ? pv : null;

        var playbooks = await _remediationRepo.GetPlaybooksAsync(provider);

        return JsonSerializer.Serialize(new
        {
            total = playbooks.Count,
            playbooks = playbooks.Select(pb => new
            {
                pb.PlaybookKey,
                pb.DisplayName,
                pb.Description,
                provider = pb.Provider.ToString(),
                pb.Category,
                risk = pb.RiskLevel.ToString(),
                always_requires_approval = pb.AlwaysRequiresApproval,
                parameters_schema = pb.ParametersSchemaJson
            })
        });
    }

    private async Task<string> ExecuteProposeRemediation(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var playbookKey = args.TryGetValue("playbook_key", out var pk) ? pk.GetString() : null;
        var title = args.TryGetValue("title", out var t) ? t.GetString() : null;
        var reason = args.TryGetValue("reason", out var r) ? r.GetString() : null;
        if (string.IsNullOrWhiteSpace(playbookKey) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(reason))
            return JsonSerializer.Serialize(new { error = "playbook_key, title, and reason are required." });

        var resourceId = args.TryGetValue("resource_id", out var rid) ? rid.GetString() : null;
        var parametersJson = args.TryGetValue("parameters_json", out var pj) ? pj.GetString() : null;

        try
        {
            var action = await _remediationService.ProposeAsync(
                tenantId, playbookKey, resourceId, title, reason, parametersJson, requestedBy: "ai:query");

            return JsonSerializer.Serialize(new
            {
                proposed = true,
                action_id = action.Id,
                status = action.Status.ToString(),
                approval_mode = action.ApprovalMode,
                risk = action.RiskLevel.ToString(),
                note = action.Status == Core.Models.RemediationStatus.Approved
                    ? "Auto-approved by tenant policy (low risk); the execution worker will run it within ~5 minutes."
                    : "Awaiting human approval in the approvals queue. Nothing has been changed yet."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { proposed = false, error = ex.Message });
        }
    }

    private async Task<string> ExecuteGetCloudAccounts(Guid tenantId)
    {
        var accounts = await _cloudAccountRepo.GetByTenantAsync(tenantId);
        return JsonSerializer.Serialize(new
        {
            total = accounts.Count,
            accounts = accounts.Select(a => new
            {
                a.AccountId,
                provider = a.Provider.ToString(),
                a.ExternalId,
                a.DisplayName,
                status = a.Status.ToString(),
                regions = a.Regions,
                last_inventory_at = a.LastInventoryAt,
                last_error = a.LastError
            })
        });
    }

    private async Task<string> ExecuteSearchResources(Guid tenantId, Dictionary<string, JsonElement> args)
    {
        var query = args["query"].GetString()!;
        var limit = args.TryGetValue("limit", out var lim) ? Math.Min(lim.GetInt32(), 100) : 20;

        // Simple search across resource name, type, and resource group
        var allResources = await _inventoryRepo.GetResourcesAsync(tenantId, limit: 10000);
        var matches = allResources
            .Where(r =>
                r.ResourceName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.ResourceType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.ResourceGroup.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.Tags != null && r.Tags.Any(t =>
                    t.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Value.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .Take(limit)
            .Select(r => new
            {
                r.ResourceId,
                r.ResourceType,
                r.ResourceName,
                r.Location,
                r.SkuName,
                r.ResourceGroup,
                r.Tags
            });

        return JsonSerializer.Serialize(new { query, results = matches, result_count = matches.Count() });
    }

}
