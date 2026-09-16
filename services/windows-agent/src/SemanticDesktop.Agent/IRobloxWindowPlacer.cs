using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Agent;

public interface IRobloxWindowPlacer
{
    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken);

    Task<WindowMutationResult?> MoveWindowAsync(WindowMoveRequest request, CancellationToken cancellationToken);
}

internal sealed class WindowServiceRobloxWindowPlacer : IRobloxWindowPlacer
{
    private readonly WindowService _windows;

    public WindowServiceRobloxWindowPlacer(WindowService windows)
    {
        _windows = windows;
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken) =>
        _windows.ListAsync(cancellationToken);

    public Task<WindowMutationResult?> MoveWindowAsync(WindowMoveRequest request, CancellationToken cancellationToken) =>
        _windows.MoveAsync(request, cancellationToken);
}
