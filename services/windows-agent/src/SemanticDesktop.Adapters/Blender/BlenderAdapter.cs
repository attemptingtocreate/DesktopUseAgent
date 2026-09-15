using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.Blender;

public sealed class BlenderAdapter : IApplicationAdapter
{
    public string Id => "blender";

    public bool CanHandle(ProcessInfo process) =>
        process.Name.Contains("blender", StringComparison.OrdinalIgnoreCase) ||
        (process.Path?.Contains("blender", StringComparison.OrdinalIgnoreCase) ?? false);

    public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var exe = FindBlenderExecutable();
        return Task.FromResult(new ApplicationCapabilities
        {
            AdapterId = Id,
            Available = exe is not null,
            Actions = new[]
            {
                CommandNames.BlenderOpen,
                CommandNames.BlenderGetScene,
                CommandNames.BlenderGetObjects,
                CommandNames.BlenderSelectObject,
                CommandNames.BlenderExecutePython,
                CommandNames.BlenderExport,
                CommandNames.BlenderSave
            },
            Meta = new Dictionary<string, object?>
            {
                ["executable"] = exe,
                ["provider"] = "blender-python"
            }
        });
    }

    public async Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        var exe = FindBlenderExecutable();
        if (exe is null)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "Blender executable not found.");
        }

        return command.Action.ToLowerInvariant() switch
        {
            CommandNames.BlenderOpen => await OpenAsync(exe, command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderGetScene => await RunJsonScriptAsync(exe, BuildSceneScript(), command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderGetObjects => await RunJsonScriptAsync(exe, BuildObjectsScript(), command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderSelectObject => await SelectObjectAsync(exe, command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderExecutePython => await ExecutePythonAsync(exe, command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderExport => await ExportAsync(exe, command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderSave => await SaveAsync(exe, command, cancellationToken).ConfigureAwait(false),
            _ => AdapterResult.Fail(ErrorCodes.Unsupported, $"Unknown blender action '{command.Action}'.")
        };
    }

    private static async Task<AdapterResult> OpenAsync(string exe, AdapterCommand command, CancellationToken ct)
    {
        var path = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "path to .blend file is required.");
        }

        var result = await RunBlenderAsync(exe, new[] { "--background", path, "--python-expr", "import bpy; print('OPEN_OK')" }, ct)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            return result;
        }

        return AdapterResult.Success(new { opened = path, provider = "blender-python" });
    }

    private static async Task<AdapterResult> SelectObjectAsync(string exe, AdapterCommand command, CancellationToken ct)
    {
        var name = GetString(command.Params, "name") ?? GetString(command.Params, "object");
        if (string.IsNullOrWhiteSpace(name))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "name is required.");
        }

        var blend = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        var script = $$"""
import bpy, json
name = {{JsonSerializer.Serialize(name)}}
obj = bpy.data.objects.get(name)
if obj is None:
    print("SD_JSON:" + json.dumps({"ok": False, "error": "not found", "name": name}))
else:
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    print("SD_JSON:" + json.dumps({"ok": True, "selected": name}))
""";
        return await RunJsonScriptAsync(exe, script, command, ct, blend).ConfigureAwait(false);
    }

    private static async Task<AdapterResult> ExecutePythonAsync(string exe, AdapterCommand command, CancellationToken ct)
    {
        var code = GetString(command.Params, "code") ?? GetString(command.Params, "python");
        if (string.IsNullOrWhiteSpace(code))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "code is required.");
        }

        var blend = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        var sb = new StringBuilder();
        sb.AppendLine("import json");
        sb.AppendLine("try:");
        sb.AppendLine(Indent(code!, 4));
        sb.AppendLine("    print(\"SD_JSON:\" + json.dumps({\"ok\": True}))");
        sb.AppendLine("except Exception as ex:");
        sb.AppendLine("    print(\"SD_JSON:\" + json.dumps({\"ok\": False, \"error\": str(ex)}))");
        return await RunJsonScriptAsync(exe, sb.ToString(), command, ct, blend).ConfigureAwait(false);
    }

    private static async Task<AdapterResult> ExportAsync(string exe, AdapterCommand command, CancellationToken ct)
    {
        var output = GetString(command.Params, "output") ?? GetString(command.Params, "destination");
        if (string.IsNullOrWhiteSpace(output))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "output path is required.");
        }

        var format = (GetString(command.Params, "format") ?? Path.GetExtension(output).TrimStart('.') ?? "obj").ToLowerInvariant();
        var blend = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        var exportCall = format switch
        {
            "fbx" => $"bpy.ops.export_scene.fbx(filepath={JsonSerializer.Serialize(output)})",
            "gltf" or "glb" => $"bpy.ops.export_scene.gltf(filepath={JsonSerializer.Serialize(output)})",
            "stl" => $"bpy.ops.export_mesh.stl(filepath={JsonSerializer.Serialize(output)})",
            _ => $"bpy.ops.wm.obj_export(filepath={JsonSerializer.Serialize(output)})"
        };

        var script = $$"""
import bpy, json
{{exportCall}}
print("SD_JSON:" + json.dumps({"ok": True, "exported": {{JsonSerializer.Serialize(output)}}, "format": {{JsonSerializer.Serialize(format)}}, "provider": "blender-python"}))
""";
        return await RunJsonScriptAsync(exe, script, command, ct, blend).ConfigureAwait(false);
    }

    private static async Task<AdapterResult> SaveAsync(string exe, AdapterCommand command, CancellationToken ct)
    {
        var blend = GetString(command.Params, "path") ?? GetString(command.Params, "file");
        var dest = GetString(command.Params, "destination") ?? blend;
        if (string.IsNullOrWhiteSpace(dest))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "path or destination is required for save.");
        }

        var script = $$"""
import bpy, json
bpy.ops.wm.save_as_mainfile(filepath={{JsonSerializer.Serialize(dest)}})
print("SD_JSON:" + json.dumps({"ok": True, "saved": {{JsonSerializer.Serialize(dest)}}, "provider": "blender-python"}))
""";
        return await RunJsonScriptAsync(exe, script, command, ct, blend).ConfigureAwait(false);
    }

    private static string BuildSceneScript() => """
import bpy, json
scene = bpy.context.scene
data = {
  "ok": True,
  "name": scene.name,
  "frame_current": scene.frame_current,
  "frame_start": scene.frame_start,
  "frame_end": scene.frame_end,
  "objects": len(bpy.data.objects),
  "provider": "blender-python"
}
print("SD_JSON:" + json.dumps(data))
""";

    private static string BuildObjectsScript() => """
import bpy, json
objs = [{"name": o.name, "type": o.type, "location": list(o.location)} for o in bpy.data.objects]
print("SD_JSON:" + json.dumps({"ok": True, "objects": objs, "provider": "blender-python"}))
""";

    private static async Task<AdapterResult> RunJsonScriptAsync(
        string exe,
        string script,
        AdapterCommand command,
        CancellationToken ct,
        string? blendPath = null)
    {
        blendPath ??= GetString(command.Params, "path") ?? GetString(command.Params, "file");
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
            var result = await RunBlenderAsync(exe, args, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                return result;
            }

            var stdout = result.Data as string ?? "";
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
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private static async Task<AdapterResult> RunBlenderAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Blender.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                return AdapterResult.Fail(ErrorCodes.AdapterFailed, $"Blender exit {proc.ExitCode}: {Truncate(stderr + stdout)}");
            }

            return AdapterResult.Success(stdout);
        }
        catch (OperationCanceledException)
        {
            return AdapterResult.Fail(ErrorCodes.Cancelled, "Blender invocation cancelled.");
        }
        catch (Exception ex)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, ex.Message);
        }
    }

    public static string? FindBlenderExecutable()
    {
        var env = Environment.GetEnvironmentVariable("BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var name in new[] { "blender", "blender.exe" })
        {
            var onPath = FindOnPath(name);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var blenderRoot = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(blenderRoot))
            {
                continue;
            }

            var hit = Directory.GetFiles(blenderRoot, "blender.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore invalid PATH entries
            }
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

    private static string Indent(string code, int spaces)
    {
        var pad = new string(' ', spaces);
        var sb = new StringBuilder();
        foreach (var line in code.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append(pad).AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text) =>
        text.Length <= 800 ? text : text[..800] + "…";
}
