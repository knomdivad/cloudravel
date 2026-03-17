using System.Net;
using System.Text.Json;
using AzureInventoryMonitor.Api.Middleware;
using AzureInventoryMonitor.Core.AI;
using AzureInventoryMonitor.Core.DTOs;
using AzureInventoryMonitor.Core.Interfaces;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Functions;

/// <summary>
/// AI-assisted analysis endpoint using Claude via Azure AI Foundry with strict tool calling.
/// 
/// The AI agent operates under these constraints:
///   - Can only retrieve data via defined tools
///   - All tool calls execute against the tenant-scoped database (RLS enforced)
///   - AI cannot fabricate resource states or findings
///   - All responses must cite which tool provided each data point
/// </summary>
public sealed class AiFunctions
{
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IChangeRepository _changeRepo;
    private readonly IRecommendationRepository _recRepo;
    private readonly IConfiguration _config;
    private readonly ILogger<AiFunctions> _logger;

    public AiFunctions(
        IInventoryRepository inventoryRepo,
        IChangeRepository changeRepo,
        IRecommendationRepository recRepo,
        IConfiguration config,
        ILogger<AiFunctions> logger)
    {
        _inventoryRepo = inventoryRepo;
        _changeRepo = changeRepo;
        _recRepo = recRepo;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/ai/query
    /// Accepts a natural language query and uses Claude via Azure AI Foundry with tool calling
    /// to answer it using only authoritative data from the platform's data stores.
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
            var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse { Code = "INVALID_QUERY", Message = "Query is required." });
            return badReq;
        }

        _logger.LogInformation("AI query for tenant {TenantId}: {Query}", tenantId, request.Query);

        var endpoint = _config["AzureOpenAI:Endpoint"]!;
        var apiKey = _config["AzureOpenAI:ApiKey"]!;
        var deploymentName = _config["AzureOpenAI:DeploymentName"] ?? "gpt-41-nano";

        var azureClient = new AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(apiKey));
        var chatClient = azureClient.GetChatClient(deploymentName);

        // Build native tool definitions for the SDK
        var toolDefinitions = BuildChatTools();

        var toolInvocations = new List<AiToolInvocationDto>();

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(AiSystemPrompts.InventoryAnalyst),
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
            var response = req.CreateResponse(HttpStatusCode.OK);
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
        var fallback = req.CreateResponse(HttpStatusCode.OK);
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
