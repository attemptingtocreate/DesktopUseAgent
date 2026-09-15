using System.Text.Json;
using SemanticDesktop.ControlCenter.Client;

namespace SemanticDesktop.App.Tools;

public interface IAgentRpc
{
    Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
}

public sealed class AgentBridgeRpc : IAgentRpc
{
    private readonly AgentBridge _bridge;

    public AgentBridgeRpc(AgentBridge bridge)
    {
        _bridge = bridge;
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        _bridge.CallAsync(method, parameters, cancellationToken);
}

public sealed class ToolRouterContext
{
    public string? SessionId { get; init; }
    public string? ConversationId { get; init; }
    public bool AllowComputerControl { get; init; }
    public string? McpServerId { get; init; }
}

public sealed class ToolRouterResult
{
    public bool Success { get; init; }
    public bool PendingApproval { get; init; }
    public string? ApprovalId { get; init; }
    public string? PermissionDecision { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public string? Target { get; init; }
    public string? ContentForModel { get; init; }

    public static ToolRouterResult Ok(string summary, string? target = null, string? permission = "Allow") => new()
    {
        Success = true,
        PermissionDecision = permission,
        Summary = summary,
        Target = target,
        ContentForModel = summary
    };

    public static ToolRouterResult Denied(string message, string? permission = "Deny") => new()
    {
        Success = false,
        PermissionDecision = permission,
        Error = message,
        Summary = message,
        ContentForModel = message
    };

    public static ToolRouterResult Fail(string message, string? permission = null) => new()
    {
        Success = false,
        PermissionDecision = permission,
        Error = message,
        Summary = message,
        ContentForModel = message
    };

    public static ToolRouterResult Pending(string approvalId, string action, string? target = null) => new()
    {
        Success = false,
        PendingApproval = true,
        ApprovalId = approvalId,
        PermissionDecision = "Ask",
        Target = target,
        Summary = $"Approval required for {action}",
        ContentForModel = $"Approval required for {action}"
    };
}

public interface IToolRouter
{
    Task<ToolRouterResult> InvokeAsync(string toolName, JsonElement arguments, ToolRouterContext ctx, CancellationToken cancellationToken);
}

public interface IApprovalCoordinator
{
    Task<bool> WaitAsync(string approvalId, CancellationToken cancellationToken);
}

public sealed class TestApprovalBroker : IApprovalCoordinator
{
    private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Approve(string approvalId) => Resolve(approvalId, true);

    public void Deny(string approvalId) => Resolve(approvalId, false);

    public Task<bool> WaitAsync(string approvalId, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(approvalId, out tcs!))
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[approvalId] = tcs;
            }
        }

        return tcs.Task.WaitAsync(cancellationToken);
    }

    private void Resolve(string approvalId, bool allow)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(approvalId, out tcs!))
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[approvalId] = tcs;
            }
        }

        tcs.TrySetResult(allow);
    }
}
