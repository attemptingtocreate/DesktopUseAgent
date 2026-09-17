using System.Diagnostics;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Win32.Apps;

public sealed class AppLaunchRequest
{
    public required string Name { get; init; }
    public string[]? Args { get; init; }
    public int? Monitor { get; init; }
    public string? Placement { get; init; }
}

public sealed class AppLauncher
{
    private const int WindowWaitMs = 8_000;
    private readonly WindowService _windows;

    public AppLauncher(WindowService windows)
    {
        _windows = windows;
    }

    public async Task<object> LaunchAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("name is required.", nameof(request));
        }

        var started = Stopwatch.StartNew();
        var name = request.Name.Trim();

        // Primary: Start Menu .lnk + App Paths + fuzzy exe
        var executable = AppExecutableCache.Resolve(name);
        if (executable is not null)
        {
            return await LaunchExecutableAsync(name, executable, request, started, cancellationToken)
                .ConfigureAwait(false);
        }

        // Additional: AppsFolder / Appx AUMID
        var appx = AppsFolderCatalog.FindBestMatch(name)
            ?? throw new InvalidOperationException($"{ErrorCodes.NotFound}: Could not resolve app '{name}'.");

        var launch = AppsFolderLauncher.Launch(appx.Aumid, appx.DisplayName);
        var pid = GetAnonInt(launch, "pid");
        var window = await WaitForWindowAsync(pid, appx.DisplayName, cancellationToken).ConfigureAwait(false);
        if (window is not null)
        {
            await ApplyPlacementAsync(window, request.Monitor, request.Placement, cancellationToken).ConfigureAwait(false);
        }

        return new
        {
            launched = true,
            name,
            displayName = appx.DisplayName,
            aumid = appx.Aumid,
            executable = (string?)null,
            pid,
            windowId = window?.Id,
            durationMs = started.ElapsedMilliseconds,
            provider = GetAnonString(launch, "provider") ?? "appx"
        };
    }

    private async Task<object> LaunchExecutableAsync(
        string name,
        string executable,
        AppLaunchRequest request,
        Stopwatch started,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true
        };
        if (request.Args is { Length: > 0 })
        {
            startInfo.Arguments = string.Join(" ", request.Args.Select(QuoteArg));
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{ErrorCodes.ProcessFailed}: Failed to start process.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"{ErrorCodes.ProcessFailed}: {ex.Message}", ex);
        }

        var window = await WaitForWindowAsync(process.Id, Path.GetFileNameWithoutExtension(executable), cancellationToken)
            .ConfigureAwait(false);

        if (window is not null)
        {
            await ApplyPlacementAsync(window, request.Monitor, request.Placement, cancellationToken).ConfigureAwait(false);
        }

        return new
        {
            launched = true,
            name,
            executable,
            pid = process.Id,
            windowId = window?.Id,
            durationMs = started.ElapsedMilliseconds,
            provider = "exe"
        };
    }

    private async Task<WindowInfo?> WaitForWindowAsync(int? pid, string processStem, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(WindowWaitMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var windows = await _windows.ListAsync(ct).ConfigureAwait(false);
            var match = pid is int p
                ? windows.FirstOrDefault(w => w.Pid == p)
                : null;
            match ??= windows.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.Process) &&
                w.Process.Contains(processStem, StringComparison.OrdinalIgnoreCase));
            match ??= windows.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.Title) &&
                w.Title.Contains(processStem, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task ApplyPlacementAsync(WindowInfo window, int? monitor, string? placement, CancellationToken ct)
    {
        if (monitor is null && string.IsNullOrWhiteSpace(placement))
        {
            return;
        }

        try
        {
            if (monitor is not null)
            {
                await _windows.MoveAsync(new WindowMoveRequest
                {
                    WindowId = window.Id,
                    Monitor = monitor,
                    Placement = string.IsNullOrWhiteSpace(placement) ? "maximize" : placement
                }, ct).ConfigureAwait(false);
                return;
            }

            if (string.Equals(placement, "maximize", StringComparison.OrdinalIgnoreCase))
            {
                await _windows.MaximizeAsync(window.Id, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Placement is best-effort.
        }
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;

    private static int? GetAnonInt(object anon, string name)
    {
        var prop = anon.GetType().GetProperty(name);
        var value = prop?.GetValue(anon);
        return value switch
        {
            int i => i,
            uint u => (int)u,
            long l => (int)l,
            _ => null
        };
    }

    private static string? GetAnonString(object anon, string name)
    {
        var prop = anon.GetType().GetProperty(name);
        return prop?.GetValue(anon) as string;
    }
}
