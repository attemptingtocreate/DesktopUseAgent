using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.Blender;

public sealed class BlenderAdapter : IApplicationAdapter
{
    public const int MaxBatchOperations = 32;

    private readonly IBlenderBridge? _bridge;
    private readonly IBlenderProcessRunner _runner;

    public BlenderAdapter(IBlenderBridge? bridge = null, IBlenderProcessRunner? runner = null)
    {
        _bridge = bridge;
        _runner = runner ?? new DefaultBlenderProcessRunner();
    }

    public string Id => "blender";

    public bool CanHandle(ProcessInfo process) =>
        process.Name.Contains("blender", StringComparison.OrdinalIgnoreCase) ||
        (process.Path?.Contains("blender", StringComparison.OrdinalIgnoreCase) ?? false);

    public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var exe = BlenderExecutableCache.Get();
        var health = _bridge?.GetHealth();
        return Task.FromResult(new ApplicationCapabilities
        {
            AdapterId = Id,
            Available = exe is not null || health?.Listening == true,
            Actions = new[]
            {
                CommandNames.BlenderOpen,
                CommandNames.BlenderGetScene,
                CommandNames.BlenderGetObjects,
                CommandNames.BlenderSelectObject,
                CommandNames.BlenderExecutePython,
                CommandNames.BlenderExport,
                CommandNames.BlenderSave,
                CommandNames.BlenderBatch,
                CommandNames.BlenderRender,
                CommandNames.BlenderImportMesh
            },
            Meta = new Dictionary<string, object?>
            {
                ["executable"] = exe,
                ["provider"] = "blender-python",
                ["backgroundAvailable"] = exe is not null,
                ["liveBridgeListening"] = health?.Listening ?? false,
                ["liveSessionConnected"] = health?.AddonConnected ?? false,
                ["liveBridgePort"] = health?.Port,
                ["liveBridgeStartError"] = health?.StartError
            }
        });
    }

    public async Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        return command.Action.ToLowerInvariant() switch
        {
            CommandNames.BlenderOpen => await OpenAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderGetScene => await RouteAsync(
                BlenderBridgeOperations.GetScene,
                () => RunBackgroundScriptAsync(BlenderScriptBuilder.BuildSceneScript(), command, cancellationToken),
                command,
                cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderGetObjects => await RouteAsync(
                BlenderBridgeOperations.GetObjects,
                () => RunBackgroundScriptAsync(BlenderScriptBuilder.BuildObjectsScript(), command, cancellationToken),
                command,
                cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderSelectObject => await SelectObjectAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderExecutePython => await ExecutePythonAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderExport => await ExportAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderSave => await SaveAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderBatch => await BatchAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderRender => await RenderAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderImportMesh => await ImportMeshAsync(command, cancellationToken).ConfigureAwait(false),
            _ => AdapterResult.Fail(ErrorCodes.Unsupported, $"Unknown blender action '{command.Action}'.")
        };
    }

    public static string? FindBlenderExecutable() => BlenderExecutableCache.Get();

    private async Task<AdapterResult> OpenAsync(AdapterCommand command, CancellationToken ct)
    {
        var exe = RequireExecutable();
        if (exe.Error is not null)
        {
            return exe.Error;
        }

        var path = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "path to .blend file is required.");
        }

        if (!path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "path must be a .blend file.");
        }

        var mode = (GetString(command.Params, "mode") ?? "background").ToLowerInvariant();
        if (mode is "gui" or "live")
        {
            var gui = await _runner.LaunchGuiAsync(exe.Value!, path, ct).ConfigureAwait(false);
            if (!gui.Ok)
            {
                return AdapterResult.Fail(ErrorCodes.AdapterFailed, gui.ErrorMessage ?? "Blender GUI launch failed.");
            }

            return AdapterResult.Success(new { opened = path, mode = "gui", provider = "blender-gui" });
        }

        var result = await _runner.RunAsync(
                exe.Value!,
                new[] { "--background", path, "--python-expr", "import bpy; print('OPEN_OK')" },
                ct)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, result.ErrorMessage ?? "Blender open failed.");
        }

        return AdapterResult.Success(new { opened = path, mode = "background", provider = "blender-python" });
    }

    private async Task<AdapterResult> SelectObjectAsync(AdapterCommand command, CancellationToken ct)
    {
        var name = GetString(command.Params, "name") ?? GetString(command.Params, "object");
        if (string.IsNullOrWhiteSpace(name))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "name is required.");
        }

        return await RouteAsync(
            BlenderBridgeOperations.SelectObject,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildSelectObjectScript(name),
                command,
                ct),
            command,
            ct,
            new Dictionary<string, object?> { ["name"] = name }).ConfigureAwait(false);
    }

    private async Task<AdapterResult> ExecutePythonAsync(AdapterCommand command, CancellationToken ct)
    {
        var code = GetString(command.Params, "code") ?? GetString(command.Params, "python");
        if (string.IsNullOrWhiteSpace(code))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "code is required.");
        }

        var exe = RequireExecutable();
        if (exe.Error is not null)
        {
            return exe.Error;
        }

        return await RunBackgroundScriptAsync(BlenderScriptBuilder.BuildExecutePythonScript(code!), command, ct)
            .ConfigureAwait(false);
    }

    private async Task<AdapterResult> ExportAsync(AdapterCommand command, CancellationToken ct)
    {
        var output = GetString(command.Params, "output") ?? GetString(command.Params, "destination");
        var overwrite = GetBool(command.Params, "overwrite") ?? false;
        var format = (GetString(command.Params, "format") ?? Path.GetExtension(output ?? "").TrimStart('.') ?? "obj").ToLowerInvariant();
        var validation = BlenderPathValidation.ValidateExportFormat(format)
                         ?? BlenderPathValidation.ValidateOutputFile(output!, overwrite, BlenderPathValidation.ExportExtensions);
        if (validation is not null)
        {
            return validation;
        }

        BlenderPathValidation.EnsureParentDirectory(output!);
        var bridgeParams = new Dictionary<string, object?>
        {
            ["output"] = output,
            ["format"] = format,
            ["overwrite"] = overwrite
        };
        return await RouteAsync(
            BlenderBridgeOperations.Export,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildExportScript(output!, format),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> SaveAsync(AdapterCommand command, CancellationToken ct)
    {
        var dest = GetString(command.Params, "destination")
                   ?? GetString(command.Params, "path")
                   ?? GetString(command.Params, "file");
        if (string.IsNullOrWhiteSpace(dest))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "destination is required for save.");
        }

        var overwrite = GetBool(command.Params, "overwrite") ?? false;
        var validation = BlenderPathValidation.ValidateBlendDestination(dest, overwrite);
        if (validation is not null)
        {
            return validation;
        }

        BlenderPathValidation.EnsureParentDirectory(dest);
        var bridgeParams = new Dictionary<string, object?> { ["destination"] = dest, ["overwrite"] = overwrite };
        return await RouteAsync(
            BlenderBridgeOperations.Save,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildSaveScript(dest),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> RenderAsync(AdapterCommand command, CancellationToken ct)
    {
        var output = GetString(command.Params, "output");
        if (string.IsNullOrWhiteSpace(output))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "output is required.");
        }

        var overwrite = GetBool(command.Params, "overwrite") ?? false;
        var validation = BlenderPathValidation.ValidateOutputFile(
            output,
            overwrite,
            BlenderPathValidation.RenderOutputExtensions);
        if (validation is not null)
        {
            return validation;
        }

        var engine = GetString(command.Params, "engine");
        if (!string.IsNullOrWhiteSpace(engine) && !BlenderRenderEngines.Allowlist.Contains(engine))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"engine '{engine}' is not allowlisted.");
        }

        var frame = GetInt(command.Params, "frame");
        if (frame is <= 0)
        {
            frame = null;
        }

        var animation = GetBool(command.Params, "animation") ?? false;
        if (animation)
        {
            return AdapterResult.Fail(ErrorCodes.Unsupported, "animation render is not supported in this phase.");
        }

        BlenderPathValidation.EnsureParentDirectory(output!);
        var blendPath = GetOptionalBlendFile(command.Params);
        var bridgeParams = new Dictionary<string, object?>
        {
            ["output"] = output,
            ["engine"] = engine,
            ["frame"] = frame,
            ["animation"] = false,
            ["overwrite"] = overwrite
        };

        return await RouteAsync(
            BlenderBridgeOperations.Render,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildRenderScript(output!, engine, frame, animation),
                command,
                ct,
                blendPath),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> ImportMeshAsync(AdapterCommand command, CancellationToken ct)
    {
        var input = GetString(command.Params, "path") ?? GetString(command.Params, "input");
        var abs = BlenderPathValidation.ValidateAbsolutePath(input, "path");
        if (abs is not null)
        {
            return abs;
        }

        if (!File.Exists(input!))
        {
            return AdapterResult.Fail(ErrorCodes.NotFound, $"Mesh file not found: {input}");
        }

        var format = (GetString(command.Params, "format") ?? Path.GetExtension(input!).TrimStart('.') ?? "").ToLowerInvariant();
        var formatError = BlenderPathValidation.ValidateExportFormat(format);
        if (formatError is not null)
        {
            return formatError;
        }

        var objectName = GetString(command.Params, "name") ?? GetString(command.Params, "objectName");
        var collection = GetString(command.Params, "collection") ?? GetString(command.Params, "collectionName");
        var saveAs = GetString(command.Params, "saveAs");
        var saveOverwrite = GetBool(command.Params, "overwrite") ?? false;
        if (!string.IsNullOrWhiteSpace(saveAs))
        {
            var saveValidation = BlenderPathValidation.ValidateBlendDestination(saveAs, saveOverwrite);
            if (saveValidation is not null)
            {
                return saveValidation;
            }
        }

        var blendPath = GetOptionalBlendFile(command.Params);
        var bridgeParams = new Dictionary<string, object?>
        {
            ["path"] = input,
            ["format"] = format,
            ["name"] = objectName,
            ["collection"] = collection,
            ["saveAs"] = saveAs
        };

        var result = await RouteAsync(
            BlenderBridgeOperations.ImportMesh,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildImportMeshScript(input!, format, objectName, collection),
                command,
                ct,
                blendPath),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);

        if (!result.Ok || string.IsNullOrWhiteSpace(saveAs))
        {
            return result;
        }

        BlenderPathValidation.EnsureParentDirectory(saveAs);
        return await SaveAsync(new AdapterCommand
        {
            Action = CommandNames.BlenderSave,
            Params = new Dictionary<string, object?>
            {
                ["file"] = GetString(command.Params, "file") ?? GetString(command.Params, "blendFile"),
                ["destination"] = saveAs,
                ["overwrite"] = saveOverwrite
            },
            ProcessId = command.ProcessId,
            WindowId = command.WindowId
        }, ct).ConfigureAwait(false);
    }

    private async Task<AdapterResult> BatchAsync(AdapterCommand command, CancellationToken ct)
    {
        var exe = RequireExecutable();
        if (exe.Error is not null)
        {
            return exe.Error;
        }

        var operations = ParseOperations(command.Params);
        if (operations.Count is < 1 or > MaxBatchOperations)
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"operations must contain 1..{MaxBatchOperations} items.");
        }

        foreach (var op in operations)
        {
            var opName = GetString(op, "op") ?? GetString(op, "operation");
            if (string.IsNullOrWhiteSpace(opName) || !BlenderBridgeOperations.BatchAllowlist.Contains(opName))
            {
                return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"operation '{opName}' is not allowlisted for batch.");
            }
        }

        var failFast = GetBool(command.Params, "failFast") ?? true;
        var started = Stopwatch.GetTimestamp();
        var script = BlenderScriptBuilder.BuildBatchScript(operations, failFast);
        var result = await RunBackgroundScriptAsync(script, command, ct, GetOptionalBlendFile(command.Params)).ConfigureAwait(false);
        if (!result.Ok)
        {
            return result;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (result.Data is Dictionary<string, object?> dict)
        {
            dict["totalMs"] = elapsedMs;
            dict["provider"] = "blender-python";
            return AdapterResult.Success(dict);
        }

        return AdapterResult.Success(new { data = result.Data, totalMs = elapsedMs, provider = "blender-python" });
    }

    private async Task<AdapterResult> RouteAsync(
        string bridgeOperation,
        Func<Task<AdapterResult>> background,
        AdapterCommand command,
        CancellationToken ct,
        Dictionary<string, object?>? bridgeParams = null)
    {
        var mode = ResolveMode(command);
        if (mode == BlenderExecutionMode.Live)
        {
            return await ExecuteLiveAsync(bridgeOperation, bridgeParams ?? command.Params, command, ct).ConfigureAwait(false);
        }

        if (mode == BlenderExecutionMode.Background)
        {
            return await background().ConfigureAwait(false);
        }

        if (mode == BlenderExecutionMode.Auto && CanUseLiveBridge(command))
        {
            var live = await ExecuteLiveAsync(bridgeOperation, bridgeParams ?? command.Params, command, ct).ConfigureAwait(false);
            if (live.Ok || live.ErrorCode is not (ErrorCodes.AdapterUnavailable or ErrorCodes.Timeout))
            {
                return live;
            }
        }

        return await background().ConfigureAwait(false);
    }

    private async Task<AdapterResult> ExecuteLiveAsync(
        string operation,
        Dictionary<string, object?>? parameters,
        AdapterCommand command,
        CancellationToken ct)
    {
        if (_bridge is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "Blender live bridge is not configured.");
        }

        var sessionId = GetString(command.Params, "sessionId");
        var timeoutSeconds = GetInt(command.Params, "timeoutSeconds") ?? 30;
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 120);
        var result = await _bridge.ExecuteCommandAsync(
            operation,
            parameters,
            sessionId,
            TimeSpan.FromSeconds(timeoutSeconds),
            ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            return AdapterResult.Fail(result.ErrorCode ?? ErrorCodes.AdapterFailed, result.Message ?? "Live bridge failed.");
        }

        return AdapterResult.Success(result.Data ?? new { ok = true, provider = "blender-live" });
    }

    private bool CanUseLiveBridge(AdapterCommand command)
    {
        if (_bridge is null)
        {
            return false;
        }

        var health = _bridge.GetHealth();
        if (!health.Listening || !health.AddonConnected)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(command.ProcessId)
               || !string.IsNullOrWhiteSpace(command.WindowId)
               || !string.IsNullOrWhiteSpace(GetString(command.Params, "sessionId"));
    }

    private static BlenderExecutionMode ResolveMode(AdapterCommand command)
    {
        var raw = GetString(command.Params, "mode") ?? "auto";
        return raw.ToLowerInvariant() switch
        {
            "live" => BlenderExecutionMode.Live,
            "background" => BlenderExecutionMode.Background,
            _ => BlenderExecutionMode.Auto
        };
    }

    private async Task<AdapterResult> RunBackgroundScriptAsync(
        string script,
        AdapterCommand command,
        CancellationToken ct,
        string? blendPath = null)
    {
        var exe = RequireExecutable();
        if (exe.Error is not null)
        {
            return exe.Error;
        }

        blendPath ??= GetOptionalBlendFile(command.Params);
        var temp = Path.Combine(Path.GetTempPath(), "sd-blender-" + Guid.NewGuid().ToString("N") + ".py");
        await File.WriteAllTextAsync(temp, script, ct).ConfigureAwait(false);
        try
        {
            var args = new List<string> { "--background" };
            if (!string.IsNullOrWhiteSpace(blendPath))
            {
                if (!File.Exists(blendPath))
                {
                    return AdapterResult.Fail(ErrorCodes.NotFound, $"Blend file not found: {blendPath}");
                }

                args.Add(blendPath);
            }

            args.Add("--python");
            args.Add(temp);
            var result = await _runner.RunAsync(exe.Value!, args, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                return AdapterResult.Fail(ErrorCodes.AdapterFailed, Truncate(result.ErrorMessage ?? result.Stderr + result.Stdout));
            }

            return ParseJsonResult(result.Stdout);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private static AdapterResult ParseJsonResult(string stdout)
    {
        var jsonLine = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("SD_JSON:", StringComparison.Ordinal));
        if (jsonLine is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, "Blender produced no structured result.\n" + Truncate(stdout));
        }

        using var doc = JsonDocument.Parse(jsonLine["SD_JSON:".Length..]);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, root.TryGetProperty("error", out var err) ? err.GetString() ?? "failed" : "failed");
        }

        return AdapterResult.Success(JsonSerializer.Deserialize<object>(root.GetRawText()));
    }

    private static (string? Value, AdapterResult? Error) RequireExecutable()
    {
        var exe = BlenderExecutableCache.Get();
        return exe is null
            ? (null, AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "Blender executable not found."))
            : (exe, null);
    }

    private static List<Dictionary<string, object?>> ParseOperations(Dictionary<string, object?>? parameters)
    {
        var list = new List<Dictionary<string, object?>>();
        if (parameters is null || !parameters.TryGetValue("operations", out var raw) || raw is null)
        {
            return list;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in je.EnumerateArray())
            {
                if (TryParseOperationObject(item, out var dict))
                {
                    list.Add(dict!);
                }
            }

            return list;
        }

        if (raw is IEnumerable<object?> enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is Dictionary<string, object?> dict)
                {
                    list.Add(new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase));
                    continue;
                }

                if (item is JsonElement jeItem && TryParseOperationObject(jeItem, out var parsed))
                {
                    list.Add(parsed!);
                }
            }
        }

        return list;
    }

    private static bool TryParseOperationObject(JsonElement item, out Dictionary<string, object?>? dict)
    {
        dict = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in item.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt32(out var n) ? n : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.GetRawText()
            };
        }

        return true;
    }

    private static string? GetOptionalBlendFile(Dictionary<string, object?>? parameters)
    {
        var file = GetString(parameters, "file") ?? GetString(parameters, "blendFile");
        if (!string.IsNullOrWhiteSpace(file))
        {
            return file;
        }

        var path = GetString(parameters, "path");
        if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return null;
    }

    private static string? GetString(Dictionary<string, object?>? parameters, string key)
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

    private static bool? GetBool(Dictionary<string, object?>? parameters, string key)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            JsonElement je when je.ValueKind is JsonValueKind.True or JsonValueKind.False => je.GetBoolean(),
            string s when bool.TryParse(s, out var b) => b,
            _ => null
        };
    }

    private static int? GetInt(Dictionary<string, object?>? parameters, string key)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n) => n,
            string s when int.TryParse(s, out var n) => n,
            _ => null
        };
    }

    private static string Truncate(string text) =>
        text.Length <= 800 ? text : text[..800] + "…";
}

internal enum BlenderExecutionMode
{
    Auto,
    Live,
    Background
}
