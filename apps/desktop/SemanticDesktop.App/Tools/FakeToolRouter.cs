using System.Text.Json;

namespace SemanticDesktop.App.Tools;

public sealed class FakeToolRouter : IToolRouter
{
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    public List<(string Name, JsonElement Arguments, ToolRouterContext Context)> Invocations { get; } = new();

    public Dictionary<string, ToolRouterResult> ResultsByName { get; } = new(StringComparer.Ordinal);

    public Func<string, JsonElement, ToolRouterContext, CancellationToken, Task<ToolRouterResult>>? Handler { get; set; }

    public string PendingApprovalId { get; set; } = "apr_test";

    public HashSet<string> AskThenSucceedTools { get; } = new(StringComparer.Ordinal);

    public async Task<ToolRouterResult> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolRouterContext ctx,
        CancellationToken cancellationToken)
    {
        Invocations.Add((toolName, arguments.Clone(), ctx));
        if (AskThenSucceedTools.Contains(toolName) && _asked.Add(toolName))
        {
            return ToolRouterResult.Pending(PendingApprovalId, toolName);
        }

        if (Handler is not null)
        {
            return await Handler(toolName, arguments, ctx, cancellationToken).ConfigureAwait(false);
        }

        if (ResultsByName.TryGetValue(toolName, out var result))
        {
            return result;
        }

        return ToolRouterResult.Ok($"{toolName} ok");
    }
}

public sealed class DirectDispatcherToolRouter : IToolRouter
{
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>> _dispatch;

    public DirectDispatcherToolRouter(Func<string, JsonElement, CancellationToken, Task<JsonElement>> dispatch)
    {
        _dispatch = dispatch;
    }

    public async Task<ToolRouterResult> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolRouterContext ctx,
        CancellationToken cancellationToken)
    {
        if (ToolCatalog.IsComputerControl(toolName) && !ctx.AllowComputerControl)
        {
            return ToolRouterResult.Denied("Computer control is disabled for this session.");
        }

        var parameters = JsonElementExtensions.WithSession(arguments, ctx.SessionId);
        var result = await _dispatch(toolName, parameters, cancellationToken).ConfigureAwait(false);
        return AgentToolRouter.ParseAgentResult(result, toolName);
    }
}
