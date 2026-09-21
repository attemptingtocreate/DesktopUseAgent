"""Higher-poly RefineSign, UpgradeSign, PickaxePedestal — match roll-button density."""
import bpy
import math
import os
import json
from mathutils import Vector, Euler

OUT = r"C:\Users\Administrator\Desktop\DesktopUseAgent\assets\pickaxe-set"
os.makedirs(OUT, exist_ok=True)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for coll in (bpy.data.meshes, bpy.data.curves, bpy.data.fonts):
        for b in list(coll):
            try:
                coll.remove(b)
            except Exception:
                pass
    for b in list(bpy.data.materials):
        if b.users == 0:
            try:
                bpy.data.materials.remove(b)
            except Exception:
                pass


def mat(name, rgb, roughness=0.45, metallic=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    if "Metallic" in bsdf.inputs:
        bsdf.inputs["Metallic"].default_value = metallic
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return m


def assign(obj, material):
    if obj.data.materials:
        obj.data.materials[0] = material
    else:
        obj.data.materials.append(material)
    for p in obj.data.polygons:
        p.use_smooth = False


def bevel(obj, width=0.035, segments=3):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_add(type="BEVEL")
    mod = obj.modifiers[-1]
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    bpy.ops.object.modifier_apply(modifier=mod.name)
    obj.select_set(False)


def apply_trs(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    obj.select_set(False)


def cube(name, sx, sy, sz, loc=(0, 0, 0), rot=(0, 0, 0), material=None, do_bevel=True, bw=0.035):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = (sx, sy, sz)
    bpy.ops.object.transform_apply(scale=True)
    if do_bevel:
        bevel(o, bw, 3)
    if material:
        assign(o, material)
    return o


def cylinder(name, radius, depth, loc=(0, 0, 0), verts=16, material=None, do_bevel=True):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=radius, depth=depth, location=loc)
    o = bpy.context.active_object
    o.name = name
    if do_bevel:
        bevel(o, 0.025, 2)
    if material:
        assign(o, material)
    apply_trs(o)
    return o


def text_mesh(name, body, size, loc, rot, material, extrude=0.045):
    curve = bpy.data.curves.new(name + "_c", type="FONT")
    curve.body = body
    curve.size = size
    curve.extrude = extrude
    curve.align_x = "CENTER"
    curve.align_y = "CENTER"
    o = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(o)
    o.location = loc
    o.rotation_euler = Euler(rot)
    o.data.materials.append(material)
    bpy.context.view_layer.objects.active = o
    o.select_set(True)
    bpy.ops.object.convert(target="MESH")
    o = bpy.context.active_object
    o.name = name
    for p in o.data.polygons:
        p.use_smooth = False
    apply_trs(o)
    o.select_set(False)
    return o


def mat_color(obj):
    if not obj.data.materials or not obj.data.materials[0]:
        return (0.7, 0.7, 0.7)
    m = obj.data.materials[0]
    if m.use_nodes and m.node_tree:
        for n in m.node_tree.nodes:
            if n.type == "BSDF_PRINCIPLED":
                c = n.inputs["Base Color"].default_value
                return (float(c[0]), float(c[1]), float(c[2]))
    return (0.7, 0.7, 0.7)


def package_scene(package_name):
    parts = []
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        dg = bpy.context.evaluated_depsgraph_get()
        eo = obj.evaluated_get(dg)
        mesh = eo.to_mesh()
        mesh.transform(obj.matrix_world)
        mesh.calc_loop_triangles()
        verts = [[round(v.co.x, 4), round(v.co.z, 4), round(-v.co.y, 4)] for v in mesh.vertices]
        faces = [[int(t.vertices[0]), int(t.vertices[1]), int(t.vertices[2])] for t in mesh.loop_triangles]
        c = mat_color(obj)
        parts.append(
            {
                "name": obj.name,
                "verts": verts,
                "faces": faces,
                "color": [round(c[0], 4), round(c[1], 4), round(c[2], 4)],
                "emit": 0.0,
            }
        )
        eo.to_mesh_clear()
    path = os.path.join(OUT, package_name + ".mesh.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"name": package_name, "parts": parts}, f, separators=(",", ":"))
    print(package_name, len(parts), os.path.getsize(path))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, package_name + ".blend"))
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.fbx(
        filepath=os.path.join(OUT, package_name + ".fbx"),
        use_selection=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
    )


def build_pedestal():
    clear_scene()
    m_ped = mat("Ped", (0.45, 0.48, 0.52), 0.55)
    m_top = mat("PedTop", (0.58, 0.60, 0.64), 0.45, 0.08)
    m_dark = mat("PedDark", (0.28, 0.30, 0.34), 0.5)
    m_trim = mat("PedTrim", (0.40, 0.48, 0.58), 0.4, 0.15)
    cylinder("Base", 1.1, 0.28, loc=(0, 0, 0.14), verts=16, material=m_dark)
    cylinder("Column", 0.52, 0.95, loc=(0, 0, 0.75), verts=16, material=m_ped)
    # mid ring
    cylinder("MidRing", 0.62, 0.1, loc=(0, 0, 0.95), verts=16, material=m_trim, do_bevel=False)
    cylinder("Top", 0.95, 0.22, loc=(0, 0, 1.25), verts=16, material=m_top)
    cylinder("Ring", 0.7, 0.1, loc=(0, 0, 1.4), verts=16, material=m_dark)
    # small corner accents
    for i, (x, y) in enumerate([(0.55, 0), (-0.55, 0), (0, 0.55), (0, -0.55)]):
        cube(f"Accent{i}", 0.14, 0.14, 0.55, loc=(x, y, 0.75), material=m_trim, bw=0.02)
    package_scene("PickaxePedestal")


def build_refine():
    clear_scene()
    wood = mat("Wood", (0.42, 0.26, 0.14), 0.65)
    board = mat("Board", (0.72, 0.68, 0.58), 0.5)
    ink = mat("Ink", (0.12, 0.18, 0.35), 0.3)
    metal = mat("Metal", (0.5, 0.5, 0.55), 0.35, 0.45)
    frame = mat("Frame", (0.55, 0.52, 0.45), 0.45)
    # slightly thicker legs with bevel
    cube("LegL", 0.28, 0.28, 2.7, loc=(-1.4, 0, 1.35), material=wood, bw=0.04)
    cube("LegR", 0.28, 0.28, 2.7, loc=(1.4, 0, 1.35), material=wood, bw=0.04)
    # board + frame lip
    cube("Board", 2.9, 0.22, 1.2, loc=(0, 0, 2.55), material=board, bw=0.04)
    cube("FrameTop", 3.0, 0.12, 0.12, loc=(0, -0.08, 3.2), material=frame, bw=0.02)
    cube("FrameBot", 3.0, 0.12, 0.12, loc=(0, -0.08, 1.9), material=frame, bw=0.02)
    cube("Brace", 2.8, 0.14, 0.16, loc=(0, 0, 1.9), material=metal, bw=0.02)
    text_mesh("RefineText", "REFINE", 0.58, loc=(0, -0.14, 2.55), rot=(math.radians(90), 0, 0), material=ink)
    package_scene("RefineSign")


def build_upgrade():
    clear_scene()
    wood = mat("UpWood", (0.42, 0.26, 0.14), 0.65)
    board_c = mat("UpBoard", (0.22, 0.26, 0.34), 0.5)
    panel_c = mat("UpPanel", (0.14, 0.18, 0.28), 0.45)
    accent = mat("UpAccent", (0.35, 0.55, 0.95), 0.35)
    ink = mat("UpInk", (0.98, 0.98, 1.0), 0.3)
    metal = mat("UpMetal", (0.45, 0.48, 0.55), 0.4, 0.3)

    cube("LegL", 0.32, 0.32, 3.3, loc=(-2.2, 0, 1.65), material=wood, bw=0.045)
    cube("LegR", 0.32, 0.32, 3.3, loc=(2.2, 0, 1.65), material=wood, bw=0.045)
    # main board
    cube("Board", 4.6, 0.26, 2.7, loc=(0, 0, 2.5), material=board_c, bw=0.045)
    # header bar full width
    cube("Header", 4.4, 0.14, 0.48, loc=(0, -0.16, 3.55), material=accent, bw=0.025)
    cube("HeaderLip", 4.5, 0.08, 0.08, loc=(0, -0.18, 3.28), material=metal, bw=0.015)
    # THREE evenly spaced panels across the front (centers at -1.45, 0, 1.45)
    panel_w, panel_h, panel_d = 1.3, 1.75, 0.1
    for name, x in (("LuckPanel", -1.45), ("PickaxesPanel", 0.0), ("RollsPanel", 1.45)):
        cube(name, panel_w, panel_d, panel_h, loc=(x, -0.18, 2.25), material=panel_c, do_bevel=True, bw=0.03)
    # thin dividers between panels
    cube("DivL", 0.06, 0.08, 1.9, loc=(-0.72, -0.17, 2.25), material=metal, bw=0.01)
    cube("DivR", 0.06, 0.08, 1.9, loc=(0.72, -0.17, 2.25), material=metal, bw=0.01)
    text_mesh(
        "HeaderText",
        "UPGRADES",
        0.32,
        loc=(0, -0.24, 3.55),
        rot=(math.radians(90), 0, 0),
        material=ink,
        extrude=0.025,
    )
    package_scene("UpgradeSign")


def main():
    build_pedestal()
    build_refine()
    build_upgrade()
    print("SIGNS_PEDS_OK")


if __name__ == "__main__":
    main()
