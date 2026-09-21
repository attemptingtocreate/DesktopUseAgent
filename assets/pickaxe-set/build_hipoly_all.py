"""
Higher-poly low-poly props for Roblox EditableMesh import.
Slightly denser geometry (still stylized, not Roblox brick).
"""
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


def mat(name, rgb, roughness=0.45, metallic=0.0, emit=None, emit_str=0.0):
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
    if emit:
        if "Emission Color" in bsdf.inputs:
            bsdf.inputs["Emission Color"].default_value = (*emit, 1.0)
            bsdf.inputs["Emission Strength"].default_value = emit_str
        elif "Emission" in bsdf.inputs:
            bsdf.inputs["Emission"].default_value = (*emit, 1.0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return m


def assign(obj, material):
    if obj.data.materials:
        obj.data.materials[0] = material
    else:
        obj.data.materials.append(material)
    for p in obj.data.polygons:
        p.use_smooth = False


def apply_trs(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    obj.select_set(False)


def bevel(obj, width=0.03, segments=2):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_add(type="BEVEL")
    mod = obj.modifiers[-1]
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    bpy.ops.object.modifier_apply(modifier=mod.name)
    obj.select_set(False)


def cube(name, sx, sy, sz, loc=(0, 0, 0), rot=(0, 0, 0), material=None, do_bevel=True):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = (sx, sy, sz)
    bpy.ops.object.transform_apply(scale=True)
    if do_bevel:
        bevel(o, 0.04, 2)
    if material:
        assign(o, material)
    return o


def cylinder(name, radius, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=12, material=None, do_bevel=False):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=verts, radius=radius, depth=depth, location=loc, rotation=rot
    )
    o = bpy.context.active_object
    o.name = name
    if do_bevel:
        bevel(o, 0.02, 1)
    if material:
        assign(o, material)
    apply_trs(o)
    return o


def ico(name, radius, loc=(0, 0, 0), subdiv=2, material=None, squash=(1.2, 1.05, 0.9)):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdiv, radius=radius, location=loc)
    o = bpy.context.active_object
    o.name = name
    if material:
        assign(o, material)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.transform.resize(value=squash)
    bpy.ops.object.mode_set(mode="OBJECT")
    apply_trs(o)
    return o


def cone(name, r1, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=8, material=None):
    bpy.ops.mesh.primitive_cone_add(
        vertices=verts, radius1=r1, radius2=0.0, depth=depth, location=loc, rotation=rot
    )
    o = bpy.context.active_object
    o.name = name
    if material:
        assign(o, material)
    apply_trs(o)
    return o


def text_mesh(name, body, size, loc, rot, material, extrude=0.05):
    curve = bpy.data.curves.new(name + "_c", type="FONT")
    curve.body = body
    curve.size = size
    curve.extrude = extrude
    curve.bevel_depth = 0.008
    curve.bevel_resolution = 2
    curve.align_x = "CENTER"
    curve.align_y = "CENTER"
    o = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(o)
    o.location = loc
    o.rotation_euler = Euler(rot)
    if o.data.materials:
        o.data.materials[0] = material
    else:
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


def set_origin(obj, world_loc):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.context.scene.cursor.location = Vector(world_loc)
    bpy.ops.object.origin_set(type="ORIGIN_CURSOR")
    obj.select_set(False)


def save(name):
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, name))


def export_fbx(path):
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
    )


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
            if n.type == "BSDF_PRINCIPLED" and "Emission Strength" in n.inputs:
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


def package_scene(package_name):
    parts = []
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH":
            parts.append(mesh_to_dict(obj))
    package = {"name": package_name, "parts": parts}
    out_path = os.path.join(OUT, package_name + ".mesh.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(package, f, separators=(",", ":"))
    print(f"JSON {package_name} parts={len(parts)} bytes={os.path.getsize(out_path)}")
    return out_path


# Colors
COL_BASE = (0.55, 0.62, 0.70)
COL_DARK = (0.22, 0.24, 0.28)
COL_FRAME = (0.40, 0.48, 0.58)
COL_SIGN = (0.48, 0.58, 0.70)
COL_RED = (0.95, 0.18, 0.18)
COL_YELLOW = (1.0, 0.85, 0.15)
COL_WHITE = (0.98, 0.98, 1.0)
COL_INK = (0.12, 0.18, 0.35)
COL_WOOD = (0.42, 0.26, 0.14)
COL_BOARD = (0.72, 0.68, 0.58)
COL_UP_BOARD = (0.25, 0.28, 0.35)
COL_UP_PANEL = (0.18, 0.22, 0.32)
COL_ROCK = (0.52, 0.52, 0.55)
COL_PED = (0.45, 0.48, 0.52)
COL_PED_TOP = (0.58, 0.60, 0.64)


def build_roll():
    clear_scene()
    m_base = mat("LP_Base", COL_BASE, 0.55, 0.15)
    m_dark = mat("LP_Dark", COL_DARK, 0.4, 0.25)
    m_frame = mat("LP_Frame", COL_FRAME, 0.5, 0.1)
    m_sign = mat("LP_Sign", COL_SIGN, 0.45)
    m_red = mat("LP_Red", COL_RED, 0.35, 0.0, emit=COL_RED, emit_str=1.8)
    m_white = mat("LP_White", COL_WHITE, 0.35)
    cube("Base", 3.6, 2.0, 0.35, loc=(0, 0, 0.175), material=m_base)
    cube("BaseLip", 3.75, 2.15, 0.08, loc=(0, 0, 0.38), material=m_frame)
    tilt = math.radians(-28)
    cube("SignFrame", 1.7, 0.16, 1.15, loc=(-0.85, 0, 1.05), rot=(tilt, 0, 0), material=m_frame)
    cube("SignFace", 1.45, 0.06, 0.95, loc=(-0.85, -0.08, 1.08), rot=(tilt, 0, 0), material=m_sign, do_bevel=False)
    text_mesh("RollText", "Roll", 0.55, loc=(-0.85, -0.14, 1.12), rot=(math.radians(62), 0, 0), material=m_white)
    cube("ButtonHousing", 1.05, 1.05, 0.28, loc=(1.05, 0, 0.52), material=m_dark)
    btn = cylinder("RollButton", 0.42, 0.22, loc=(1.05, 0, 0.78), verts=12, material=m_red, do_bevel=True)
    set_origin(btn, (1.05, 0, 0.78))
    save("RollButtonSystem.blend")
    export_fbx(os.path.join(OUT, "RollButtonSystem.fbx"))
    package_scene("RollButtonSystem")


def build_side():
    clear_scene()
    m_base = mat("LP_SideBase", COL_BASE, 0.55, 0.15)
    m_dark = mat("LP_SideDark", COL_DARK, 0.4, 0.25)
    m_yel = mat("LP_Yellow", COL_YELLOW, 0.35, 0.0, emit=COL_YELLOW, emit_str=1.6)
    m_accent = mat("LP_Accent", COL_FRAME, 0.5)
    cube("Pedestal", 1.15, 1.15, 2.2, loc=(0, 0, 1.1), material=m_base)
    cube("Band", 1.25, 1.25, 0.12, loc=(0, 0, 1.9), material=m_accent)
    cube("TopPlate", 1.2, 1.2, 0.18, loc=(0, 0, 2.25), material=m_base)
    cube("ButtonHousing", 0.95, 0.95, 0.22, loc=(0, 0, 2.42), material=m_dark)
    btn = cylinder("YellowButton", 0.38, 0.2, loc=(0, 0, 2.62), verts=12, material=m_yel, do_bevel=True)
    set_origin(btn, (0, 0, 2.62))
    save("SideButton.blend")
    export_fbx(os.path.join(OUT, "SideButton.fbx"))
    package_scene("SideButton")


def build_pedestal():
    clear_scene()
    m_ped = mat("LP_Ped", COL_PED, 0.6)
    m_top = mat("LP_PedTop", COL_PED_TOP, 0.5, 0.05)
    m_dark = mat("LP_PedDark", (0.28, 0.30, 0.34), 0.55)
    cylinder("Base", 1.05, 0.22, loc=(0, 0, 0.11), verts=12, material=m_dark, do_bevel=True)
    cylinder("Column", 0.48, 0.85, loc=(0, 0, 0.65), verts=12, material=m_ped)
    cylinder("Top", 0.88, 0.18, loc=(0, 0, 1.15), verts=12, material=m_top, do_bevel=True)
    cylinder("Ring", 0.62, 0.08, loc=(0, 0, 1.28), verts=12, material=m_dark)
    save("PickaxePedestal.blend")
    export_fbx(os.path.join(OUT, "PickaxePedestal.fbx"))
    package_scene("PickaxePedestal")


def build_refine_sign():
    clear_scene()
    wood = mat("SignWood", COL_WOOD, 0.7)
    board = mat("SignBoard", COL_BOARD, 0.5)
    ink = mat("SignInk", COL_INK, 0.3)
    metal = mat("SignMetal", (0.5, 0.5, 0.52), 0.4, 0.5)
    cube("LegL", 0.22, 0.22, 2.6, loc=(-1.35, 0, 1.3), material=wood)
    cube("LegR", 0.22, 0.22, 2.6, loc=(1.35, 0, 1.3), material=wood)
    cube("Board", 2.8, 0.18, 1.1, loc=(0, 0, 2.5), material=board)
    cube("Brace", 2.7, 0.12, 0.15, loc=(0, 0, 1.95), material=metal, do_bevel=False)
    text_mesh("RefineText", "REFINE", 0.55, loc=(0, -0.12, 2.5), rot=(math.radians(90), 0, 0), material=ink)
    save("RefineSign.blend")
    export_fbx(os.path.join(OUT, "RefineSign.fbx"))
    package_scene("RefineSign")


def build_upgrade_sign():
    clear_scene()
    wood = mat("UpWood", COL_WOOD, 0.7)
    board = mat("UpBoard", COL_UP_BOARD, 0.55)
    panel = mat("UpPanel", COL_UP_PANEL, 0.5)
    ink = mat("UpInk", COL_WHITE, 0.3)
    accent = mat("UpAccent", (0.35, 0.55, 0.95), 0.35)
    cube("LegL", 0.28, 0.28, 3.2, loc=(-2.1, 0, 1.6), material=wood)
    cube("LegR", 0.28, 0.28, 3.2, loc=(2.1, 0, 1.6), material=wood)
    cube("Board", 4.4, 0.22, 2.6, loc=(0, 0, 2.4), material=board)
    cube("Header", 4.2, 0.12, 0.45, loc=(0, -0.14, 3.45), material=accent)
    cube("LuckPanel", 1.25, 0.08, 1.7, loc=(-1.4, -0.16, 2.2), material=panel, do_bevel=False)
    cube("PickaxesPanel", 1.25, 0.08, 1.7, loc=(0.0, -0.16, 2.2), material=panel, do_bevel=False)
    cube("RollsPanel", 1.25, 0.08, 1.7, loc=(1.4, -0.16, 2.2), material=panel, do_bevel=False)
    text_mesh("HeaderText", "UPGRADES", 0.28, loc=(0, -0.22, 3.45), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)
    text_mesh("LuckText", "Luck", 0.28, loc=(-1.4, -0.22, 2.85), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)
    text_mesh("PickaxesText", "Pickaxes", 0.22, loc=(0, -0.22, 2.85), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)
    text_mesh("RollsText", "Rolls", 0.28, loc=(1.4, -0.22, 2.85), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)
    save("UpgradeSign.blend")
    export_fbx(os.path.join(OUT, "UpgradeSign.fbx"))
    package_scene("UpgradeSign")


def build_ores():
    # Tier specs: (tier, style, base_rgb, accent_rgb, emit)
    # Tier1: bare grey only
    tiers = [
        (1, "bare", (0.52, 0.52, 0.55), None, 0),
        (2, "jagged", (0.42, 0.43, 0.46), (0.55, 0.55, 0.58), 0),
        (3, "gold", (0.40, 0.40, 0.43), (1.0, 0.78, 0.2), 0.4),
        (4, "purple", (0.36, 0.36, 0.42), (0.55, 0.25, 0.9), 0.6),
        (5, "pillars", (0.34, 0.36, 0.40), (0.25, 0.9, 0.35), 0.45),
        (6, "teal", (0.32, 0.34, 0.40), (0.15, 0.9, 0.85), 0.7),
        (7, "hell", (0.08, 0.07, 0.09), (1.0, 0.12, 0.05), 2.2),
        (8, "gem", (0.38, 0.38, 0.42), (1.0, 0.55, 0.1), 1.2),
        (9, "ice", (0.30, 0.34, 0.42), (0.45, 0.7, 1.0), 0.85),
        (10, "mythic", (0.08, 0.07, 0.1), (1.0, 0.92, 0.35), 2.0),
    ]
    for tier, style, base, accent, emit in tiers:
        clear_scene()
        m_rock = mat(f"Rock{tier}", base, 0.85)
        if style == "bare":
            ico("Rock", 1.25, loc=(0, 0, 0.95), subdiv=2, material=m_rock, squash=(1.3, 1.1, 0.95))
            ico("Lump", 0.55, loc=(0.5, -0.2, 0.7), subdiv=2, material=m_rock, squash=(1.15, 0.95, 0.85))
            ico("Lump2", 0.42, loc=(-0.55, 0.3, 0.55), subdiv=2, material=m_rock, squash=(1.05, 1.2, 0.8))
        elif style == "jagged":
            ico("Rock", 1.3, loc=(0, 0, 1.0), subdiv=2, material=m_rock)
            m2 = mat(f"Spike{tier}", accent or base, 0.8)
            cone("Spike1", 0.32, 1.0, loc=(0.55, 0.15, 1.85), rot=(math.radians(15), 0, 0.3), verts=8, material=m2)
            cone("Spike2", 0.26, 0.85, loc=(-0.45, -0.2, 1.7), rot=(math.radians(-12), 0.2, -0.3), verts=8, material=m2)
        elif style == "gold":
            ico("Rock", 1.25, loc=(0, 0, 0.95), subdiv=2, material=m_rock)
            m_g = mat(f"Gold{tier}", accent, 0.25, 0.35, emit=accent, emit_str=emit)
            m_v = mat(f"Vein{tier}", (1.0, 0.45, 0.12), 0.3, 0.0, emit=(1.0, 0.4, 0.1), emit_str=0.5)
            cone("Crystal1", 0.28, 1.0, loc=(0.45, 0.1, 1.9), verts=6, material=m_g)
            cone("Crystal2", 0.22, 0.8, loc=(-0.4, 0.3, 1.7), rot=(math.radians(-10), 0, 0.4), verts=6, material=m_g)
            cube("Vein1", 0.12, 0.12, 1.4, loc=(-0.55, -0.1, 1.1), rot=(0.3, 0.2, 0), material=m_v, do_bevel=False)
        elif style == "purple":
            ico("Rock", 1.3, loc=(0, 0, 1.0), subdiv=2, material=m_rock)
            m_p = mat(f"Purp{tier}", accent, 0.2, 0.0, emit=accent, emit_str=emit)
            m_b = mat(f"Blue{tier}", (0.25, 0.55, 1.0), 0.25, 0.0, emit=(0.2, 0.5, 1.0), emit_str=0.4)
            cylinder("Crystal1", 0.32, 1.25, loc=(0.35, 0.1, 1.95), verts=6, material=m_p)
            cylinder("Crystal2", 0.22, 0.95, loc=(-0.5, 0.25, 1.75), verts=6, material=m_p)
            cylinder("Crystal3", 0.18, 0.75, loc=(0.1, -0.55, 1.55), verts=6, material=m_p)
            cube("Vein", 0.1, 0.1, 1.5, loc=(0.6, -0.2, 1.15), rot=(-0.4, 0, 0.15), material=m_b, do_bevel=False)
        elif style == "pillars":
            for i, (x, y, h) in enumerate([(-0.55, 0.2, 2.5), (0.15, -0.45, 2.9), (0.55, 0.35, 2.3), (-0.15, 0.55, 2.1), (0.0, 0.0, 1.7)]):
                cylinder(f"Pillar{i}", 0.28 if i < 4 else 0.48, h, loc=(x, y, h * 0.5), verts=8, material=m_rock)
            m_g = mat(f"Green{tier}", accent, 0.25, 0.0, emit=accent, emit_str=emit)
            cone("Green1", 0.18, 0.55, loc=(-0.55, 0.2, 2.65), verts=6, material=m_g)
            cone("Green2", 0.15, 0.45, loc=(0.55, 0.35, 2.45), verts=6, material=m_g)
            cone("Green3", 0.2, 0.5, loc=(0.15, -0.45, 3.05), verts=6, material=m_g)
        elif style == "teal":
            ico("Rock", 1.2, loc=(0, 0, 0.9), subdiv=2, material=m_rock)
            m_t = mat(f"Teal{tier}", accent, 0.18, 0.0, emit=accent, emit_str=emit)
            cone("TealMain", 0.7, 2.3, loc=(0, 0, 2.3), verts=8, material=m_t)
            cone("TealSide", 0.35, 1.35, loc=(0.55, 0.25, 1.8), rot=(math.radians(25), 0, 0.4), verts=8, material=m_t)
        elif style == "hell":
            ico("Rock", 1.35, loc=(0, 0, 1.0), subdiv=2, material=m_rock)
            m_h = mat(f"Hell{tier}", accent, 0.2, 0.0, emit=accent, emit_str=emit)
            m_horn = mat(f"Horn{tier}", (0.92, 0.92, 0.95), 0.35)
            ico("Core", 0.55, loc=(0, 0, 1.4), subdiv=2, material=m_h, squash=(1, 1, 1))
            for i in range(4):
                ang = i * (math.pi * 0.5) + 0.4
                x, y = math.cos(ang) * 0.95, math.sin(ang) * 0.95
                cone(f"Horn{i}", 0.28, 1.85, loc=(x, y, 2.15), rot=(math.radians(-35), 0, ang), verts=6, material=m_horn)
        elif style == "gem":
            ico("Rock", 1.05, loc=(0, 0, 0.8), subdiv=2, material=m_rock)
            m_gem = mat(f"Gem{tier}", accent, 0.15, 0.0, emit=accent, emit_str=emit)
            cube("Gem", 1.35, 1.35, 1.35, loc=(0, 0, 1.95), rot=(math.radians(45), math.radians(35), math.radians(20)), material=m_gem)
        elif style == "ice":
            ico("Rock", 1.2, loc=(0, 0, 0.9), subdiv=2, material=m_rock)
            m_ice = mat(f"Ice{tier}", accent, 0.12, 0.0, emit=accent, emit_str=emit)
            for i in range(5):
                ang = i * (math.pi * 2 / 5)
                x, y = math.cos(ang) * 0.55, math.sin(ang) * 0.55
                cone(f"Shard{i}", 0.22, 1.65 + (i % 3) * 0.25, loc=(x, y, 1.8), rot=(math.radians(8), 0, ang), verts=6, material=m_ice)
        else:  # mythic
            ico("Rock", 1.25, loc=(0, 0, 0.95), subdiv=2, material=m_rock)
            m_m = mat(f"Myth{tier}", accent, 0.12, 0.0, emit=accent, emit_str=emit)
            m_halo = mat(f"Halo{tier}", (1.0, 0.95, 0.55), 0.2, 0.0, emit=(1, 0.9, 0.4), emit_str=1.5)
            ico("MythCore", 0.65, loc=(0, 0, 1.55), subdiv=2, material=m_m, squash=(1, 1, 1))
            cylinder("Halo", 1.15, 0.12, loc=(0, 0, 2.2), verts=16, material=m_halo)
            for i in range(6):
                ang = i * (math.pi / 3)
                x, y = math.cos(ang) * 0.85, math.sin(ang) * 0.85
                cone(f"Spire{i}", 0.16, 1.45, loc=(x, y, 2.35), rot=(math.radians(-20), 0, ang), verts=6, material=m_m)

        name = f"OreTier{tier}"
        if tier == 1:
            save("OreSimple.blend")
            export_fbx(os.path.join(OUT, "OreSimple.fbx"))
            package_scene("OreSimple")
        save("OreSet.blend")  # last tier visible
        export_fbx(os.path.join(OUT, f"{name}.fbx"))
        package_scene(name)


def main():
    build_roll()
    build_side()
    build_pedestal()
    build_refine_sign()
    build_upgrade_sign()
    build_ores()
    with open(os.path.join(OUT, "_hipoly_ok.txt"), "w", encoding="utf-8") as f:
        f.write("ok")
    print("ALL_BUILT")


if __name__ == "__main__":
    main()
