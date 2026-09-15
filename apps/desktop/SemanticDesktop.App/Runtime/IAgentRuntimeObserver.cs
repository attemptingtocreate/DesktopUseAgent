using SemanticDesktop.App.Models;

namespace SemanticDesktop.App.Runtime;

public interface IAgentRuntimeObserver
{
    void OnTextDelta(string conversationId, string text);
    void OnToolStarted(string conversationId, ToolInvocation invocation);
    void OnToolCompleted(string conversationId, ToolInvocation invocation);
    void OnApprovalNeeded(string conversationId, string approvalId, string action, string? target);
    void OnStatus(string conversationId, string status);
}

public sealed class NullAgentRuntimeObserver : IAgentRuntimeObserver
{
    public static NullAgentRuntimeObserver Instance { get; } = new();

    public void OnTextDelta(string conversationId, string text) { }
    public void OnToolStarted(string conversationId, ToolInvocation invocation) { }
    public void OnToolCompleted(string conversationId, ToolInvocation invocation) { }
    public void OnApprovalNeeded(string conversationId, string approvalId, string action, string? target) { }
    public void OnStatus(string conversationId, string status) { }
}

public sealed class RecordingAgentRuntimeObserver : IAgentRuntimeObserver
{
    public List<string> Text { get; } = new();
    public List<ToolInvocation> Started { get; } = new();
    public List<ToolInvocation> Completed { get; } = new();
    public List<(string ApprovalId, string Action, string? Target)> Approvals { get; } = new();
    public List<string> Statuses { get; } = new();

    public void OnTextDelta(string conversationId, string text) => Text.Add(text);
    public void OnToolStarted(string conversationId, ToolInvocation invocation) => Started.Add(invocation);
    public void OnToolCompleted(string conversationId, ToolInvocation invocation) => Completed.Add(invocation);
    public void OnApprovalNeeded(string conversationId, string approvalId, string action, string? target) =>
        Approvals.Add((approvalId, action, target));
    public void OnStatus(string conversationId, string status) => Statuses.Add(status);
}

public sealed class CompositeAgentRuntimeObserver : IAgentRuntimeObserver
{
    private readonly List<IAgentRuntimeObserver> _listeners = new();
    private readonly object _gate = new();

    public void Add(IAgentRuntimeObserver observer)
    {
        lock (_gate)
        {
            _listeners.Add(observer);
        }
    }

    public void Remove(IAgentRuntimeObserver observer)
    {
        lock (_gate)
        {
            _listeners.Remove(observer);
        }
    }

    private IAgentRuntimeObserver[] Snapshot()
    {
        lock (_gate)
        {
            return _listeners.ToArray();
        }
    }

    public void OnTextDelta(string conversationId, string text)
    {
        foreach (var listener in Snapshot()) listener.OnTextDelta(conversationId, text);
    }

    public void OnToolStarted(string conversationId, ToolInvocation invocation)
    {
        foreach (var listener in Snapshot()) listener.OnToolStarted(conversationId, invocation);
    }

    public void OnToolCompleted(string conversationId, ToolInvocation invocation)
    {
        foreach (var listener in Snapshot()) listener.OnToolCompleted(conversationId, invocation);
    }

    public void OnApprovalNeeded(string conversationId, string approvalId, string action, string? target)
    {
        foreach (var listener in Snapshot()) listener.OnApprovalNeeded(conversationId, approvalId, action, target);
    }

    public void OnStatus(string conversationId, string status)
    {
        foreach (var listener in Snapshot()) listener.OnStatus(conversationId, status);
    }
}

public sealed class RuntimeRunState
{
    public string ConversationId { get; init; } = "";
    public string Status { get; set; } = TurnStatus.Streaming;
    public bool ComputerControlInFlight { get; set; }
    public string? InFlightTool { get; set; }
}
