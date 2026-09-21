"""Export low-poly props as JSON for Roblox EditableMesh import (verts/faces/colors)."""
import bpy
import json
import os
import math
from mathutils import Vector, Color

OUT = r"C:\Users\Administrator\Desktop\DesktopUseAgent\assets\pickaxe-set"
BLEND_FILES = [
    ("RollButtonSystem.blend", "RollButtonSystem"),
    ("SideButton.blend", "SideButton"),
    ("PickaxePedestal.blend", "PickaxePedestal"),
    ("OreSimple.blend", "OreSimple"),
]


def mat_color(obj):
    if not obj.data or not getattr(obj.data, "materials", None) or not obj.data.materials:
        return (0.7, 0.7, 0.7)
    m = obj.data.materials[0]
    if not m:
        return (0.7, 0.7, 0.7)
    if m.use_nodes and m.node_tree:
        for n in m.node_tree.nodes:
            if n.type == "BSDF_PRINCIPLED":
                c = n.inputs["Base Color"].default_value
                return (float(c[0]), float(c[1]), float(c[2]))
    c = m.diffuse_color
    return (float(c[0]), float(c[1]), float(c[2]))


def emit_strength(obj):
    if not obj.data or not obj.data.materials or not obj.data.materials[0]:
        return 0.0
    m = obj.data.materials[0]
    if m.use_nodes and m.node_tree:
        for n in m.node_tree.nodes:
            if n.type == "BSDF_PRINCIPLED":
                if "Emission Strength" in n.inputs:
                    return float(n.inputs["Emission Strength"].default_value)
    return 0.0


def mesh_to_dict(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    eval_obj = obj.evaluated_get(depsgraph)
    mesh = eval_obj.to_mesh()
    mesh.transform(obj.matrix_world)
    mesh.calc_loop_triangles()

    verts = []
    for v in mesh.vertices:
        # Blender Y-up / -Z forward-ish → Roblox: X right, Y up, Z back
        # Blender (x,y,z) with Y forward Z up → Roblox (x, z, -y)
        verts.append([round(v.co.x, 4), round(v.co.z, 4), round(-v.co.y, 4)])

    faces = []
    for tri in mesh.loop_triangles:
        faces.append([int(tri.vertices[0]), int(tri.vertices[1]), int(tri.vertices[2])])

    color = mat_color(obj)
    emit = emit_strength(obj)
    eval_obj.to_mesh_clear()

    return {
        "name": obj.name,
        "verts": verts,
        "faces": faces,
        "color": [round(color[0], 4), round(color[1], 4), round(color[2], 4)],
        "emit": round(emit, 3),
    }


def export_blend(blend_path, package_name):
    bpy.ops.wm.open_mainfile(filepath=blend_path)
    parts = []
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        parts.append(mesh_to_dict(obj))
    package = {"name": package_name, "parts": parts}
    out_path = os.path.join(OUT, package_name + ".mesh.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(package, f, separators=(",", ":"))
    size = os.path.getsize(out_path)
    print(f"EXPORTED {package_name} parts={len(parts)} bytes={size}")
    return out_path, size


def main():
    results = []
    for fname, pname in BLEND_FILES:
        path = os.path.join(OUT, fname)
        if not os.path.exists(path):
            print("MISSING", path)
            continue
        results.append(export_blend(path, pname))
    with open(os.path.join(OUT, "_mesh_json_ok.txt"), "w", encoding="utf-8") as f:
        for p, s in results:
            f.write(f"{p}\t{s}\n")


if __name__ == "__main__":
    main()
