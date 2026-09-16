using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.RobloxStudio;

public sealed class RobloxStudioAdapter : IApplicationAdapter, IRobloxPlaceLauncher
{
    public const int DefaultMaxHierarchyDepth = 3;
    public const int DefaultMaxHierarchyNodes = 100;
    public const int MaxHierarchyDepth = 10;
    public const int MaxHierarchyNodes = 500;
    public const int MaxSelectCount = 50;

    private readonly IRobloxBridge _bridge;

    public RobloxStudioAdapter(IRobloxBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public string Id => "roblox";

    public bool CanHandle(ProcessInfo process)
    {
        var name = process.Name ?? "";
        var path = process.Path ?? "";
        return name.Contains("RobloxStudioBeta", StringComparison.OrdinalIgnoreCase)
               || path.Contains("RobloxStudioBeta.exe", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var exe = FindRobloxStudioExecutable();
        var health = _bridge.GetHealth();
        return Task.FromResult(new ApplicationCapabilities
        {
            AdapterId = Id,
            Available = exe is not null || health.Listening,
            Actions = new[]
            {
                CommandNames.RobloxOpenPlace,
                CommandNames.RobloxPluginPing,
                CommandNames.RobloxGetHierarchy,
                CommandNames.RobloxGetSelection,
                CommandNames.RobloxSelect,
                CommandNames.RobloxSetProperty
            },
            Meta = new Dictionary<string, object?>
            {
                ["executable"] = exe,
                ["provider"] = "roblox-studio-plugin",
                ["bridgeListening"] = health.Listening,
                ["bridgePort"] = health.Port,
                ["pluginConnected"] = health.PluginConnected
            }
        });
    }

    public async Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        return command.Action.ToLowerInvariant() switch
        {
            CommandNames.RobloxOpenPlace => await LaunchPlaceAsync(command.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxPluginPing => PluginPing(command),
            CommandNames.RobloxGetHierarchy => await GetHierarchyAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxGetSelection => await GetSelectionAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxSelect => await SelectAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxSetProperty => await SetPropertyAsync(command, cancellationToken).ConfigureAwait(false),
            _ => AdapterResult.Fail(ErrorCodes.Unsupported, $"Unknown roblox action '{command.Action}'.")
        };
    }

    private AdapterResult PluginPing(AdapterCommand command)
    {
        var health = _bridge.GetHealth();
        var sessionId = GetString(command.Params, "sessionId");
        RobloxBridgeSessionInfo? session = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            session = health.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        }
        else
        {
            session = health.Sessions.FirstOrDefault(s => s.PollingEnabled);
        }

        return AdapterResult.Success(new
        {
            bridgeListening = health.Listening,
            bridgePort = health.Port,
            pluginConnected = health.PluginConnected,
            session = session is null ? null : new
            {
                sessionId = session.SessionId,
                studioVersion = session.StudioVersion,
                placeName = session.PlaceName,
                pollingEnabled = session.PollingEnabled,
                lastSeenUtc = session.LastSeenUtc,
                hasOutstandingCommand = session.HasOutstandingCommand
            },
            sessions = health.Sessions
        });
    }

    private async Task<AdapterResult> GetHierarchyAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var depth = ClampInt(GetInt(command.Params, "depth"), DefaultMaxHierarchyDepth, 1, MaxHierarchyDepth);
        var maxNodes = ClampInt(GetInt(command.Params, "maxNodes"), DefaultMaxHierarchyNodes, 1, MaxHierarchyNodes);
        var rootPath = GetString(command.Params, "rootPath");
        var sessionId = GetString(command.Params, "sessionId");
        var timeout = TimeSpan.FromSeconds(ClampInt(GetInt(command.Params, "timeoutSeconds"), 30, 1, 120));

        var result = await _bridge.ExecuteCommandAsync(
            RobloxBridgeOperations.GetHierarchy,
            new Dictionary<string, object?>
            {
                ["depth"] = depth,
                ["maxNodes"] = maxNodes,
                ["rootPath"] = rootPath
            },
            sessionId,
            timeout,
            cancellationToken).ConfigureAwait(false);

        return MapBridgeResult(result);
    }

    private async Task<AdapterResult> GetSelectionAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var sessionId = GetString(command.Params, "sessionId");
        var timeout = TimeSpan.FromSeconds(ClampInt(GetInt(command.Params, "timeoutSeconds"), 15, 1, 120));
        var result = await _bridge.ExecuteCommandAsync(
            RobloxBridgeOperations.GetSelection,
            null,
            sessionId,
            timeout,
            cancellationToken).ConfigureAwait(false);
        return MapBridgeResult(result);
    }

    private async Task<AdapterResult> SelectAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var ids = GetStringArray(command.Params, "instanceIds") ?? GetStringArray(command.Params, "ids");
        if (ids is null || ids.Count == 0)
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "instanceIds is required.");
        }

        if (ids.Count > MaxSelectCount)
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"instanceIds exceeds max of {MaxSelectCount}.");
        }

        var sessionId = GetString(command.Params, "sessionId");
        var timeout = TimeSpan.FromSeconds(ClampInt(GetInt(command.Params, "timeoutSeconds"), 15, 1, 120));
        var result = await _bridge.ExecuteCommandAsync(
            RobloxBridgeOperations.Select,
            new Dictionary<string, object?> { ["instanceIds"] = ids },
            sessionId,
            timeout,
            cancellationToken).ConfigureAwait(false);
        return MapBridgeResult(result);
    }

    private async Task<AdapterResult> SetPropertyAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var instanceId = GetString(command.Params, "instanceId");
        var property = GetString(command.Params, "property");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "instanceId is required.");
        }

        if (string.IsNullOrWhiteSpace(property))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "property is required.");
        }

        if (!command.Params!.TryGetValue("value", out var value))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "value is required.");
        }

        if (!RobloxPropertyContract.TryValidateSetPropertyValue(property, value, out var normalizedValue, out var validationError))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, validationError ?? "Invalid property value.");
        }

        var sessionId = GetString(command.Params, "sessionId");
        var timeout = TimeSpan.FromSeconds(ClampInt(GetInt(command.Params, "timeoutSeconds"), 20, 1, 120));
        var result = await _bridge.ExecuteCommandAsync(
            RobloxBridgeOperations.SetProperty,
            new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["property"] = property,
                ["value"] = normalizedValue
            },
            sessionId,
            timeout,
            cancellationToken).ConfigureAwait(false);
        return MapBridgeResult(result);
    }

    public Task<AdapterResult> LaunchPlaceAsync(Dictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var path = GetString(parameters, "path") ?? GetString(parameters, "file");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(AdapterResult.Fail(ErrorCodes.InvalidArgument, "path to .rbxl/.rbxlx file is required."));
        }

        path = Path.GetFullPath(path);
        var ext = Path.GetExtension(path);
        if (!ext.Equals(".rbxl", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".rbxlx", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AdapterResult.Fail(ErrorCodes.InvalidArgument, "path must be a .rbxl or .rbxlx file."));
        }

        if (!File.Exists(path))
        {
            return Task.FromResult(AdapterResult.Fail(ErrorCodes.NotFound, "Place file not found."));
        }

        var exe = FindRobloxStudioExecutable();
        if (exe is null)
        {
            return Task.FromResult(AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "RobloxStudioBeta.exe not found."));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = Quote(path),
            UseShellExecute = true
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return Task.FromResult(AdapterResult.Fail(ErrorCodes.ProcessFailed, ex.Message));
        }

        return Task.FromResult(AdapterResult.Success(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["opened"] = path,
            ["pid"] = process?.Id,
            ["executable"] = exe,
            ["provider"] = "roblox-studio-launch"
        }));
    }

    public static string? FindRobloxStudioExecutable()
    {
        var env = Environment.GetEnvironmentVariable("ROBLOX_STUDIO_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Roblox", "Versions", "RobloxStudioBeta.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions", "RobloxStudioBeta.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions", "RobloxStudioBeta.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var versionsRoot = Path.Combine(localAppData, "Roblox", "Versions");
        if (Directory.Exists(versionsRoot))
        {
            var nested = Directory.EnumerateFiles(versionsRoot, "RobloxStudioBeta.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static AdapterResult MapBridgeResult(RobloxCommandResult result) =>
        result.Ok
            ? AdapterResult.Success(result.Data)
            : AdapterResult.Fail(result.ErrorCode ?? ErrorCodes.AdapterFailed, result.Message ?? "Roblox bridge command failed.");

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    public static string? GetString(Dictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => raw.ToString()
        };
    }

    public static int? GetInt(Dictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) => n,
            string s when int.TryParse(s, out var n) => n,
            _ => null
        };
    }

    public static int ClampInt(int? value, int fallback, int min, int max) =>
        Math.Clamp(value ?? fallback, min, max);

    private static IReadOnlyList<string>? GetStringArray(Dictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is IEnumerable<string> strings)
        {
            return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        if (raw is IEnumerable<object> objects)
        {
            return objects.Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        }

        return null;
    }
}
