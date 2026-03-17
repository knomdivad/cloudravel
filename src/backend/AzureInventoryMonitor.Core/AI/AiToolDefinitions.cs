using System.Text.Json;

namespace AzureInventoryMonitor.Core.AI;

/// <summary>
/// Defines the AI agent tools available for Azure OpenAI function calling.
/// 
/// CRITICAL DESIGN RULE:
///   The AI agent NEVER fabricates data. It can only:
///   1. Call these tools to retrieve data from authoritative sources
///   2. Summarize, correlate, and prioritize the returned data
///   3. Cite which tool provided each piece of information
/// 
/// All tools operate within the tenant boundary enforced by RLS.
/// </summary>
public static class AiToolDefinitions
{
    /// <summary>
    /// Returns the OpenAI function definitions for all available tools.
    /// These are passed to the chat completion API as the 'tools' parameter.
    /// </summary>
    public static IReadOnlyList<AiToolDefinition> GetToolDefinitions()
    {
        return new List<AiToolDefinition>
        {
            new()
            {
                Name = "get_inventory_snapshot",
                Description = "Retrieve the current or historical inventory snapshot for the tenant. Returns a list of Azure resources with their type, location, SKU, tags, and configuration. Use this to answer questions about what resources exist, how many of each type, and their current configuration.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["snapshot_id"] = new() { Type = "integer", Description = "Optional: specific snapshot ID. Omit for latest snapshot.", Required = false },
                    ["resource_type"] = new() { Type = "string", Description = "Optional: filter by resource type (e.g., 'Microsoft.Compute/virtualMachines')", Required = false },
                    ["subscription_id"] = new() { Type = "string", Description = "Optional: filter by subscription ID", Required = false },
                    ["limit"] = new() { Type = "integer", Description = "Maximum resources to return (default: 50, max: 200)", Required = false }
                }
            },
            new()
            {
                Name = "get_inventory_diff",
                Description = "Compare two inventory snapshots and return the differences: resources added, removed, and modified between two points in time. Use this to answer questions about what changed between two snapshots.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["from_snapshot_id"] = new() { Type = "integer", Description = "The older snapshot ID to compare from", Required = true },
                    ["to_snapshot_id"] = new() { Type = "integer", Description = "The newer snapshot ID to compare to. Omit for latest.", Required = false }
                }
            },
            new()
            {
                Name = "get_resource_changes",
                Description = "Retrieve resource configuration changes detected via Azure Resource Graph Change History. Shows what properties changed, who made the change, and when. Limited to ~14 days of history. Use this for recent change analysis, drift detection, and audit questions.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["hours"] = new() { Type = "integer", Description = "Look back N hours (default: 24, max: 336 = 14 days)", Required = false },
                    ["resource_id"] = new() { Type = "string", Description = "Optional: filter to a specific resource's ARM ID", Required = false },
                    ["classification"] = new() { Type = "string", Description = "Optional: filter by classification (security, governance, cost, operational)", Required = false },
                    ["limit"] = new() { Type = "integer", Description = "Maximum changes to return (default: 50, max: 200)", Required = false }
                }
            },
            new()
            {
                Name = "get_resource_detail",
                Description = "Get full configuration details for a specific Azure resource, including properties, networking configuration, and security settings. Use this to drill into a single resource for detailed analysis.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["resource_id"] = new() { Type = "string", Description = "The full ARM resource ID", Required = true }
                }
            },
            new()
            {
                Name = "get_advisor_recommendations",
                Description = "Retrieve Azure Advisor recommendations for the tenant. Covers categories: HighAvailability, Security, Performance, Cost, OperationalExcellence. Includes estimated cost savings for cost recommendations. Use this for optimization and best-practice questions.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["category"] = new() { Type = "string", Description = "Optional: filter by category (HighAvailability, Security, Performance, Cost, OperationalExcellence)", Required = false },
                    ["status"] = new() { Type = "string", Description = "Optional: filter by lifecycle status (Active, Acknowledged, Snoozed, Resolved, Dismissed). Default: Active", Required = false },
                    ["limit"] = new() { Type = "integer", Description = "Maximum recommendations to return (default: 50, max: 200)", Required = false }
                }
            },
            new()
            {
                Name = "get_policy_compliance",
                Description = "Retrieve Azure Policy compliance states for the tenant. Shows which resources are non-compliant with which policies. Use this for governance and compliance questions.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["compliance_state"] = new() { Type = "string", Description = "Filter by state: Compliant, NonCompliant, Exempt, Unknown. Default: NonCompliant", Required = false },
                    ["limit"] = new() { Type = "integer", Description = "Maximum records to return (default: 50, max: 200)", Required = false }
                }
            },
            new()
            {
                Name = "get_defender_findings",
                Description = "Retrieve Microsoft Defender for Cloud security findings for the tenant. Shows security assessments with severity levels and remediation guidance. Use this for security posture and vulnerability questions.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["severity"] = new() { Type = "string", Description = "Optional: filter by severity (Critical, High, Medium, Low, Informational)", Required = false },
                    ["status"] = new() { Type = "string", Description = "Optional: filter by status (Unhealthy, Healthy). Default: Unhealthy", Required = false },
                    ["limit"] = new() { Type = "integer", Description = "Maximum findings to return (default: 50, max: 200)", Required = false }
                }
            },
            new()
            {
                Name = "get_tenant_summary",
                Description = "Get a high-level summary of the tenant's Azure environment: total resources, resource type breakdown, recent changes count, open recommendation counts, and security posture score. Use this as a starting point for broad questions.",
                Parameters = new Dictionary<string, AiToolParameter>()  // No parameters
            },
            new()
            {
                Name = "search_resources",
                Description = "Search for resources by name, type, tag, or location across the current inventory snapshot. Use this when the user asks about specific resources by name or characteristic.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["query"] = new() { Type = "string", Description = "Search term to match against resource name, type, tags, or resource group", Required = true },
                    ["limit"] = new() { Type = "integer", Description = "Maximum results (default: 20, max: 100)", Required = false }
                }
            }
        };
    }

    /// <summary>
    /// Returns a text description of all tools for embedding in a system prompt.
    /// Used for models that don't support native function calling (e.g. Phi-4).
    /// </summary>
    public static string GetToolDescriptionsForPrompt()
    {
        var definitions = GetToolDefinitions();
        var sb = new System.Text.StringBuilder();

        foreach (var def in definitions)
        {
            sb.AppendLine($"### {def.Name}");
            sb.AppendLine(def.Description);
            if (def.Parameters.Count > 0)
            {
                sb.AppendLine("Parameters:");
                foreach (var (name, param) in def.Parameters)
                {
                    var reqStr = param.Required ? " (REQUIRED)" : " (optional)";
                    sb.AppendLine($"  - {name} ({param.Type}){reqStr}: {param.Description}");
                }
            }
            else
            {
                sb.AppendLine("Parameters: none");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

public sealed class AiToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, AiToolParameter> Parameters { get; set; } = new();
}

public sealed class AiToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; } = false;
}
