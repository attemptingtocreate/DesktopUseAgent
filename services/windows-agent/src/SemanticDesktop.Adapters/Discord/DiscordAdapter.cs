using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.Discord;

public sealed class DiscordAdapter : IApplicationAdapter
{
    private const int DefaultLaunchTimeoutMs = 15_000;
    private const int QuickSwitchSettleMs = 350;
    private const int JoinVerifyMs = 900;

    private readonly IDiscordDesktopHost? _host;

    public DiscordAdapter(IDiscordDesktopHost? host = null)
    {
        _host = host;
    }

    public string Id => "discord";

    public bool CanHandle(ProcessInfo process)
    {
        var name = process.Name ?? "";
        var path = process.Path ?? "";
        return name.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Discord", StringComparison.OrdinalIgnoreCase)
               || path.Contains(@"\Discord\", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var exe = DiscordExecutableCache.Get();
        return Task.FromResult(new ApplicationCapabilities
        {
            AdapterId = Id,
            Available = exe is not null,
            Actions = new[]
            {
                CommandNames.DiscordOpen,
                CommandNames.DiscordJoinVoice,
                CommandNames.DiscordQuickSwitch
            },
            Meta = new Dictionary<string, object?>
            {
                ["executable"] = exe,
                ["provider"] = "discord-quick-switch",
                ["strategy"] = "quick_switch",
                ["hostBound"] = _host is not null
            }
        });
    }

    public async Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        return command.Action.ToLowerInvariant() switch
        {
            CommandNames.DiscordOpen => await OpenAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.DiscordJoinVoice => await JoinVoiceAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.DiscordQuickSwitch => await QuickSwitchAsync(command, cancellationToken).ConfigureAwait(false),
            _ => AdapterResult.Fail(ErrorCodes.Unsupported, $"Unknown discord action '{command.Action}'.")
        };
    }

    public static string? FindDiscordExecutable() => DiscordExecutableCache.Get();

    private async Task<AdapterResult> OpenAsync(AdapterCommand command, CancellationToken ct)
    {
        var hostError = RequireHost();
        if (hostError is not null)
        {
            return hostError;
        }

        var exe = RequireExecutable();
        if (exe.Error is not null)
        {
            return exe.Error;
        }

        var started = Stopwatch.StartNew();
        var ensure = await EnsureDiscordWindowAsync(exe.Value!, ct).ConfigureAwait(false);
        if (ensure.Error is not null)
        {
            return ensure.Error;
        }

        var window = ensure.Window!;
        await ApplyPlacementAsync(window, command.Params, ct).ConfigureAwait(false);
        window = await _host!.FocusAsync(window.Id, ct).ConfigureAwait(false) ?? window;

        return AdapterResult.Success(new
        {
            opened = true,
            windowId = window.Id,
            title = window.Title,
            monitor = GetInt(command.Params, "monitor"),
            placement = GetString(command.Params, "placement"),
            executable = exe.Value,
            durationMs = started.ElapsedMilliseconds,
            strategy = "quick_switch"
        });
    }

    private async Task<AdapterResult> QuickSwitchAsync(AdapterCommand command, CancellationToken ct)
    {
        var hostError = RequireHost();
        if (hostError is not null)
        {
            return hostError;
        }

        var query = GetString(command.Params, "query")
                    ?? GetString(command.Params, "channel");
        if (string.IsNullOrWhiteSpace(query))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "query (or channel) is required.");
        }

        var exe = RequireExecutable();
        if (exe.Error is not null)
        {
            return exe.Error;
        }

        var started = Stopwatch.StartNew();
        var ensure = await EnsureDiscordWindowAsync(exe.Value!, ct).ConfigureAwait(false);
        if (ensure.Error is not null)
        {
            return ensure.Error;
        }

        var window = ensure.Window!;
        await ApplyPlacementAsync(window, command.Params, ct).ConfigureAwait(false);
        await _host!.FocusAsync(window.Id, ct).ConfigureAwait(false);
        await RunQuickSwitchAsync(query, ct).ConfigureAwait(false);

        return AdapterResult.Success(new
        {
            switched = true,
            query,
            windowId = window.Id,
            monitor = GetInt(command.Params, "monitor"),
            durationMs = started.ElapsedMilliseconds,
            strategy = "quick_switch"
        });
    }

    private async Task<AdapterResult> JoinVoiceAsync(AdapterCommand command, CancellationToken ct)
    {
        var hostError = RequireHost();
        if (hostError is not null)
        {
            return hostError;
        }

        var channel = GetString(command.Params, "channel");
        if (string.IsNullOrWhiteSpace(channel))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "channel is required.");
        }

        var server = GetString(command.Params, "server");
        var exe = RequireExecutable();
        if (exe.Error is not null)
        {
            return exe.Error;
        }

        var started = Stopwatch.StartNew();
        var ensure = await EnsureDiscordWindowAsync(exe.Value!, ct).ConfigureAwait(false);
        if (ensure.Error is not null)
        {
            return ensure.Error;
        }

        var window = ensure.Window!;
        var monitor = GetInt(command.Params, "monitor");
        await ApplyPlacementAsync(window, command.Params, ct).ConfigureAwait(false);
        window = await _host!.FocusAsync(window.Id, ct).ConfigureAwait(false) ?? window;

        // Prefer channel-only first; retry with "channel server" if verification fails.
        var primaryQuery = channel;
        var secondaryQuery = string.IsNullOrWhiteSpace(server) ? null : $"{channel} {server}";

        await RunQuickSwitchAsync(primaryQuery, ct).ConfigureAwait(false);
        var joined = await VerifyJoinedAsync(channel, ct).ConfigureAwait(false);
        var usedQuery = primaryQuery;

        if (!joined && secondaryQuery is not null)
        {
            await RunQuickSwitchAsync(secondaryQuery, ct).ConfigureAwait(false);
            joined = await VerifyJoinedAsync(channel, ct).ConfigureAwait(false);
            usedQuery = secondaryQuery;
        }

        var windows = await _host.ListWindowsAsync(ct).ConfigureAwait(false);
        var latest = FindDiscordWindow(windows) ?? window;

        return AdapterResult.Success(new
        {
            joined,
            server,
            channel,
            query = usedQuery,
            windowId = latest.Id,
            title = latest.Title,
            monitor,
            durationMs = started.ElapsedMilliseconds,
            strategy = "quick_switch",
            verify = joined
                ? "title_contains_channel"
                : "best_effort_failed_title_check"
        });
    }

    private async Task RunQuickSwitchAsync(string query, CancellationToken ct)
    {
        _host!.Hotkey(new[] { "ctrl", "k" });
        await Task.Delay(QuickSwitchSettleMs, ct).ConfigureAwait(false);
        _host.TypeText(query);
        await Task.Delay(120, ct).ConfigureAwait(false);
        _host.Key("enter");
    }

    private async Task<bool> VerifyJoinedAsync(string channel, CancellationToken ct)
    {
        await Task.Delay(JoinVerifyMs, ct).ConfigureAwait(false);
        var windows = await _host!.ListWindowsAsync(ct).ConfigureAwait(false);
        var window = FindDiscordWindow(windows);
        if (window is null)
        {
            return false;
        }

        var title = window.Title ?? "";
        if (title.Contains("Friends", StringComparison.OrdinalIgnoreCase) &&
            !title.Contains(channel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Best-effort: Discord titles often include "#channel | Server | Discord".
        if (title.Contains(channel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Weak fallback: still on a Discord window (not Friends hub alone).
        return title.Contains("Discord", StringComparison.OrdinalIgnoreCase) &&
               !title.StartsWith("Friends", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyPlacementAsync(
        WindowInfo window,
        Dictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var monitor = GetInt(parameters, "monitor");
        var placement = GetString(parameters, "placement");
        if (monitor is null && string.IsNullOrWhiteSpace(placement))
        {
            return;
        }

        try
        {
            if (monitor is not null)
            {
                await _host!.MoveAsync(new WindowMoveRequest
                {
                    WindowId = window.Id,
                    Monitor = monitor,
                    Placement = string.IsNullOrWhiteSpace(placement) ? "maximize" : placement
                }, ct).ConfigureAwait(false);
                return;
            }

            if (string.Equals(placement, "maximize", StringComparison.OrdinalIgnoreCase))
            {
                await _host!.MaximizeAsync(window.Id, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Placement is best-effort; join/open continue.
        }
    }

    private async Task<(WindowInfo? Window, AdapterResult? Error)> EnsureDiscordWindowAsync(
        string executable,
        CancellationToken ct)
    {
        var windows = await _host!.ListWindowsAsync(ct).ConfigureAwait(false);
        var existing = FindDiscordWindow(windows);
        if (existing is not null)
        {
            return (existing, null);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            return (null, AdapterResult.Fail(ErrorCodes.AdapterFailed, "Failed to launch Discord: " + ex.Message));
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(DefaultLaunchTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            windows = await _host.ListWindowsAsync(ct).ConfigureAwait(false);
            existing = FindDiscordWindow(windows);
            if (existing is not null)
            {
                return (existing, null);
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return (null, AdapterResult.Fail(
            ErrorCodes.Timeout,
            "Timed out waiting for Discord.exe window (~15s)."));
    }

    private static WindowInfo? FindDiscordWindow(IReadOnlyList<WindowInfo> windows) =>
        windows.FirstOrDefault(w =>
            w.Process.Contains("Discord", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(w.Title));

    private AdapterResult? RequireHost() =>
        _host is null
            ? AdapterResult.Fail(
                ErrorCodes.AdapterUnavailable,
                "Discord desktop host is not bound (window/input services required).")
            : null;

    private static (string? Value, AdapterResult? Error) RequireExecutable()
    {
        var exe = DiscordExecutableCache.Get();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return (null, AdapterResult.Fail(
                ErrorCodes.AdapterUnavailable,
                "Discord.exe not found. Set DISCORD_PATH or install Discord."));
        }

        return (exe, null);
    }

    internal static string? GetString(Dictionary<string, object?>? parameters, string key)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => value.ToString()
        };
    }

    internal static int? GetInt(Dictionary<string, object?>? parameters, string key)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            JsonElement je when je.TryGetInt32(out var n) => n,
            string s when int.TryParse(s, out var n) => n,
            _ => null
        };
    }
}
