using System.Diagnostics;
using System.Runtime.InteropServices;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Contracts;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Win32.Monitors;
using SemanticDesktop.Win32.Native;

namespace SemanticDesktop.Win32.Windows;

public sealed class WindowService : IWindowService
{
    private readonly HandleRegistry _handles;
    private readonly MonitorService _monitors;

    public WindowService(HandleRegistry handles, MonitorService? monitors = null)
    {
        _handles = handles;
        _monitors = monitors ?? new MonitorService();
    }

    public MonitorService Monitors => _monitors;

    public Task<IReadOnlyList<WindowInfo>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windows = EnumerateTopLevelWindows();
        return Task.FromResult<IReadOnlyList<WindowInfo>>(windows);
    }

    public Task<WindowInfo?> GetAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHwnd(windowId, out var hwnd))
        {
            return Task.FromResult<WindowInfo?>(null);
        }

        return Task.FromResult<WindowInfo?>(ToWindowInfo(windowId, hwnd));
    }

    public Task<WindowInfo?> FocusAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHwnd(windowId, out var hwnd))
        {
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

    public Task<WindowInfo?> MinimizeAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHwnd(windowId, out var hwnd))
        {
            return Task.FromResult<WindowInfo?>(null);
        }

        ShowWindowExpecting(hwnd, NativeMethods.SW_MINIMIZE, h => NativeMethods.IsIconic(h));
        return Task.FromResult<WindowInfo?>(ToWindowInfo(windowId, hwnd));
    }

    public Task<WindowInfo?> MaximizeAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHwnd(windowId, out var hwnd))
        {
            return Task.FromResult<WindowInfo?>(null);
        }

        ShowWindowExpecting(hwnd, NativeMethods.SW_MAXIMIZE, h => NativeMethods.IsZoomed(h));
        return Task.FromResult<WindowInfo?>(ToWindowInfo(windowId, hwnd));
    }

    public Task<WindowInfo?> RestoreAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHwnd(windowId, out var hwnd))
        {
            return Task.FromResult<WindowInfo?>(null);
        }

        ShowWindowExpecting(
            hwnd,
            NativeMethods.SW_RESTORE,
            h => !NativeMethods.IsIconic(h) && !NativeMethods.IsZoomed(h));
        return Task.FromResult<WindowInfo?>(ToWindowInfo(windowId, hwnd));
    }

    public Task<WindowMutationResult?> MoveAsync(WindowMoveRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHwnd(request.WindowId, out var hwnd))
        {
            return Task.FromResult<WindowMutationResult?>(null);
        }

        var placement = string.IsNullOrWhiteSpace(request.Placement) ? "preserve" : request.Placement;
        var hasMonitor = request.Monitor is int;
        var hasX = request.X is int;
        var hasY = request.Y is int;

        if (!hasMonitor && !(hasX && hasY))
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: window.move requires monitor and/or both x and y.");
        }

        if ((hasX && !hasY) || (!hasX && hasY))
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: window.move requires both x and y when either is provided.");
        }

        var priorBounds = GetBounds(hwnd);
        if (!string.Equals(placement, "maximize", StringComparison.OrdinalIgnoreCase))
        {
            EnsureNormalState(hwnd);
        }

        if (hasMonitor)
        {
            _monitors.List(forceRefresh: true);
        }

        var (targetX, targetY) = ResolveMoveTarget(request, hasMonitor, hasX, hasY);

        RequireSetWindowPos(
            hwnd,
            targetX,
            targetY,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        if (string.Equals(placement, "maximize", StringComparison.OrdinalIgnoreCase))
        {
            ShowWindowExpecting(hwnd, NativeMethods.SW_MAXIMIZE, h => NativeMethods.IsZoomed(h));
        }

        return Task.FromResult<WindowMutationResult?>(new WindowMutationResult
        {
            Window = ToWindowInfo(request.WindowId, hwnd),
            PriorBounds = priorBounds
        });
    }

    public Task<WindowMutationResult?> ResizeAsync(WindowResizeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHwnd(request.WindowId, out var hwnd))
        {
            return Task.FromResult<WindowMutationResult?>(null);
        }

        if (request.Width <= 0 || request.Height <= 0)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: width and height must be positive.");
        }

        var priorBounds = GetBounds(hwnd);
        EnsureNormalState(hwnd);

        var targetX = priorBounds?.X ?? 0;
        var targetY = priorBounds?.Y ?? 0;
        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            targetX = request.X ?? rect.Left;
            targetY = request.Y ?? rect.Top;
        }
        else if (request.X is int x)
        {
            targetX = x;
        }
        else if (request.Y is int y)
        {
            targetY = y;
        }

        RequireSetWindowPos(
            hwnd,
            (int)targetX,
            (int)targetY,
            request.Width,
            request.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        return Task.FromResult<WindowMutationResult?>(new WindowMutationResult
        {
            Window = ToWindowInfo(request.WindowId, hwnd),
            PriorBounds = priorBounds
        });
    }

    public string? TryGetProcessName(string windowId)
    {
        if (!TryGetHwnd(windowId, out var hwnd))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.IsNullOrWhiteSpace(process.ProcessName)
                ? null
                : process.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    public ProcessInfo? TryResolveProcess(string? processId, string? windowId)
    {
        if (!string.IsNullOrWhiteSpace(windowId))
        {
            if (!TryGetHwnd(windowId, out var hwnd))
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            return BuildProcessInfo((int)pid, processId ?? windowId);
        }

        if (!string.IsNullOrWhiteSpace(processId))
        {
            if (processId.StartsWith("proc_", StringComparison.Ordinal) &&
                int.TryParse(processId.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out var pid))
            {
                return BuildProcessInfo(pid, processId);
            }

            if (int.TryParse(processId, out var numericPid))
            {
                return BuildProcessInfo(numericPid, processId);
            }
        }

        return null;
    }

    private static ProcessInfo? BuildProcessInfo(int pid, string id)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // access may be denied
            }

            return new ProcessInfo
            {
                Id = id,
                Pid = pid,
                Name = process.ProcessName + ".exe",
                Path = path
            };
        }
        catch
        {
            return null;
        }
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

    internal static Rect? GetRestoreBounds(IntPtr hwnd)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
        };
        if (!NativeMethods.GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var rect = placement.rcNormalPosition;
        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new Rect
        {
            X = rect.Left,
            Y = rect.Top,
            Width = width,
            Height = height
        };
    }

    private (int X, int Y) ResolveMoveTarget(WindowMoveRequest request, bool hasMonitor, bool hasX, bool hasY)
    {
        if (hasMonitor)
        {
            var monitorIndex = request.Monitor!.Value;
            var monitor = _monitors.GetByIndex(monitorIndex, forceRefresh: true)
                ?? throw new ArgumentException($"{ErrorCodes.InvalidArgument}: monitor index {monitorIndex} out of range.");

            if (hasX && hasY)
            {
                return ((int)monitor.WorkArea.X + request.X!.Value, (int)monitor.WorkArea.Y + request.Y!.Value);
            }

            return ((int)monitor.WorkArea.X, (int)monitor.WorkArea.Y);
        }

        return (request.X!.Value, request.Y!.Value);
    }

    private static void EnsureNormalState(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
        {
            ShowWindowExpecting(
                hwnd,
                NativeMethods.SW_RESTORE,
                h => !NativeMethods.IsIconic(h) && !NativeMethods.IsZoomed(h));
        }
    }

    private static void RequireSetWindowPos(
        IntPtr hwnd,
        int x,
        int y,
        int width,
        int height,
        uint flags)
    {
        if (!NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, flags))
        {
            throw new InvalidOperationException($"{ErrorCodes.Internal}: SetWindowPos failed.");
        }
    }

    private static void ShowWindowExpecting(IntPtr hwnd, int command, Func<IntPtr, bool> verify)
    {
        NativeMethods.ShowWindow(hwnd, command);
        if (!verify(hwnd))
        {
            throw new InvalidOperationException($"{ErrorCodes.Internal}: ShowWindow did not reach expected state.");
        }
    }

    private List<WindowInfo> EnumerateTopLevelWindows()
    {
        var result = new List<WindowInfo>();
        var seenHwnds = new HashSet<IntPtr>();
        var processNames = new Dictionary<int, string>();

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
            result.Add(ToWindowInfo(id, hwnd, title, processNames));
            return true;
        }, IntPtr.Zero);

        _handles.RemoveWhere(entry =>
            entry.Kind == HandleKind.Window &&
            entry.NativeKey is IntPtr hwnd &&
            !seenHwnds.Contains(hwnd));

        return result.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private WindowInfo ToWindowInfo(
        string id,
        IntPtr hwnd,
        string? title = null,
        Dictionary<int, string>? processNames = null)
    {
        title ??= GetWindowTitle(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var processName = ResolveProcessName((int)pid, processNames);

        var minimized = NativeMethods.IsIconic(hwnd);
        var maximized = NativeMethods.IsZoomed(hwnd);
        return new WindowInfo
        {
            Id = id,
            Title = title,
            Process = processName,
            Pid = (int)pid,
            Foreground = NativeMethods.GetForegroundWindow() == hwnd,
            Minimized = minimized,
            Maximized = maximized,
            ShowState = GetShowState(minimized, maximized),
            MonitorIndex = _monitors.GetMonitorIndexForWindow(hwnd),
            Bounds = GetBounds(hwnd),
            RestoreBounds = GetRestoreBounds(hwnd)
        };
    }

    private static Rect? GetBounds(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return new Rect
        {
            X = rect.Left,
            Y = rect.Top,
            Width = Math.Max(0, rect.Right - rect.Left),
            Height = Math.Max(0, rect.Bottom - rect.Top)
        };
    }

    private static string GetShowState(bool minimized, bool maximized)
    {
        if (minimized)
        {
            return "minimized";
        }

        return maximized ? "maximized" : "normal";
    }

    private static string ResolveProcessName(int pid, Dictionary<int, string>? processNames)
    {
        if (processNames is not null && processNames.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var processName = "unknown";
        try
        {
            using var process = Process.GetProcessById(pid);
            processName = string.IsNullOrWhiteSpace(process.ProcessName)
                ? "unknown"
                : process.ProcessName + ".exe";
        }
        catch
        {
            // process may have exited
        }

        processNames?.TryAdd(pid, processName);
        return processName;
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
