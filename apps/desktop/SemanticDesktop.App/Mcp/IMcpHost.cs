using System.Text.Json;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Tools;

namespace SemanticDesktop.App.Mcp;

public interface IMcpClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<ToolRouterResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default);
}

public interface IMcpHost
{
    Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(string mcpId, CancellationToken cancellationToken = default);
    Task<ToolRouterResult> CallToolAsync(string mcpId, string name, JsonElement arguments, CancellationToken cancellationToken = default);
    McpServerStatus GetStatus(string mcpId);
    void RecordError(string mcpId, string? error);
}
