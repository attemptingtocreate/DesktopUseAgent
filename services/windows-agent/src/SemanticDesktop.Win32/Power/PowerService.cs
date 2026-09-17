using System.Diagnostics;
using System.Runtime.InteropServices;
using SemanticDesktop.Core.Errors;

namespace SemanticDesktop.Win32.Power;

public static class PowerService
{
    public static object Execute(string action, bool confirm)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("action is required.", nameof(action));
        }

        var normalized = action.Trim().ToLowerInvariant();
        var requiresConfirm = normalized is "shutdown" or "restart" or "hibernate";
        if (requiresConfirm && !confirm)
        {
            throw new ArgumentException(
                $"{ErrorCodes.InvalidArgument}: confirm=true is required for action '{normalized}'.");
        }

        switch (normalized)
        {
            case "lock":
                if (!LockWorkStation())
                {
                    throw new InvalidOperationException($"{ErrorCodes.Internal}: LockWorkStation failed.");
                }

                break;
            case "sleep":
                if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
                {
                    throw new InvalidOperationException($"{ErrorCodes.Internal}: SetSuspendState(sleep) failed.");
                }

                break;
            case "hibernate":
                if (!SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false))
                {
                    // Fall back to shutdown.exe hibernate if powrprof fails.
                    RunShutdown("/h");
                }

                break;
            case "shutdown":
                RunShutdown("/s /t 0");
                break;
            case "restart":
                RunShutdown("/r /t 0");
                break;
            default:
                throw new ArgumentException(
                    $"{ErrorCodes.InvalidArgument}: action must be lock|sleep|hibernate|shutdown|restart.");
        }

        return new { action = normalized, initiated = true };
    }

    private static void RunShutdown(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{ErrorCodes.ProcessFailed}: {ex.Message}", ex);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
