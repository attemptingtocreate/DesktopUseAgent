using System.Diagnostics;
using SemanticDesktop.Core.Contracts;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Win32.Native;

namespace SemanticDesktop.Win32.Windows;

public sealed class WindowService : IWindowService
{
    private readonly HandleRegistry _handles;

    public WindowService(HandleRegistry handles)
    {
        _handles = handles;
    }

    public Task<IReadOnlyList<WindowInfo>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windows = EnumerateTopLevelWindows();
        return Task.FromResult<IReadOnlyList<WindowInfo>>(windows);
    }

    public Task<WindowInfo?> FocusAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_handles.TryResolve<IntPtr>(windowId, out var hwnd, out _))
        {
            return Task.FromResult<WindowInfo?>(null);
        }

        if (!NativeMethods.IsWindow(hwnd))
        {
            _handles.Remove(windowId);
            return Task.FromResult<WindowInfo?>(null);
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        ForceForeground(hwnd);
        var info = ToWindowInfo(windowId, hwnd);
        return Task.FromResult<WindowInfo?>(info);
    }

    public bool TryGetHwnd(string windowId, out IntPtr hwnd)
    {
        if (_handles.TryResolve<IntPtr>(windowId, out hwnd, out _))
        {
            if (NativeMethods.IsWindow(hwnd))
            {
                return true;
            }

            _handles.Remove(windowId);
        }

        // Recover stable hwnd-based IDs across process restarts.
        if (windowId.StartsWith("win_", StringComparison.Ordinal) &&
            ulong.TryParse(windowId.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out var raw))
        {
            var recovered = new IntPtr(unchecked((long)raw));
            if (NativeMethods.IsWindow(recovered))
            {
                EnsureWindowHandle(recovered);
                hwnd = recovered;
                return true;
            }
        }

        hwnd = IntPtr.Zero;
        return false;
    }

    public string? FindWindowIdByHwnd(IntPtr hwnd)
    {
        foreach (var id in _handles.ListIds(HandleKind.Window))
        {
            if (_handles.TryResolve<IntPtr>(id, out var existing, out _) && existing == hwnd)
            {
                return id;
            }
        }

        return null;
    }

    public string EnsureWindowHandle(IntPtr hwnd)
    {
        var stableId = "win_" + unchecked((ulong)hwnd.ToInt64()).ToString("x");
        var existing = FindWindowIdByHwnd(hwnd);
        if (existing is not null)
        {
            return existing;
        }

        return _handles.Allocate(HandleKind.Window, hwnd, new Dictionary<string, object?>
        {
            ["hwnd"] = hwnd.ToInt64()
        }, preferredId: stableId);
    }

    private List<WindowInfo> EnumerateTopLevelWindows()
    {
        var result = new List<WindowInfo>();
        var seenHwnds = new HashSet<IntPtr>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            seenHwnds.Add(hwnd);
            var id = EnsureWindowHandle(hwnd);
            result.Add(ToWindowInfo(id, hwnd, title));
            return true;
        }, IntPtr.Zero);

        _handles.RemoveWhere(entry =>
            entry.Kind == HandleKind.Window &&
            entry.NativeKey is IntPtr hwnd &&
            !seenHwnds.Contains(hwnd));

        return result.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private WindowInfo ToWindowInfo(string id, IntPtr hwnd, string? title = null)
    {
        title ??= GetWindowTitle(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var processName = "unknown";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = string.IsNullOrWhiteSpace(process.ProcessName)
                ? "unknown"
                : process.ProcessName + ".exe";
        }
        catch
        {
            // process may have exited
        }

        Rect? bounds = null;
        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            bounds = new Rect
            {
                X = rect.Left,
                Y = rect.Top,
                Width = Math.Max(0, rect.Right - rect.Left),
                Height = Math.Max(0, rect.Bottom - rect.Top)
            };
        }

        return new WindowInfo
        {
            Id = id,
            Title = title,
            Process = processName,
            Pid = (int)pid,
            Foreground = NativeMethods.GetForegroundWindow() == hwnd,
            Minimized = NativeMethods.IsIconic(hwnd),
            Bounds = bounds
        };
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundThread);
        var currentThread = NativeMethods.GetCurrentThreadId();
        if (foregroundThread != currentThread)
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
        else
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
    }
}

public sealed class StaleWindowException : Exception
{
    public StaleWindowException() : base(ErrorCodes.StaleTarget)
    {
    }
}
