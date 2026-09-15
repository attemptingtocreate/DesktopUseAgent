using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticDesktop.App.Mcp;

namespace SemanticDesktop.App.Tools;

public sealed class AgentToolRouter : IToolRouter
{
    private readonly IAgentRpc? _rpc;
    private readonly IMcpHost? _mcp;

    public AgentToolRouter(IAgentRpc? rpc, IMcpHost? mcp = null)
    {
        _rpc = rpc;
        _mcp = mcp;
    }

    public async Task<ToolRouterResult> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolRouterContext ctx,
        CancellationToken cancellationToken)
    {
        if (ToolCatalog.IsComputerControl(toolName))
        {
            if (!ctx.AllowComputerControl)
            {
                return ToolRouterResult.Denied("Computer control is disabled for this session.");
            }

            if (_rpc is null)
            {
                return ToolRouterResult.Fail("Agent RPC is not configured.");
            }

            var parameters = JsonElementExtensions.WithSession(arguments, ctx.SessionId);
            var result = await _rpc.CallAsync(toolName, parameters, cancellationToken).ConfigureAwait(false);
            return ParseAgentResult(result, toolName);
        }

        if (_mcp is null)
        {
            return ToolRouterResult.Fail($"Unknown tool '{toolName}'.");
        }

        if (ToolCatalog.IsComputerControl(toolName))
        {
            return ToolRouterResult.Denied("External MCP cannot execute computer-control tools.");
        }

        var mcpId = ctx.McpServerId;
        if (string.IsNullOrWhiteSpace(mcpId) || string.Equals(mcpId, McpIds.Builtin, StringComparison.Ordinal))
        {
            return ToolRouterResult.Fail($"No external MCP mapped for tool '{toolName}'.");
        }

        return await _mcp.CallToolAsync(mcpId, toolName, arguments, cancellationToken).ConfigureAwait(false);
    }

    internal static ToolRouterResult ParseAgentResult(JsonElement result, string toolName)
    {
        var root = result;
        if (root.TryGetProperty("ok", out var okEl))
        {
            if (okEl.ValueKind == JsonValueKind.True)
            {
                var summary = Summarize(root.TryGetProperty("data", out var data) ? data : root, toolName);
                return ToolRouterResult.Ok(summary);
            }

            var error = root.TryGetProperty("error", out var err) ? err : root;
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "Tool failed." : "Tool failed.";
            var code = error.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            string? approvalId = null;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("approvalId", out var apr))
            {
                approvalId = apr.GetString();
            }

            var permission = code switch
            {
                "PERMISSION_DENIED" or "PATH_NOT_ALLOWED" or "APPROVAL_DENIED" => "Deny",
                "APPROVAL_REQUIRED" or "APPROVAL_TIMEOUT" => "Ask",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(approvalId) && permission == "Ask")
            {
                return ToolRouterResult.Pending(approvalId, toolName);
            }

            return ToolRouterResult.Fail(message, permission);
        }

        return ToolRouterResult.Ok(Summarize(root, toolName));
    }

    private static string Summarize(JsonElement data, string toolName)
    {
        var raw = data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : data.GetRawText();
        if (raw.Length > 500)
        {
            raw = raw[..500] + "…";
        }

        return $"{toolName}: {raw}";
    }
}

internal static class JsonElementExtensions
{
    public static JsonElement WithSession(JsonElement arguments, string? sessionId)
    {
        JsonObject node;
        try
        {
            node = arguments.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            node = new JsonObject();
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            node["sessionId"] = sessionId;
        }

        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
