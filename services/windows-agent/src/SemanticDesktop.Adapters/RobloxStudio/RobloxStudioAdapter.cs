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
                CommandNames.RobloxSetProperty,
                CommandNames.RobloxCreateInstance,
                CommandNames.RobloxDestroyInstance,
                CommandNames.RobloxCloneInstance,
                CommandNames.RobloxSetParent,
                CommandNames.RobloxFindInstances,
                CommandNames.RobloxGetScriptSource,
                CommandNames.RobloxSetScriptSource,
                CommandNames.RobloxBatch,
                CommandNames.RobloxPlaytestStart,
                CommandNames.RobloxPlaytestStop
            },
            Meta = new Dictionary<string, object?>
            {
                ["executable"] = exe,
                ["provider"] = "roblox-studio-plugin",
                ["bridgeListening"] = health.Listening,
                ["bridgePort"] = health.Port,
                ["pluginConnected"] = health.PluginConnected,
                ["startError"] = health.StartError,
                ["maxScriptSourceBytes"] = RobloxInstanceContract.MaxScriptSourceBytes,
                ["maxBatchOperations"] = RobloxInstanceContract.MaxBatchOperations
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
            CommandNames.RobloxCreateInstance => await CreateInstanceAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxDestroyInstance => await DestroyInstanceAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxCloneInstance => await CloneInstanceAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxSetParent => await SetParentAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxFindInstances => await FindInstancesAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxGetScriptSource => await GetScriptSourceAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxSetScriptSource => await SetScriptSourceAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxBatch => await BatchAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxPlaytestStart => await SimpleBridgeAsync(command, RobloxBridgeOperations.PlaytestStart, cancellationToken).ConfigureAwait(false),
            CommandNames.RobloxPlaytestStop => await SimpleBridgeAsync(command, RobloxBridgeOperations.PlaytestStop, cancellationToken).ConfigureAwait(false),
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

    private async Task<AdapterResult> CreateInstanceAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var className = GetString(command.Params, "className");
        if (!RobloxInstanceContract.IsCreatable(className))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "className is missing or not in the creatable allowlist.");
        }

        var parentId = GetString(command.Params, "parentId");
        var parentPath = GetString(command.Params, "parentPath");
        if (string.IsNullOrWhiteSpace(parentId) && string.IsNullOrWhiteSpace(parentPath))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "parentId or parentPath is required.");
        }

        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.CreateInstance,
            new Dictionary<string, object?>
            {
                ["className"] = className,
                ["parentId"] = parentId,
                ["parentPath"] = parentPath,
                ["name"] = GetString(command.Params, "name")
            },
            30,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> DestroyInstanceAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var instanceId = GetString(command.Params, "instanceId");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "instanceId is required.");
        }

        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.DestroyInstance,
            new Dictionary<string, object?> { ["instanceId"] = instanceId },
            20,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> CloneInstanceAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var instanceId = GetString(command.Params, "instanceId");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "instanceId is required.");
        }

        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.CloneInstance,
            new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["parentId"] = GetString(command.Params, "parentId"),
                ["parentPath"] = GetString(command.Params, "parentPath"),
                ["name"] = GetString(command.Params, "name")
            },
            30,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> SetParentAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var instanceId = GetString(command.Params, "instanceId");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "instanceId is required.");
        }

        var parentId = GetString(command.Params, "parentId");
        var parentPath = GetString(command.Params, "parentPath");
        if (string.IsNullOrWhiteSpace(parentId) && string.IsNullOrWhiteSpace(parentPath))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "parentId or parentPath is required.");
        }

        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.SetParent,
            new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["parentId"] = parentId,
                ["parentPath"] = parentPath
            },
            20,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> FindInstancesAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var maxResults = ClampInt(
            GetInt(command.Params, "maxResults"),
            RobloxInstanceContract.DefaultFindMaxResults,
            1,
            RobloxInstanceContract.MaxFindResults);

        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.FindInstances,
            new Dictionary<string, object?>
            {
                ["className"] = GetString(command.Params, "className"),
                ["nameContains"] = GetString(command.Params, "nameContains"),
                ["pathPrefix"] = GetString(command.Params, "pathPrefix"),
                ["rootPath"] = GetString(command.Params, "rootPath"),
                ["maxResults"] = maxResults
            },
            45,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> GetScriptSourceAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var instanceId = GetString(command.Params, "instanceId");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "instanceId is required.");
        }

        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.GetScriptSource,
            new Dictionary<string, object?> { ["instanceId"] = instanceId },
            30,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> SetScriptSourceAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var instanceId = GetString(command.Params, "instanceId");
        var source = GetString(command.Params, "source");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "instanceId is required.");
        }

        if (source is null)
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "source is required.");
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(source);
        if (byteCount > RobloxInstanceContract.MaxScriptSourceBytes)
        {
            return AdapterResult.Fail(
                ErrorCodes.InvalidArgument,
                $"source exceeds max of {RobloxInstanceContract.MaxScriptSourceBytes} bytes.");
        }

        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.SetScriptSource,
            new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["source"] = source
            },
            60,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> BatchAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        if (command.Params is null ||
            !command.Params.TryGetValue("operations", out var opsObj) ||
            opsObj is null)
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "operations array is required.");
        }

        var operations = NormalizeBatchOperations(opsObj);
        if (operations is null)
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "operations must be an array of {operation, params}.");
        }

        if (operations.Count > RobloxInstanceContract.MaxBatchOperations)
        {
            return AdapterResult.Fail(
                ErrorCodes.InvalidArgument,
                $"operations exceeds max of {RobloxInstanceContract.MaxBatchOperations}.");
        }

        foreach (var op in operations)
        {
            if (!op.TryGetValue("operation", out var nameObj) || nameObj is not string name ||
                !RobloxBridgeOperations.BatchAllowlist.Contains(name))
            {
                return AdapterResult.Fail(ErrorCodes.InvalidArgument, "batch contains an unsupported or missing operation.");
            }
        }

        var stopOnError = GetBool(command.Params, "stopOnError") ?? false;
        return await BridgeOpAsync(
            command,
            RobloxBridgeOperations.Batch,
            new Dictionary<string, object?>
            {
                ["operations"] = operations,
                ["stopOnError"] = stopOnError
            },
            120,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdapterResult> SimpleBridgeAsync(
        AdapterCommand command,
        string operation,
        CancellationToken cancellationToken) =>
        await BridgeOpAsync(command, operation, new Dictionary<string, object?>(), 30, cancellationToken)
            .ConfigureAwait(false);

    private async Task<AdapterResult> BridgeOpAsync(
        AdapterCommand command,
        string operation,
        Dictionary<string, object?> bridgeParams,
        int defaultTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var sessionId = GetString(command.Params, "sessionId");
        var timeout = TimeSpan.FromSeconds(ClampInt(GetInt(command.Params, "timeoutSeconds"), defaultTimeoutSeconds, 1, 180));
        var result = await _bridge.ExecuteCommandAsync(
            operation,
            bridgeParams,
            sessionId,
            timeout,
            cancellationToken).ConfigureAwait(false);
        return MapBridgeResult(result);
    }

    private static List<Dictionary<string, object?>>? NormalizeBatchOperations(object opsObj)
    {
        if (opsObj is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<Dictionary<string, object?>>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("operation", out var op) && op.ValueKind == JsonValueKind.String)
                {
                    dict["operation"] = op.GetString();
                }
                else if (item.TryGetProperty("op", out var op2) && op2.ValueKind == JsonValueKind.String)
                {
                    dict["operation"] = op2.GetString();
                }
                else
                {
                    return null;
                }

                if (item.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    var inner = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in p.EnumerateObject())
                    {
                        inner[prop.Name] = prop.Value.Clone();
                    }

                    dict["params"] = inner;
                }
                else
                {
                    dict["params"] = new Dictionary<string, object?>();
                }

                list.Add(dict);
            }

            return list;
        }

        if (opsObj is IEnumerable<object> enumerable)
        {
            var list = new List<Dictionary<string, object?>>();
            foreach (var item in enumerable)
            {
                if (item is Dictionary<string, object?> dict)
                {
                    list.Add(dict);
                }
                else
                {
                    return null;
                }
            }

            return list;
        }

        return null;
    }

    private static bool? GetBool(Dictionary<string, object?>? parameters, string name)
    {
        if (parameters is null || !parameters.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            JsonElement el when el.ValueKind is JsonValueKind.True or JsonValueKind.False => el.GetBoolean(),
            _ => null
        };
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
