using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.Discord;

/// <summary>
/// Desktop primitives Discord playbooks need. Implemented by the agent (WindowService + InputService).
/// </summary>
public interface IDiscordDesktopHost
{
    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken);

    Task<WindowInfo?> FocusAsync(string windowId, CancellationToken cancellationToken);

    Task<WindowInfo?> MaximizeAsync(string windowId, CancellationToken cancellationToken);

    Task<WindowMutationResult?> MoveAsync(WindowMoveRequest request, CancellationToken cancellationToken);

    void Hotkey(IReadOnlyList<string> keys);

    void TypeText(string text);

    void Key(string key);
}
