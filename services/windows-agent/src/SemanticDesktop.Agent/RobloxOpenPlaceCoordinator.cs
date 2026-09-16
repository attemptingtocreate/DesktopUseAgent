using SemanticDesktop.Adapters;
using SemanticDesktop.Adapters.RobloxStudio;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Agent;

public sealed class RobloxOpenPlaceCoordinator
{
    private readonly IRobloxWindowPlacer _windows;

    public RobloxOpenPlaceCoordinator(IRobloxWindowPlacer windows)
    {
        _windows = windows;
    }

    public RobloxOpenPlaceCoordinator(WindowService windows)
        : this(new WindowServiceRobloxWindowPlacer(windows))
    {
    }

    public async Task<AdapterResult> OpenPlaceAsync(
        IRobloxPlaceLauncher launcher,
        Dictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var launch = await launcher.LaunchPlaceAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (!launch.Ok)
        {
            return launch;
        }

        var monitor = RobloxStudioAdapter.GetInt(parameters, "monitor");
        var placement = RobloxStudioAdapter.GetString(parameters, "placement");
        var x = RobloxStudioAdapter.GetInt(parameters, "x");
        var y = RobloxStudioAdapter.GetInt(parameters, "y");
        var timeoutSeconds = RobloxStudioAdapter.ClampInt(
            RobloxStudioAdapter.GetInt(parameters, "timeoutSeconds"),
            20,
            1,
            120);
        var hasPlacement = monitor is not null || !string.IsNullOrWhiteSpace(placement) || x is not null || y is not null;
        if (!hasPlacement)
        {
            return launch;
        }

        if (launch.Data is not Dictionary<string, object?> launchData ||
            !launchData.TryGetValue("pid", out var pidRaw) ||
            pidRaw is not int pid)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, "Roblox Studio launched without a process id for window placement.");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        WindowInfo? matched = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = await _windows.ListWindowsAsync(cancellationToken).ConfigureAwait(false);
            matched = windows.FirstOrDefault(w =>
                w.Pid == pid &&
                w.Process.Contains("RobloxStudioBeta", StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                break;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        if (matched is null)
        {
            return AdapterResult.Success(new Dictionary<string, object?>(launchData, StringComparer.Ordinal)
            {
                ["windowPlacement"] = "timeout",
                ["windowFound"] = false
            });
        }

        WindowMutationResult? moved = null;
        try
        {
            moved = await _windows.MoveWindowAsync(new WindowMoveRequest
            {
                WindowId = matched.Id,
                Monitor = monitor,
                X = x,
                Y = y,
                Placement = string.IsNullOrWhiteSpace(placement) ? "preserve" : placement
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AdapterResult.Success(new Dictionary<string, object?>(launchData, StringComparer.Ordinal)
            {
                ["windowId"] = matched.Id,
                ["windowFound"] = true,
                ["windowPlacement"] = "failed",
                ["placementError"] = ex.Message
            });
        }

        return AdapterResult.Success(new Dictionary<string, object?>(launchData, StringComparer.Ordinal)
        {
            ["windowId"] = matched.Id,
            ["windowFound"] = true,
            ["windowPlacement"] = moved is null ? "failed" : "applied",
            ["monitor"] = monitor,
            ["placement"] = placement,
            ["x"] = x,
            ["y"] = y,
            ["window"] = matched
        });
    }
}
