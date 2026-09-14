using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Results;
using SemanticDesktop.Core.Targets;

namespace SemanticDesktop.Core.Contracts;

public interface IUIAutomationService
{
    Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken);

    Task<UIElement?> ResolveAsync(SemanticTarget target, CancellationToken cancellationToken);

    Task<IReadOnlyList<UIElement>> FindAsync(UIFindQuery query, CancellationToken cancellationToken);

    Task<UITree> GetTreeAsync(UITreeQuery query, CancellationToken cancellationToken);

    Task<ActionResult> InvokeAsync(ElementHandle element, CancellationToken cancellationToken);

    Task<ActionResult> SetValueAsync(ElementHandle element, string value, CancellationToken cancellationToken);

    Task<string?> GetTextAsync(ElementHandle element, CancellationToken cancellationToken);
}

public interface IWindowService
{
    Task<IReadOnlyList<WindowInfo>> ListAsync(CancellationToken cancellationToken);
    Task<WindowInfo?> FocusAsync(string windowId, CancellationToken cancellationToken);
}

public interface IProcessService
{
    Task<ProcessInfo> LaunchAsync(ProcessLaunchRequest request, CancellationToken cancellationToken);
}
