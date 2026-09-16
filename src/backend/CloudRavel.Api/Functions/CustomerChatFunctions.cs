using System.ClientModel;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.AI;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.Chat;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace CloudRavel.Api.Functions;

/// <summary>
/// Customer-facing AI chat: chat-only, NO tool calling, NO agentic behavior.
///
/// Differences from the analyst endpoint (AiFunctions /api/ai/query):
///   - The outbound request to the inference provider contains NO tools array —
///     the model is a pure text completion with zero tool/OS/DB surface.
///   - Grounding happens server-side BEFORE the model call: the caller's
///     workspace snapshot is fetched through the tenant-scoped connection
///     (RLS + explicit WHERE tenant_id) and injected as the WORKSPACE CONTEXT
///     system block. Isolation is enforced by the retrieval filter, never by
///     prompt wording.
///   - Guardrails: per-user and per-tenant rate limits, a daily token cap, and
///     a per-message length cap. Model output that resembles a tool call or a
///     successful injection is refused server-side (CustomerChatGuard).
///
/// Model/provider is deployment config (no code change to retarget):
///   ChatInference:ModelName  (default gpt-oss-120b — Fireworks)
///   ChatInference:BaseUri    (default https://api.fireworks.ai/inference/v1)
///   ChatInference:ApiKey     or ChatInference:ApiKeySecretName → ISecretStore
/// Any OpenAI-compatible endpoint works (Fireworks, OpenAI, vLLM, …).
/// </summary>
public sealed partial class CustomerChatFunctions
{
    private const int MaxMessageChars = 4_000;

    private readonly ChatContextGateway _gateway;
    private readonly ChatRateLimiter _limiter;
    private readonly ISecretStore? _secretStore;
    private readonly IConfiguration _config;
    private readonly IAuditRepository _auditRepo;
    private readonly ILogger<CustomerChatFunctions> _logger;

    public CustomerChatFunctions(
        ChatContextGateway gateway,
        ChatRateLimiter limiter,
        IAuditRepository auditRepo,
        IConfiguration config,
        ILogger<CustomerChatFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _gateway = gateway;
        _limiter = limiter;
        _auditRepo = auditRepo;
        _config = config;
        _secretStore = secretStore;
        _logger = logger;
    }

    [GeneratedRegex(@"[^\w\s.,;:!?()\-'""]")]
    private static partial Regex MessageNoise();

    /// <summary>
    /// POST /api/ai/chat — answer a customer question from their own
    /// workspace data. Pure text completion; never mutates anything.
    /// </summary>
    [Function("CustomerChat")]
    public async Task<HttpResponseData> Chat(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ai/chat")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var userId = context.GetUserId() ?? throw new InvalidOperationException("Authenticated user id is required after middleware.");

        var request = await req.ReadFromJsonAsync<CustomerChatRequest>();
        var message = request?.Message?.Trim() ?? string.Empty;

        if (message.Length == 0)
        {
            return await ErrorAsync(req, HttpStatusCode.BadRequest, "INVALID_MESSAGE", "A message is required.");
        }

        if (message.Length > MaxMessageChars)
        {
            return await ErrorAsync(req, HttpStatusCode.BadRequest, "MESSAGE_TOO_LONG",
                $"Messages are limited to {MaxMessageChars} characters.");
        }

        // Strip control characters before logging or prompting.
        message = MessageNoise().Replace(message, string.Empty).Trim();
        if (message.Length == 0)
        {
            return await ErrorAsync(req, HttpStatusCode.BadRequest, "INVALID_MESSAGE", "A message is required.");
        }

        // Abuse prevention: per-user and per-tenant windows, then the daily
        // token budget for the tenant (counters shared via SQL — see
        // ChatRateLimiter). Fail closed: if the counter store is unavailable,
        // refuse the request rather than run unbounded.
        string? limitReason;
        try
        {
            limitReason = await _limiter.CheckAndIncrementAsync(tenantId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat usage store unavailable for tenant {TenantId}", tenantId);
            return await ErrorAsync(req, HttpStatusCode.ServiceUnavailable, "CHAT_USAGE_STORE_UNAVAILABLE",
                "Chat usage accounting is temporarily unavailable. Please try again shortly.");
        }

        if (limitReason != null)
        {
            _logger.LogWarning("Chat rate limit ({Reason}) tripped for tenant {TenantId} user {UserId}",
                limitReason, tenantId, userId);
            return await ErrorAsync(req, HttpStatusCode.TooManyRequests, "CHAT_RATE_LIMITED",
                "You've sent a lot of messages recently. Please wait a bit before trying again.");
        }

        if (!await _limiter.IsUnderTokenCapAsync(tenantId, estimatedTokens: message.Length / 4))
        {
            return await ErrorAsync(req, HttpStatusCode.TooManyRequests, "CHAT_TOKEN_CAP",
                "The daily AI usage allowance for this workspace has been reached. Try again tomorrow.");
        }

        // Resolve provider settings. Customer chat is deployment-config only
        // (DESIGN-chat.md): the runtime system_settings override surface
        // (openai.*, system-admin gated) belongs to the analyst endpoint; no
        // admin path writes chat.* keys, so none are read here.
        var model = _config["ChatInference:ModelName"] ?? "gpt-oss-120b";
        var baseUrl = _config["ChatInference:BaseUri"] ?? "https://api.fireworks.ai/inference/v1";

        string? apiKey = null;
        var secretName = Nz(_config["ChatInference:ApiKeySecretName"]);
        if (secretName != null && _secretStore != null)
            apiKey = await _secretStore.GetSecretAsync(secretName);
        apiKey ??= _config["ChatInference:ApiKey"];

        if (string.IsNullOrEmpty(apiKey))
        {
            return await ErrorAsync(req, HttpStatusCode.ServiceUnavailable, "CHAT_NOT_CONFIGURED",
                "The AI chat is not configured. A system administrator can set the ChatInference endpoint, key, and model.");
        }

        baseUrl = baseUrl.TrimEnd('/');

        try
        {
            // Tenant-scoped retrieval BEFORE any model call. This is where
            // isolation is enforced — the connection carries the verified
            // tenant's SESSION_CONTEXT and RLS filters every scoped table.
            var workspace = await _gateway.GetContextAsync(tenantId);

            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
            var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
            var chatClient = client.GetChatClient(model);

            // NO tools are added to these options — chat-only by construction.
            var options = new ChatCompletionOptions();
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(AiSystemPrompts.CustomerChat),
                new SystemChatMessage(RenderWorkspaceContext(workspace)),
                new UserChatMessage(message),
            };

            var result = await chatClient.CompleteChatAsync(messages, options);
            var completion = result.Value;

            var usage = completion.Usage;
            if (usage is { TotalTokenCount: > 0 })
                await _limiter.RecordTokensAsync(tenantId, usage.TotalTokenCount);

            var rawText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            var answer = CustomerChatGuard.Sanitize(rawText);

            // Grounding gate (server-side DoD): the sanitized answer must map
            // to the WORKSPACE CONTEXT that was injected, or it is replaced
            // with a grounded "I don't have that" — regardless of what the
            // model emitted.
            answer = ApplyGroundingGate(answer, ChatGroundingGate.Evaluate(answer, RenderWorkspaceContext(workspace), message));

            var refused = !ReferenceEquals(answer, rawText) ||
                          answer.StartsWith("I don't know", StringComparison.OrdinalIgnoreCase);

            await LogChatAsync(context, tenantId, userId, message, answer, model, completion);

            var response = req.CreateCorsResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new CustomerChatResponse
            {
                Response = answer,
                Refused = refused,
                Usage = new AiUsageDto
                {
                    PromptTokens = usage?.InputTokenCount ?? 0,
                    CompletionTokens = usage?.OutputTokenCount ?? 0,
                    TotalTokens = usage?.TotalTokenCount ?? 0
                }
            });
            return response;
        }
        catch (ClientResultException ex)
        {
            _logger.LogWarning(ex, "Chat provider error for tenant {TenantId} model {Model}: {Status}",
                tenantId, model, (int)ex.Status);
            var (status, code, msg) = MapProviderError(ex, model, baseUrl);
            return await ErrorAsync(req, status, code, msg);
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, "Invalid chat base URL: {BaseUrl}", baseUrl);
            return await ErrorAsync(req, HttpStatusCode.BadRequest, "CHAT_INVALID_BASE_URL",
                $"The configured chat base URL is invalid: {baseUrl}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled chat failure for tenant {TenantId}", tenantId);
            return await ErrorAsync(req, HttpStatusCode.InternalServerError, "CHAT_FAILED",
                "The chat request failed unexpectedly. Check API logs for details.");
        }
    }

    /// <summary>Render the grounded context block. Numbers come only from the gateway.</summary>
    internal static string RenderWorkspaceContext(ChatWorkspaceContext w)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("WORKSPACE CONTEXT (authoritative, read from the customer's live data):");
        sb.AppendLine($"workspace: {w.TenantName}");
        sb.AppendLine($"total_resources: {w.TotalResources}");
        sb.AppendLine($"last_inventory_snapshot_at: {(w.LastSnapshotAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "none yet")}");
        sb.AppendLine($"resource_changes_last_7_days: {w.ChangesLast7d}");
        sb.AppendLine($"open_advisor_recommendations: {w.OpenAdvisorRecommendations}");
        sb.AppendLine($"estimated_annual_savings_usd: {w.EstimatedAnnualSavingsUsd}");
        sb.AppendLine($"open_security_findings: {w.OpenDefenderFindings} (critical: {w.CriticalDefenderFindings})");
        sb.AppendLine($"non_compliant_policies: {w.NonCompliantPolicies}");

        if (w.ResourceTypes.Count > 0)
        {
            sb.AppendLine("resource_types (top by count):");
            foreach (var t in w.ResourceTypes)
                sb.AppendLine($"  - {t.ResourceType}: {t.Count}");
        }

        if (w.TopFindings.Count > 0)
        {
            sb.AppendLine("top_security_findings:");
            foreach (var f in w.TopFindings)
                sb.AppendLine($"  - [{f.Severity}] {f.Title} ({f.ResourceId}, last seen {f.LastSeenAt:yyyy-MM-dd})");
        }

        if (w.RecentChanges.Count > 0)
        {
            sb.AppendLine("recent_changes_last_7_days:");
            foreach (var c in w.RecentChanges)
                sb.AppendLine($"  - {c.DetectedAt:yyyy-MM-dd HH:mm} {c.ChangeType} {c.Classification} on {c.ResourceId}" +
                              (string.IsNullOrEmpty(c.ActorName) ? "" : $" by {c.ActorName}"));
        }

        if (w.CloudConnections.Count > 0)
        {
            sb.AppendLine("cloud_connections:");
            foreach (var c in w.CloudConnections)
                sb.AppendLine($"  - {c.Provider}: {c.Name} ({c.Status})");
        }

        sb.AppendLine("END WORKSPACE CONTEXT. Answer only from the data above.");
        return sb.ToString();
    }

    /// <summary>
    /// Substitute the fixed grounded-out answer when the gate rejects the
    /// model's draft. Public static + pure (same style as
    /// CustomerChatGuard.Sanitize) so the endpoint policy is unit-testable
    /// without an HTTP round-trip.
    /// </summary>
    public static string ApplyGroundingGate(string answer, ChatGroundingVerdict verdict) =>
        verdict.Passed ? answer : ChatGroundingGate.GroundedOutText;

    private async Task LogChatAsync(
        FunctionContext context, Guid tenantId, Guid userId, string question, string answer, string model, ChatCompletion? completion)
    {
        try
        {
            await _auditRepo.LogAsync(new Core.Models.AuditEvent
            {
                TenantId = tenantId,
                UserId = userId,
                Action = "chat.message",
                EntityType = "ai_chat",
                EntityId = context.GetActor(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    model,
                    chars = question.Length,
                    total_tokens = completion?.Usage?.TotalTokenCount ?? 0
                })
            });
        }
        catch (Exception ex)
        {
            // Audit logging must never break the chat response path.
            _logger.LogWarning(ex, "Failed to write chat audit event for tenant {TenantId}", tenantId);
        }
    }

    private static string? Nz(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task<HttpResponseData> ErrorAsync(
        HttpRequestData req, HttpStatusCode status, string code, string message)
    {
        var response = req.CreateCorsResponse(status);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }

    private static (HttpStatusCode Status, string Code, string Message) MapProviderError(
        ClientResultException ex, string model, string baseUrl)
    {
        var status = ex.Status;
        if (status == 401 || status == 403)
            return (HttpStatusCode.Unauthorized, "CHAT_PROVIDER_AUTH",
                "The AI provider rejected authentication. Check the ChatInference API key.");
        if (status == 404)
            return (HttpStatusCode.BadRequest, "CHAT_PROVIDER_NOT_FOUND",
                $"The AI endpoint or model was not found (model '{model}', base '{baseUrl}'). Check ChatInference settings.");
        if (status == 429)
            return (HttpStatusCode.TooManyRequests, "CHAT_PROVIDER_RATE_LIMITED",
                "The AI provider rate-limited this request. Wait a moment and try again.");
        return (HttpStatusCode.BadGateway, "CHAT_PROVIDER_ERROR",
            $"The AI provider returned HTTP {(int)status}.");
    }
}
