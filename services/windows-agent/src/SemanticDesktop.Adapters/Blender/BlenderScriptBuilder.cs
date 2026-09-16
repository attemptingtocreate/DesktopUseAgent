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

    public static string BuildExportForRobloxScript(string output, string format) => $$"""
import bpy, json
output = {{JsonSerializer.Serialize(output)}}
fmt = {{JsonSerializer.Serialize(format)}}
mesh_objs = [o for o in bpy.context.scene.objects if o.type == "MESH"]
if mesh_objs:
    bpy.ops.object.select_all(action='DESELECT')
    for o in mesh_objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = mesh_objs[0]
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
if fmt == "fbx":
    bpy.ops.export_scene.fbx(filepath=output, use_selection=False, apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y')
elif fmt in ("gltf", "glb"):
    bpy.ops.export_scene.gltf(filepath=output, export_format='GLB' if fmt == 'glb' else 'GLTF_SEPARATE')
else:
    bpy.ops.wm.obj_export(filepath=output)
print("SD_JSON:" + json.dumps({"ok": True, "exported": output, "format": fmt, "preset": "roblox", "provider": "blender-python"}))
""";

    public static string BuildCreateMeshScript(string kind, string? name, double[] location, double[] scale, double? size)
    {
        var kindMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cube"] = "primitive_cube_add",
            ["uv_sphere"] = "primitive_uv_sphere_add",
            ["ico_sphere"] = "primitive_ico_sphere_add",
            ["cylinder"] = "primitive_cylinder_add",
            ["cone"] = "primitive_cone_add",
            ["plane"] = "primitive_plane_add",
            ["torus"] = "primitive_torus_add"
        };
        if (!kindMap.TryGetValue(kind, out var op))
        {
            throw new ArgumentException($"Unsupported mesh kind '{kind}'.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("import bpy, json");
        if (kind.Equals("cube", StringComparison.OrdinalIgnoreCase) && size.HasValue)
        {
            sb.AppendLine($"bpy.ops.mesh.{op}(location=({location[0]}, {location[1]}, {location[2]}), size={size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        }
        else
        {
            sb.AppendLine($"bpy.ops.mesh.{op}(location=({location[0]}, {location[1]}, {location[2]}))");
        }

        sb.AppendLine("obj = bpy.context.view_layer.objects.active");
        sb.AppendLine($"obj.scale = ({scale[0]}, {scale[1]}, {scale[2]})");
        if (!string.IsNullOrWhiteSpace(name))
        {
            sb.AppendLine($"obj.name = {JsonSerializer.Serialize(name)}");
            sb.AppendLine("if obj.data: obj.data.name = obj.name");
        }

        sb.AppendLine("""print("SD_JSON:" + json.dumps({"ok": True, "created": obj.name, "kind": """ + JsonSerializer.Serialize(kind) + """, "provider": "blender-python"}))""");
        return sb.ToString();
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

    public static string BuildMeshExtrudeScript(string objectName, string mode, double value, IReadOnlyList<int>? indices)
    {
        var indicesJson = JsonSerializer.Serialize(indices ?? Array.Empty<int>());
        return $$"""
import bpy, bmesh, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bm = bmesh.from_edit_mesh(obj.data)
    bm.faces.ensure_lookup_table(); bm.edges.ensure_lookup_table(); bm.verts.ensure_lookup_table()
    bpy.ops.mesh.select_all(action='DESELECT')
    mode = {{JsonSerializer.Serialize(mode)}}
    indices = {{indicesJson}}
    if indices:
        bpy.ops.mesh.select_mode(type=mode)
        for i in indices:
            if mode == 'VERT' and 0 <= i < len(bm.verts): bm.verts[i].select = True
            elif mode == 'EDGE' and 0 <= i < len(bm.edges): bm.edges[i].select = True
            elif mode == 'FACE' and 0 <= i < len(bm.faces): bm.faces[i].select = True
        bmesh.update_edit_mesh(obj.data)
    bpy.ops.mesh.extrude_region_move(TRANSFORM_OT_translate={"value": (0.0, 0.0, {{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}})})
    bpy.ops.object.mode_set(mode='OBJECT')
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "extruded": {{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "provider": "blender-python"}))
""";
    }

    public static string BuildMeshInsetScript(string objectName, double thickness, double depth, IReadOnlyList<int>? indices)
    {
        var indicesJson = JsonSerializer.Serialize(indices ?? Array.Empty<int>());
        return $$"""
import bpy, bmesh, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bm = bmesh.from_edit_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    indices = {{indicesJson}}
    if indices:
        bpy.ops.mesh.select_all(action='DESELECT')
        bpy.ops.mesh.select_mode(type='FACE')
        for i in indices:
            if 0 <= i < len(bm.faces): bm.faces[i].select = True
        bmesh.update_edit_mesh(obj.data)
    bpy.ops.mesh.inset(thickness={{thickness.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, depth={{depth.ToString(System.Globalization.CultureInfo.InvariantCulture)}})
    bpy.ops.object.mode_set(mode='OBJECT')
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "thickness": {{thickness.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "depth": {{depth.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "provider": "blender-python"}))
""";
    }

    public static string BuildMeshBevelScript(string objectName, string mode, double offset, int segments, IReadOnlyList<int>? indices)
    {
        var indicesJson = JsonSerializer.Serialize(indices ?? Array.Empty<int>());
        return $$"""
import bpy, bmesh, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bm = bmesh.from_edit_mesh(obj.data)
    bm.faces.ensure_lookup_table(); bm.edges.ensure_lookup_table(); bm.verts.ensure_lookup_table()
    mode = {{JsonSerializer.Serialize(mode)}}
    indices = {{indicesJson}}
    if indices:
        bpy.ops.mesh.select_all(action='DESELECT')
        bpy.ops.mesh.select_mode(type=mode)
        for i in indices:
            if mode == 'VERT' and 0 <= i < len(bm.verts): bm.verts[i].select = True
            elif mode == 'EDGE' and 0 <= i < len(bm.edges): bm.edges[i].select = True
            elif mode == 'FACE' and 0 <= i < len(bm.faces): bm.faces[i].select = True
        bmesh.update_edit_mesh(obj.data)
    bpy.ops.mesh.bevel(offset={{offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, segments={{segments}}, affect='EDGES')
    bpy.ops.object.mode_set(mode='OBJECT')
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "offset": {{offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "segments": {{segments}}, "provider": "blender-python"}))
""";
    }

    public static string BuildMeshLoopCutScript(string objectName, int cuts, int? edgeIndex)
    {
        var edgeBlock = edgeIndex.HasValue
            ? $"""
    bm = bmesh.from_edit_mesh(obj.data)
    bm.edges.ensure_lookup_table()
    bpy.ops.mesh.select_all(action='DESELECT')
    bpy.ops.mesh.select_mode(type='EDGE')
    if 0 <= {edgeIndex.Value} < len(bm.edges):
        bm.edges[{edgeIndex.Value}].select = True
    bmesh.update_edit_mesh(obj.data)
"""
            : "";
        return $$"""
import bpy, bmesh, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
{{edgeBlock}}
    try:
        bpy.ops.mesh.loopcut_and_slide(MESH_OT_loopcut={"number_cuts": {{cuts}}})
    except Exception:
        bpy.ops.mesh.subdivide(number_cuts={{cuts}})
    bpy.ops.object.mode_set(mode='OBJECT')
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "cuts": {{cuts}}, "provider": "blender-python"}))
""";
    }

    public static string BuildModifierBooleanScript(string objectName, string target, string operation, bool apply) => $$"""
import bpy, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
target = bpy.data.objects.get({{JsonSerializer.Serialize(target)}})
if obj is None or obj.type != 'MESH' or target is None or target.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "object and target mesh required"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    mod = obj.modifiers.new(name='Boolean', type='BOOLEAN')
    mod.operation = {{JsonSerializer.Serialize(operation)}}
    mod.object = target
    applied = {{(apply ? "True" : "False")}}
    if applied:
        bpy.ops.object.modifier_apply(modifier=mod.name)
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "operation": mod.operation, "target": target.name, "applied": applied, "provider": "blender-python"}))
""";

    public static string BuildModifierMirrorScript(string objectName, string axis, bool apply) => $$"""
import bpy, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    mod = obj.modifiers.new(name='Mirror', type='MIRROR')
    axis = {{JsonSerializer.Serialize(axis)}}
    mod.use_axis[0] = axis == 'X'
    mod.use_axis[1] = axis == 'Y'
    mod.use_axis[2] = axis == 'Z'
    applied = {{(apply ? "True" : "False")}}
    if applied:
        bpy.ops.object.modifier_apply(modifier=mod.name)
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "axis": axis, "applied": applied, "provider": "blender-python"}))
""";

    public static string BuildModifierArrayScript(string objectName, int count, double[] relative, bool apply) => $$"""
import bpy, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    mod = obj.modifiers.new(name='Array', type='ARRAY')
    mod.count = {{count}}
    mod.relative_offset_displace = ({{relative[0].ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{relative[1].ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{relative[2].ToString(System.Globalization.CultureInfo.InvariantCulture)}})
    applied = {{(apply ? "True" : "False")}}
    if applied:
        bpy.ops.object.modifier_apply(modifier=mod.name)
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "count": {{count}}, "applied": applied, "provider": "blender-python"}))
""";

    public static string BuildMaterialSetScript(string objectName, string? material, double[] color, double? roughness, double? metallic)
    {
        var matExpr = string.IsNullOrWhiteSpace(material)
            ? "f\"{obj.name}_Mat\""
            : JsonSerializer.Serialize(material);
        var roughLine = roughness.HasValue
            ? $"bsdf.inputs['Roughness'].default_value = {roughness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "pass";
        var metalLine = metallic.HasValue
            ? $"bsdf.inputs['Metallic'].default_value = {metallic.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "pass";
        return $$"""
import bpy, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    mat_name = {{matExpr}}
    mat = bpy.data.materials.get(mat_name)
    if mat is None:
        mat = bpy.data.materials.new(name=mat_name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    if bsdf:
        bsdf.inputs['Base Color'].default_value = ({{color[0].ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{color[1].ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{color[2].ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{color[3].ToString(System.Globalization.CultureInfo.InvariantCulture)}})
        {{roughLine}}
        {{metalLine}}
    if obj.data.materials:
        obj.data.materials[0] = mat
    else:
        obj.data.materials.append(mat)
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "material": mat.name, "provider": "blender-python"}))
""";
    }

    public static string BuildUvUnwrapScript(string objectName, string method, double margin, double angleLimit) => $$"""
import bpy, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    method = {{JsonSerializer.Serialize(method)}}
    if method == 'SMART':
        bpy.ops.uv.smart_project(angle_limit={{angleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}})
    else:
        bpy.ops.uv.unwrap(method=method, margin={{margin.ToString(System.Globalization.CultureInfo.InvariantCulture)}})
    bpy.ops.object.mode_set(mode='OBJECT')
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "method": method, "provider": "blender-python"}))
""";

    public static string BuildSelectGeometryScript(string objectName, string mode, bool selectAll, IReadOnlyList<int>? indices)
    {
        var indicesJson = JsonSerializer.Serialize(indices ?? Array.Empty<int>());
        return $$"""
import bpy, bmesh, json
obj = bpy.data.objects.get({{JsonSerializer.Serialize(objectName)}})
if obj is None or obj.type != 'MESH':
    print("SD_JSON:" + json.dumps({"ok": False, "error": "mesh object not found"}))
else:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    mode = {{JsonSerializer.Serialize(mode)}}
    select_all = {{(selectAll ? "True" : "False")}}
    indices = {{indicesJson}}
    bpy.ops.mesh.select_mode(type=mode)
    if select_all:
        bpy.ops.mesh.select_all(action='SELECT')
        count = {'VERT': len(obj.data.vertices), 'EDGE': len(obj.data.edges), 'FACE': len(obj.data.polygons)}[mode]
    else:
        bm = bmesh.from_edit_mesh(obj.data)
        bm.faces.ensure_lookup_table(); bm.edges.ensure_lookup_table(); bm.verts.ensure_lookup_table()
        bpy.ops.mesh.select_all(action='DESELECT')
        count = 0
        for i in indices:
            if mode == 'VERT' and 0 <= i < len(bm.verts):
                bm.verts[i].select = True; count += 1
            elif mode == 'EDGE' and 0 <= i < len(bm.edges):
                bm.edges[i].select = True; count += 1
            elif mode == 'FACE' and 0 <= i < len(bm.faces):
                bm.faces[i].select = True; count += 1
        bmesh.update_edit_mesh(obj.data)
    print("SD_JSON:" + json.dumps({"ok": True, "object": obj.name, "mode": mode, "selected": count, "provider": "blender-python"}))
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
            "export_for_roblox" => BuildBatchExportStep(op, pad),
            "save" => pad + $"bpy.ops.wm.save_as_mainfile(filepath={JsonSerializer.Serialize(GetString(op, "destination") ?? GetString(op, "path") ?? "")})",
            "render" => BuildBatchRenderStep(op, pad),
            "import_mesh" => BuildBatchImportStep(op, pad),
            "create_mesh" => pad + $"bpy.ops.mesh.primitive_cube_add()\n" + pad + "# create_mesh simplified in batch; prefer dedicated create_mesh tool",
            "apply_transform" => pad + BuildApplyTransformScript(GetString(op, "name")).Replace("\n", "\n" + pad).TrimStart(),
            "join" => pad + "raise RuntimeError('join in batch requires names array')",
            "mesh_extrude" => pad + "raise RuntimeError('mesh_extrude in batch: use dedicated blender.mesh_extrude tool')",
            "mesh_inset" => pad + "raise RuntimeError('mesh_inset in batch: use dedicated blender.mesh_inset tool')",
            "mesh_bevel" => pad + "raise RuntimeError('mesh_bevel in batch: use dedicated blender.mesh_bevel tool')",
            "mesh_loop_cut" => pad + "raise RuntimeError('mesh_loop_cut in batch: use dedicated blender.mesh_loop_cut tool')",
            "modifier_boolean" => BuildBatchModifierBoolean(op, pad),
            "modifier_mirror" => BuildBatchModifierMirror(op, pad),
            "modifier_array" => BuildBatchModifierArray(op, pad),
            "material_set" => pad + "raise RuntimeError('material_set in batch: use dedicated blender.material_set tool')",
            "uv_unwrap" => pad + "raise RuntimeError('uv_unwrap in batch: use dedicated blender.uv_unwrap tool')",
            "select_geometry" => pad + "raise RuntimeError('select_geometry in batch: use dedicated blender.select_geometry tool')",
            _ => pad + $"raise RuntimeError('unsupported op: {opName}')"
        };
    }

    private static string BuildBatchModifierBoolean(Dictionary<string, object?> op, string pad)
    {
        var name = GetString(op, "name") ?? GetString(op, "object") ?? "";
        var target = GetString(op, "target") ?? GetString(op, "operand") ?? "";
        var operation = (GetString(op, "operation") ?? "DIFFERENCE").ToUpperInvariant();
        return pad + $"obj = bpy.data.objects.get({JsonSerializer.Serialize(name)})\n" +
               pad + $"target = bpy.data.objects.get({JsonSerializer.Serialize(target)})\n" +
               pad + "if obj is None or target is None: raise RuntimeError('object/target required')\n" +
               pad + "bpy.context.view_layer.objects.active = obj\n" +
               pad + "mod = obj.modifiers.new(name='Boolean', type='BOOLEAN')\n" +
               pad + $"mod.operation = {JsonSerializer.Serialize(operation)}\n" +
               pad + "mod.object = target\n" +
               pad + "bpy.ops.object.modifier_apply(modifier=mod.name)";
    }

    private static string BuildBatchModifierMirror(Dictionary<string, object?> op, string pad)
    {
        var name = GetString(op, "name") ?? GetString(op, "object") ?? "";
        var axis = (GetString(op, "axis") ?? "X").ToUpperInvariant();
        return pad + $"obj = bpy.data.objects.get({JsonSerializer.Serialize(name)})\n" +
               pad + "if obj is None: raise RuntimeError('object required')\n" +
               pad + "bpy.context.view_layer.objects.active = obj\n" +
               pad + "mod = obj.modifiers.new(name='Mirror', type='MIRROR')\n" +
               pad + $"mod.use_axis[0] = {JsonSerializer.Serialize(axis)} == 'X'\n" +
               pad + $"mod.use_axis[1] = {JsonSerializer.Serialize(axis)} == 'Y'\n" +
               pad + $"mod.use_axis[2] = {JsonSerializer.Serialize(axis)} == 'Z'\n" +
               pad + "bpy.ops.object.modifier_apply(modifier=mod.name)";
    }

    private static string BuildBatchModifierArray(Dictionary<string, object?> op, string pad)
    {
        var name = GetString(op, "name") ?? GetString(op, "object") ?? "";
        var count = GetInt(op, "count") ?? 2;
        return pad + $"obj = bpy.data.objects.get({JsonSerializer.Serialize(name)})\n" +
               pad + "if obj is None: raise RuntimeError('object required')\n" +
               pad + "bpy.context.view_layer.objects.active = obj\n" +
               pad + "mod = obj.modifiers.new(name='Array', type='ARRAY')\n" +
               pad + $"mod.count = {count}\n" +
               pad + "bpy.ops.object.modifier_apply(modifier=mod.name)";
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
