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
                CommandNames.BlenderImportMesh,
                CommandNames.BlenderCreateMesh,
                CommandNames.BlenderExportForRoblox,
                CommandNames.BlenderMeshExtrude,
                CommandNames.BlenderMeshInset,
                CommandNames.BlenderMeshBevel,
                CommandNames.BlenderMeshLoopCut,
                CommandNames.BlenderModifierBoolean,
                CommandNames.BlenderModifierMirror,
                CommandNames.BlenderModifierArray,
                CommandNames.BlenderMaterialSet,
                CommandNames.BlenderUvUnwrap,
                CommandNames.BlenderSelectGeometry,
                CommandNames.BlenderAnimationApply, CommandNames.BlenderAnimationInspect,
                CommandNames.BlenderAnimationPreview, CommandNames.BlenderAssetValidate
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
            CommandNames.BlenderCreateMesh => await CreateMeshAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderExportForRoblox => await ExportForRobloxAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderMeshExtrude => await MeshExtrudeAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderMeshInset => await MeshInsetAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderMeshBevel => await MeshBevelAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderMeshLoopCut => await MeshLoopCutAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderModifierBoolean => await ModifierBooleanAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderModifierMirror => await ModifierMirrorAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderModifierArray => await ModifierArrayAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderMaterialSet => await MaterialSetAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderUvUnwrap => await UvUnwrapAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderSelectGeometry => await SelectGeometryAsync(command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderAnimationApply => await ExecuteLiveAsync(BlenderBridgeOperations.AnimationApply, command.Params, command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderAnimationInspect => await ExecuteLiveAsync(BlenderBridgeOperations.AnimationInspect, command.Params, command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderAnimationPreview => await ExecuteLiveAsync(BlenderBridgeOperations.AnimationPreview, command.Params, command, cancellationToken).ConfigureAwait(false),
            CommandNames.BlenderAssetValidate => await ExecuteLiveAsync(BlenderBridgeOperations.AssetValidate, command.Params, command, cancellationToken).ConfigureAwait(false),
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
        var code = GetString(command.Params, "code")
                   ?? GetString(command.Params, "python")
                   ?? GetString(command.Params, "source");
        if (string.IsNullOrWhiteSpace(code))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "code is required.");
        }

        var mode = ResolveMode(command);
        var preferLive = mode == BlenderExecutionMode.Live
                         || (mode == BlenderExecutionMode.Auto && CanUseLiveBridge(command));
        if (preferLive)
        {
            var confirm = GetBool(command.Params, "confirm") ?? false;
            if (!BlenderMeshOpsContract.TryValidateExecutePython(code, confirm, out var error))
            {
                return AdapterResult.Fail(ErrorCodes.InvalidArgument, error ?? "invalid execute_python request.");
            }

            var bridgeParams = new Dictionary<string, object?>
            {
                ["source"] = code,
                ["code"] = code,
                ["confirm"] = true
            };
            return await ExecuteLiveAsync(BlenderBridgeOperations.ExecutePython, bridgeParams, command, ct)
                .ConfigureAwait(false);
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

    private static readonly HashSet<string> CreateMeshKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "cube", "uv_sphere", "ico_sphere", "cylinder", "cone", "plane", "torus"
    };

    private async Task<AdapterResult> CreateMeshAsync(AdapterCommand command, CancellationToken ct)
    {
        var kind = (GetString(command.Params, "kind") ?? GetString(command.Params, "primitive") ?? "cube").ToLowerInvariant();
        if (!CreateMeshKinds.Contains(kind))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"kind must be one of: {string.Join(", ", CreateMeshKinds)}.");
        }

        var name = GetString(command.Params, "name");
        var location = GetDoubleArray(command.Params, "location", 3) ?? new[] { 0d, 0d, 0d };
        var scale = GetDoubleArray(command.Params, "scale", 3) ?? new[] { 1d, 1d, 1d };
        var size = GetDouble(command.Params, "size");
        var bridgeParams = new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["name"] = name,
            ["location"] = location,
            ["scale"] = scale,
            ["size"] = size
        };
        return await RouteAsync(
            BlenderBridgeOperations.CreateMesh,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildCreateMeshScript(kind, name, location, scale, size),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> ExportForRobloxAsync(AdapterCommand command, CancellationToken ct)
    {
        var output = GetString(command.Params, "output") ?? GetString(command.Params, "destination");
        var overwrite = GetBool(command.Params, "overwrite") ?? false;
        var format = (GetString(command.Params, "format") ?? "fbx").ToLowerInvariant();
        if (format is not ("fbx" or "obj" or "gltf" or "glb"))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "export_for_roblox format must be fbx, obj, gltf, or glb.");
        }

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
            BlenderBridgeOperations.ExportForRoblox,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildExportForRobloxScript(output!, format),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> MeshExtrudeAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var mode = ResolveGeometryMode(command.Params, "FACE");
        if (!BlenderMeshOpsContract.GeometryModes.Contains(mode))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "mode must be VERT, EDGE, or FACE.");
        }

        var value = GetDouble(command.Params, "value") ?? GetDouble(command.Params, "offset") ?? 0d;
        var indices = GetIntList(command.Params, "indices") ?? GetIntList(command.Params, "index");
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["mode"] = mode,
            ["value"] = value,
            ["indices"] = indices
        };
        return await RouteAsync(
            BlenderBridgeOperations.MeshExtrude,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildMeshExtrudeScript(objectName.Value!, mode, value, indices),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> MeshInsetAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var thickness = GetDouble(command.Params, "thickness") ?? 0.1;
        var depth = GetDouble(command.Params, "depth") ?? 0d;
        var indices = GetIntList(command.Params, "indices") ?? GetIntList(command.Params, "index");
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["thickness"] = thickness,
            ["depth"] = depth,
            ["indices"] = indices
        };
        return await RouteAsync(
            BlenderBridgeOperations.MeshInset,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildMeshInsetScript(objectName.Value!, thickness, depth, indices),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> MeshBevelAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var mode = ResolveGeometryMode(command.Params, "EDGE");
        if (!BlenderMeshOpsContract.GeometryModes.Contains(mode))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "mode must be VERT, EDGE, or FACE.");
        }

        var offset = GetDouble(command.Params, "offset") ?? GetDouble(command.Params, "width") ?? 0.05;
        var segments = Math.Max(1, GetInt(command.Params, "segments") ?? 1);
        var indices = GetIntList(command.Params, "indices") ?? GetIntList(command.Params, "index");
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["mode"] = mode,
            ["offset"] = offset,
            ["segments"] = segments,
            ["indices"] = indices
        };
        return await RouteAsync(
            BlenderBridgeOperations.MeshBevel,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildMeshBevelScript(objectName.Value!, mode, offset, segments, indices),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> MeshLoopCutAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var cuts = Math.Clamp(GetInt(command.Params, "cuts") ?? 1, 1, 64);
        var edgeIndex = GetInt(command.Params, "edgeIndex");
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["cuts"] = cuts,
            ["edgeIndex"] = edgeIndex
        };
        return await RouteAsync(
            BlenderBridgeOperations.MeshLoopCut,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildMeshLoopCutScript(objectName.Value!, cuts, edgeIndex),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> ModifierBooleanAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var target = GetString(command.Params, "target") ?? GetString(command.Params, "operand");
        if (string.IsNullOrWhiteSpace(target))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "target mesh object is required.");
        }

        var operation = (GetString(command.Params, "operation") ?? "DIFFERENCE").ToUpperInvariant();
        if (!BlenderMeshOpsContract.BooleanOperations.Contains(operation))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "operation must be UNION, DIFFERENCE, or INTERSECT.");
        }

        var apply = GetBool(command.Params, "apply") ?? true;
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["target"] = target,
            ["operation"] = operation,
            ["apply"] = apply,
            ["modifierName"] = GetString(command.Params, "modifierName")
        };
        return await RouteAsync(
            BlenderBridgeOperations.ModifierBoolean,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildModifierBooleanScript(objectName.Value!, target!, operation, apply),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> ModifierMirrorAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var axis = (GetString(command.Params, "axis") ?? "X").ToUpperInvariant();
        if (!BlenderMeshOpsContract.MirrorAxes.Contains(axis))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "axis must be X, Y, or Z.");
        }

        var apply = GetBool(command.Params, "apply") ?? true;
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["axis"] = axis,
            ["apply"] = apply,
            ["modifierName"] = GetString(command.Params, "modifierName")
        };
        return await RouteAsync(
            BlenderBridgeOperations.ModifierMirror,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildModifierMirrorScript(objectName.Value!, axis, apply),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> ModifierArrayAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var count = Math.Clamp(GetInt(command.Params, "count") ?? 2, 2, 64);
        var relative = GetDoubleArray(command.Params, "relativeOffset", 3)
                       ?? GetDoubleArray(command.Params, "offset", 3)
                       ?? new[] { 1d, 0d, 0d };
        var apply = GetBool(command.Params, "apply") ?? true;
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["count"] = count,
            ["relativeOffset"] = relative,
            ["apply"] = apply,
            ["modifierName"] = GetString(command.Params, "modifierName")
        };
        return await RouteAsync(
            BlenderBridgeOperations.ModifierArray,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildModifierArrayScript(objectName.Value!, count, relative, apply),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> MaterialSetAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var material = GetString(command.Params, "material") ?? GetString(command.Params, "materialName");
        var color = GetDoubleArray(command.Params, "color", 4)
                    ?? GetDoubleArray(command.Params, "color", 3)
                    ?? GetDoubleArray(command.Params, "baseColor", 4)
                    ?? GetDoubleArray(command.Params, "baseColor", 3)
                    ?? new[] { 0.8, 0.8, 0.8, 1.0 };
        if (color.Length == 3)
        {
            color = new[] { color[0], color[1], color[2], 1.0 };
        }

        var roughness = GetDouble(command.Params, "roughness");
        var metallic = GetDouble(command.Params, "metallic");
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["material"] = material,
            ["materialName"] = material,
            ["color"] = color,
            ["roughness"] = roughness,
            ["metallic"] = metallic
        };
        return await RouteAsync(
            BlenderBridgeOperations.MaterialSet,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildMaterialSetScript(objectName.Value!, material, color, roughness, metallic),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> UvUnwrapAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var method = (GetString(command.Params, "method") ?? "ANGLE_BASED").ToUpperInvariant();
        if (!BlenderMeshOpsContract.UvMethods.Contains(method))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "method must be ANGLE_BASED, CONFORMAL, or SMART.");
        }

        var margin = GetDouble(command.Params, "margin") ?? 0.001;
        var angleLimit = GetDouble(command.Params, "angleLimit") ?? 66.0;
        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["method"] = method,
            ["margin"] = margin,
            ["angleLimit"] = angleLimit
        };
        return await RouteAsync(
            BlenderBridgeOperations.UvUnwrap,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildUvUnwrapScript(objectName.Value!, method, margin, angleLimit),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private async Task<AdapterResult> SelectGeometryAsync(AdapterCommand command, CancellationToken ct)
    {
        var objectName = RequireObjectName(command.Params);
        if (objectName.Error is not null)
        {
            return objectName.Error;
        }

        var mode = ResolveGeometryMode(command.Params, "FACE");
        if (!BlenderMeshOpsContract.GeometryModes.Contains(mode))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "mode must be VERT, EDGE, or FACE.");
        }

        var selectAll = GetBool(command.Params, "selectAll") ?? false;
        var indices = GetIntList(command.Params, "indices") ?? GetIntList(command.Params, "index");
        if (!selectAll && (indices is null || indices.Count == 0))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "indices or selectAll required.");
        }

        var bridgeParams = new Dictionary<string, object?>
        {
            ["name"] = objectName.Value,
            ["object"] = objectName.Value,
            ["mode"] = mode,
            ["selectAll"] = selectAll,
            ["indices"] = indices
        };
        return await RouteAsync(
            BlenderBridgeOperations.SelectGeometry,
            () => RunBackgroundScriptAsync(
                BlenderScriptBuilder.BuildSelectGeometryScript(objectName.Value!, mode, selectAll, indices),
                command,
                ct,
                GetOptionalBlendFile(command.Params)),
            command,
            ct,
            bridgeParams).ConfigureAwait(false);
    }

    private static string ResolveGeometryMode(Dictionary<string, object?>? parameters, string fallback)
    {
        var raw = GetString(parameters, "element")
                  ?? GetString(parameters, "geometryMode")
                  ?? GetString(parameters, "mode");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var upper = raw.ToUpperInvariant();
        return upper is "AUTO" or "LIVE" or "BACKGROUND" ? fallback : upper;
    }

    private static (string? Value, AdapterResult? Error) RequireObjectName(Dictionary<string, object?>? parameters)
    {
        var name = GetString(parameters, "name") ?? GetString(parameters, "object");
        return string.IsNullOrWhiteSpace(name)
            ? (null, AdapterResult.Fail(ErrorCodes.InvalidArgument, "name (object) is required."))
            : (name, null);
    }

    private static List<int>? GetIntList(Dictionary<string, object?>? parameters, string name)
    {
        if (parameters is null || !parameters.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is int i)
        {
            return new List<int> { i };
        }

        if (value is long l)
        {
            return new List<int> { (int)l };
        }

        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            {
                return new List<int> { n };
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                var list = new List<int>();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var v))
                    {
                        list.Add(v);
                    }
                }

                return list;
            }
        }

        if (value is IEnumerable<object> objs)
        {
            var list = new List<int>();
            foreach (var item in objs)
            {
                switch (item)
                {
                    case int ii:
                        list.Add(ii);
                        break;
                    case long ll:
                        list.Add((int)ll);
                        break;
                    case JsonElement je when je.TryGetInt32(out var n):
                        list.Add(n);
                        break;
                }
            }

            return list;
        }

        return null;
    }

    private static double[]? GetDoubleArray(Dictionary<string, object?>? parameters, string name, int expectedLength)
    {
        if (parameters is null || !parameters.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<double>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var n))
                {
                    list.Add(n);
                }
            }

            return list.Count == expectedLength ? list.ToArray() : null;
        }

        if (value is IEnumerable<object> objs)
        {
            var list = new List<double>();
            foreach (var item in objs)
            {
                if (item is double d) list.Add(d);
                else if (item is int i) list.Add(i);
                else if (item is JsonElement je && je.TryGetDouble(out var n)) list.Add(n);
            }

            return list.Count == expectedLength ? list.ToArray() : null;
        }

        return null;
    }

    private static double? GetDouble(Dictionary<string, object?>? parameters, string name)
    {
        if (parameters is null || !parameters.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n) => n,
            _ => null
        };
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
