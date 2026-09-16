using SemanticDesktop.Core.Models;
using SemanticDesktop.Win32.Native;

namespace SemanticDesktop.Win32.Monitors;

public sealed class MonitorService
{
    private static readonly TimeSpan DefaultSnapshotTtl = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _snapshotTtl;
    private MonitorSnapshot? _snapshot;

    public MonitorService(TimeSpan? snapshotTtl = null)
    {
        _snapshotTtl = snapshotTtl ?? DefaultSnapshotTtl;
    }

    public IReadOnlyList<MonitorInfo> List(bool forceRefresh = false) =>
        GetSnapshot(forceRefresh).Monitors;

    public void Refresh() => _snapshot = null;

    public MonitorInfo? GetByIndex(int index, bool forceRefresh = false)
    {
        var snapshot = GetSnapshot(forceRefresh);
        return index >= 0 && index < snapshot.Entries.Count
            ? snapshot.Entries[index].Info
            : null;
    }

    public int? GetMonitorIndexForWindow(IntPtr hwnd, bool forceRefresh = false)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return GetMonitorIndexForRect(rect, forceRefresh);
    }

    internal int? GetMonitorIndexForRect(NativeMethods.RECT rect, bool forceRefresh = false) =>
        GetMonitorIndexForPoint(
            rect.Left + Math.Max(0, rect.Right - rect.Left) / 2,
            rect.Top + Math.Max(0, rect.Bottom - rect.Top) / 2,
            forceRefresh);

    public int? GetMonitorIndexForPoint(int x, int y, bool forceRefresh = false)
    {
        var pointRect = new NativeMethods.RECT { Left = x, Top = y, Right = x + 1, Bottom = y + 1 };
        var handle = NativeMethods.MonitorFromRect(ref pointRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var snapshot = GetSnapshot(forceRefresh);
        for (var i = 0; i < snapshot.Entries.Count; i++)
        {
            if (snapshot.Entries[i].Handle == handle)
            {
                return i;
            }
        }

        return null;
    }

    internal IntPtr GetHandleForIndex(int index, bool forceRefresh = false)
    {
        var snapshot = GetSnapshot(forceRefresh);
        return index >= 0 && index < snapshot.Entries.Count
            ? snapshot.Entries[index].Handle
            : IntPtr.Zero;
    }

    private MonitorSnapshot GetSnapshot(bool forceRefresh)
    {
        if (!forceRefresh
            && _snapshot is not null
            && DateTimeOffset.UtcNow - _snapshot.CapturedAt < _snapshotTtl)
        {
            return _snapshot;
        }

        _snapshot = CaptureSnapshot();
        return _snapshot;
    }

    private static MonitorSnapshot CaptureSnapshot()
    {
        var entries = new List<MonitorEntry>();
        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (hMonitor, _, _, _) =>
            {
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
                };
                if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    return true;
                }

                var (dpiX, dpiY, scale) = GetDpiScale(hMonitor);
                var index = entries.Count;
                entries.Add(new MonitorEntry(
                    hMonitor,
                    new MonitorInfo
                    {
                        Index = index,
                        Bounds = ToRect(info.rcMonitor),
                        WorkArea = ToRect(info.rcWork),
                        Primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                        Scale = scale,
                        DpiX = dpiX,
                        DpiY = dpiY
                    }));
                return true;
            },
            IntPtr.Zero);

        var monitors = entries.Select(entry => entry.Info).ToList();
        return new MonitorSnapshot(DateTimeOffset.UtcNow, entries, monitors);
    }

    private static Rect ToRect(NativeMethods.RECT rect) => new()
    {
        X = rect.Left,
        Y = rect.Top,
        Width = Math.Max(0, rect.Right - rect.Left),
        Height = Math.Max(0, rect.Bottom - rect.Top)
    };

    private static (int DpiX, int DpiY, double Scale) GetDpiScale(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return (96, 96, 1.0);
        }

        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0
                && dpiX > 0)
            {
                return ((int)dpiX, (int)dpiY, dpiX / 96.0);
            }
        }
        catch (DllNotFoundException)
        {
            // older platforms
        }

        return (96, 96, 1.0);
    }

    private sealed record MonitorSnapshot(
        DateTimeOffset CapturedAt,
        IReadOnlyList<MonitorEntry> Entries,
        IReadOnlyList<MonitorInfo> Monitors);

    private sealed record MonitorEntry(IntPtr Handle, MonitorInfo Info);
}
