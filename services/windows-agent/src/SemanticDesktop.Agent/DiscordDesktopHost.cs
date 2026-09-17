using SemanticDesktop.Adapters.Discord;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Win32.Input;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Agent;

internal sealed class DiscordDesktopHost : IDiscordDesktopHost
{
    private readonly WindowService _windows;
    private readonly InputService _input;

    public DiscordDesktopHost(WindowService windows, InputService input)
    {
        _windows = windows;
        _input = input;
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken) =>
        _windows.ListAsync(cancellationToken);

    public Task<WindowInfo?> FocusAsync(string windowId, CancellationToken cancellationToken) =>
        _windows.FocusAsync(windowId, cancellationToken);

    public Task<WindowInfo?> MaximizeAsync(string windowId, CancellationToken cancellationToken) =>
        _windows.MaximizeAsync(windowId, cancellationToken);

    public Task<WindowMutationResult?> MoveAsync(WindowMoveRequest request, CancellationToken cancellationToken) =>
        _windows.MoveAsync(request, cancellationToken);

    public void Hotkey(IReadOnlyList<string> keys) => _input.Hotkey(keys);

    public void TypeText(string text) => _input.TypeText(text);

    public void Key(string key) => _input.Key(key);
}
