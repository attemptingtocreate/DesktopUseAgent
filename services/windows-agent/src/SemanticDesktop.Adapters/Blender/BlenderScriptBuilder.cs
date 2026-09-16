using System.Text;
using System.Text.Json;

namespace SemanticDesktop.Adapters.Blender;

internal static class BlenderScriptBuilder
{
    public static string BuildSceneScript() => """
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

    public static string BuildObjectsScript() => """
import bpy, json
objs = [{"name": o.name, "type": o.type, "location": list(o.location)} for o in bpy.data.objects]
print("SD_JSON:" + json.dumps({"ok": True, "objects": objs, "provider": "blender-python"}))
""";

    public static string BuildSelectObjectScript(string name) => $$"""
import bpy, json
name = {{JsonSerializer.Serialize(name)}}
obj = bpy.data.objects.get(name)
if obj is None:
    print("SD_JSON:" + json.dumps({"ok": False, "error": "not found", "name": name}))
else:
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    print("SD_JSON:" + json.dumps({"ok": True, "selected": name, "provider": "blender-python"}))
""";

    public static string BuildExecutePythonScript(string code)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import json");
        sb.AppendLine("try:");
        sb.AppendLine(Indent(code, 4));
        sb.AppendLine("    print(\"SD_JSON:\" + json.dumps({\"ok\": True, \"provider\": \"blender-python\"}))");
        sb.AppendLine("except Exception as ex:");
        sb.AppendLine("    print(\"SD_JSON:\" + json.dumps({\"ok\": False, \"error\": str(ex)}))");
        return sb.ToString();
    }

    public static string BuildExportScript(string output, string format)
    {
        var exportCall = format switch
        {
            "fbx" => $"bpy.ops.export_scene.fbx(filepath={JsonSerializer.Serialize(output)})",
            "gltf" or "glb" => $"bpy.ops.export_scene.gltf(filepath={JsonSerializer.Serialize(output)})",
            "stl" => $"bpy.ops.export_mesh.stl(filepath={JsonSerializer.Serialize(output)})",
            _ => $"bpy.ops.wm.obj_export(filepath={JsonSerializer.Serialize(output)})"
        };

        return $$"""
import bpy, json
{{exportCall}}
print("SD_JSON:" + json.dumps({"ok": True, "exported": {{JsonSerializer.Serialize(output)}}, "format": {{JsonSerializer.Serialize(format)}}, "provider": "blender-python"}))
""";
    }

    public static string BuildSaveScript(string dest) => $$"""
import bpy, json
bpy.ops.wm.save_as_mainfile(filepath={{JsonSerializer.Serialize(dest)}})
print("SD_JSON:" + json.dumps({"ok": True, "saved": {{JsonSerializer.Serialize(dest)}}, "provider": "blender-python"}))
""";

    public static string BuildRenderScript(string output, string? engine, int? frame, bool animation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import bpy, json, os");
        if (!string.IsNullOrWhiteSpace(engine))
        {
            sb.AppendLine($"bpy.context.scene.render.engine = {JsonSerializer.Serialize(engine)}");
        }

        if (frame.HasValue)
        {
            sb.AppendLine($"bpy.context.scene.frame_set({frame.Value})");
        }

        sb.AppendLine($"bpy.context.scene.render.filepath = {JsonSerializer.Serialize(output)}");
        sb.AppendLine("os.makedirs(os.path.dirname(os.path.abspath(bpy.context.scene.render.filepath)), exist_ok=True)");
        if (animation)
        {
            sb.AppendLine("bpy.ops.render.render(animation=True, write_still=False)");
        }
        else
        {
            sb.AppendLine("bpy.ops.render.render(write_still=True)");
        }

        sb.AppendLine($$"""print("SD_JSON:" + json.dumps({"ok": True, "rendered": {{JsonSerializer.Serialize(output)}}, "provider": "blender-python"}))""");
        return sb.ToString();
    }

    public static string BuildImportMeshScript(string inputPath, string format, string? objectName, string? collectionName)
    {
        var importCall = format switch
        {
            "fbx" => $"bpy.ops.import_scene.fbx(filepath={JsonSerializer.Serialize(inputPath)})",
            "gltf" or "glb" => $"bpy.ops.import_scene.gltf(filepath={JsonSerializer.Serialize(inputPath)})",
            "stl" => $"bpy.ops.import_mesh.stl(filepath={JsonSerializer.Serialize(inputPath)})",
            _ => $"bpy.ops.wm.obj_import(filepath={JsonSerializer.Serialize(inputPath)})"
        };

        var sb = new StringBuilder();
        sb.AppendLine("import bpy, json");
        sb.AppendLine(importCall);
        if (!string.IsNullOrWhiteSpace(objectName))
        {
            sb.AppendLine("if bpy.context.selected_objects:");
            sb.AppendLine($"    bpy.context.selected_objects[0].name = {JsonSerializer.Serialize(objectName)}");
        }

        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            sb.AppendLine($"coll = bpy.data.collections.get({JsonSerializer.Serialize(collectionName)})");
            sb.AppendLine("if coll is None:");
            sb.AppendLine($"    coll = bpy.data.collections.new({JsonSerializer.Serialize(collectionName)})");
            sb.AppendLine("    bpy.context.scene.collection.children.link(coll)");
            sb.AppendLine("for obj in bpy.context.selected_objects:");
            sb.AppendLine("    for c in obj.users_collection:");
            sb.AppendLine("        c.objects.unlink(obj)");
            sb.AppendLine("    coll.objects.link(obj)");
        }

        sb.AppendLine($$"""print("SD_JSON:" + json.dumps({"ok": True, "imported": {{JsonSerializer.Serialize(inputPath)}}, "format": {{JsonSerializer.Serialize(format)}}, "provider": "blender-python"}))""");
        return sb.ToString();
    }

    public static string BuildApplyTransformScript(string? name)
    {
        var target = string.IsNullOrWhiteSpace(name)
            ? "obj = bpy.context.view_layer.objects.active"
            : $"obj = bpy.data.objects.get({JsonSerializer.Serialize(name)})";
        return $$"""
import bpy, json
{{target}}
if obj is None:
    print("SD_JSON:" + json.dumps({"ok": False, "error": "object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    print("SD_JSON:" + json.dumps({"ok": True, "applied": obj.name, "provider": "blender-python"}))
""";
    }

    public static string BuildJoinScript(IReadOnlyList<string> names)
    {
        var namesJson = JsonSerializer.Serialize(names);
        return $$"""
import bpy, json
names = {{namesJson}}
objs = [bpy.data.objects.get(n) for n in names]
objs = [o for o in objs if o is not None]
if len(objs) < 2:
    print("SD_JSON:" + json.dumps({"ok": False, "error": "need at least two objects"}))
else:
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    print("SD_JSON:" + json.dumps({"ok": True, "joined": objs[0].name, "provider": "blender-python"}))
""";
    }

    public static string BuildBatchScript(IReadOnlyList<Dictionary<string, object?>> operations, bool failFast)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import bpy, json");
        sb.AppendLine("results = []");
        sb.AppendLine($"fail_fast = {failFast.ToString().ToLowerInvariant()}");
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var opName = GetString(op, "op") ?? GetString(op, "operation") ?? "";
            sb.AppendLine($"# step {i}: {opName}");
            sb.AppendLine("try:");
            sb.AppendLine(BuildBatchStepBody(op, 4));
            sb.AppendLine($"    results.append({{ \"index\": {i}, \"ok\": True, \"op\": {JsonSerializer.Serialize(opName)} }})");
            sb.AppendLine("except Exception as ex:");
            sb.AppendLine($"    results.append({{ \"index\": {i}, \"ok\": False, \"op\": {JsonSerializer.Serialize(opName)}, \"error\": str(ex) }})");
            sb.AppendLine("    if fail_fast:");
            sb.AppendLine("        break");
        }

        sb.AppendLine("print(\"SD_JSON:\" + json.dumps({\"ok\": True, \"results\": results, \"provider\": \"blender-python\"}))");
        return sb.ToString();
    }

    private static string BuildBatchStepBody(Dictionary<string, object?> op, int indent)
    {
        var pad = new string(' ', indent);
        var opName = (GetString(op, "op") ?? GetString(op, "operation") ?? "").ToLowerInvariant();
        return opName switch
        {
            "get_scene" => pad + "scene = bpy.context.scene\n" +
                           pad + "_ = scene.name",
            "get_objects" => pad + "_ = len(bpy.data.objects)",
            "select_object" => pad + $"obj = bpy.data.objects.get({JsonSerializer.Serialize(GetString(op, "name") ?? GetString(op, "object") ?? "")})\n" +
                               pad + "if obj is None: raise RuntimeError('not found')\n" +
                               pad + "bpy.ops.object.select_all(action='DESELECT')\n" +
                               pad + "obj.select_set(True)\n" +
                               pad + "bpy.context.view_layer.objects.active = obj",
            "export" => BuildBatchExportStep(op, pad),
            "save" => pad + $"bpy.ops.wm.save_as_mainfile(filepath={JsonSerializer.Serialize(GetString(op, "destination") ?? GetString(op, "path") ?? "")})",
            "render" => BuildBatchRenderStep(op, pad),
            "import_mesh" => BuildBatchImportStep(op, pad),
            "apply_transform" => pad + BuildApplyTransformScript(GetString(op, "name")).Replace("\n", "\n" + pad).TrimStart(),
            "join" => pad + "raise RuntimeError('join in batch requires names array')",
            _ => pad + $"raise RuntimeError('unsupported op: {opName}')"
        };
    }

    private static string BuildBatchExportStep(Dictionary<string, object?> op, string pad)
    {
        var output = GetString(op, "output") ?? GetString(op, "destination") ?? "";
        var format = (GetString(op, "format") ?? Path.GetExtension(output).TrimStart('.') ?? "obj").ToLowerInvariant();
        var exportCall = format switch
        {
            "fbx" => $"bpy.ops.export_scene.fbx(filepath={JsonSerializer.Serialize(output)})",
            "gltf" or "glb" => $"bpy.ops.export_scene.gltf(filepath={JsonSerializer.Serialize(output)})",
            "stl" => $"bpy.ops.export_mesh.stl(filepath={JsonSerializer.Serialize(output)})",
            _ => $"bpy.ops.wm.obj_export(filepath={JsonSerializer.Serialize(output)})"
        };
        return pad + exportCall;
    }

    private static string BuildBatchRenderStep(Dictionary<string, object?> op, string pad)
    {
        var output = GetString(op, "output") ?? "";
        var engine = GetString(op, "engine");
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(engine))
        {
            sb.AppendLine(pad + $"bpy.context.scene.render.engine = {JsonSerializer.Serialize(engine)}");
        }

        var frame = GetInt(op, "frame");
        if (frame.HasValue)
        {
            sb.AppendLine(pad + $"bpy.context.scene.frame_set({frame.Value})");
        }

        sb.AppendLine(pad + $"bpy.context.scene.render.filepath = {JsonSerializer.Serialize(output)}");
        sb.AppendLine(pad + "import os");
        sb.AppendLine(pad + "os.makedirs(os.path.dirname(os.path.abspath(bpy.context.scene.render.filepath)), exist_ok=True)");
        sb.AppendLine(pad + "bpy.ops.render.render(write_still=True)");
        return sb.ToString().TrimEnd();
    }

    private static string BuildBatchImportStep(Dictionary<string, object?> op, string pad)
    {
        var input = GetString(op, "path") ?? GetString(op, "input") ?? "";
        var format = (GetString(op, "format") ?? Path.GetExtension(input).TrimStart('.') ?? "obj").ToLowerInvariant();
        var importCall = format switch
        {
            "fbx" => $"bpy.ops.import_scene.fbx(filepath={JsonSerializer.Serialize(input)})",
            "gltf" or "glb" => $"bpy.ops.import_scene.gltf(filepath={JsonSerializer.Serialize(input)})",
            "stl" => $"bpy.ops.import_mesh.stl(filepath={JsonSerializer.Serialize(input)})",
            _ => $"bpy.ops.wm.obj_import(filepath={JsonSerializer.Serialize(input)})"
        };
        return pad + importCall;
    }

    private static string? GetString(Dictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
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

    private static int? GetInt(Dictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
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
}
