"""
Clean low-poly roll props (pickaxe-set style: flat shaded, no studs/bricks).
- RollButtonSystem: base + angled ROLL sign + red octagon button
- SideButton: pedestal + yellow octagon button
- PickaxePedestal
- OreSimple: bare grey rock, no shards
"""
import bpy
import math
import os
from mathutils import Vector, Euler, Matrix

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


def cube(name, sx, sy, sz, loc=(0, 0, 0), rot=(0, 0, 0), material=None):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = (sx, sy, sz)
    bpy.ops.object.transform_apply(scale=True)
    if material:
        assign(o, material)
    return o


def cylinder(name, radius, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=8, material=None):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=verts, radius=radius, depth=depth, location=loc, rotation=rot
    )
    o = bpy.context.active_object
    o.name = name
    if material:
        assign(o, material)
    apply_trs(o)
    return o


def ico(name, radius, loc=(0, 0, 0), subdiv=1, material=None, squash=(1.2, 1.0, 0.85)):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdiv, radius=radius, location=loc)
    o = bpy.context.active_object
    o.name = name
    if material:
        assign(o, material)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.transform.resize(value=squash)
    bpy.ops.object.mode_set(mode="OBJECT")
    # slight random faceting: limited displace via scale noise on verts
    apply_trs(o)
    return o


def text_mesh(name, body, size, loc, rot, material, extrude=0.05):
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


def export_fbx(path, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
        bpy.context.view_layer.objects.active = o
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
    )


def save(name):
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, name))


# Colors — soft low-poly, not Roblox brick
COL_BASE = (0.55, 0.62, 0.70)       # soft steel blue-grey
COL_DARK = (0.22, 0.24, 0.28)       # button housing
COL_FRAME = (0.40, 0.48, 0.58)      # sign frame
COL_SIGN = (0.48, 0.58, 0.70)       # sign face
COL_RED = (0.95, 0.18, 0.18)
COL_YELLOW = (1.0, 0.85, 0.15)
COL_INK = (0.12, 0.18, 0.35)        # outline-ish dark blue
COL_WHITE = (0.98, 0.98, 1.0)
COL_ROCK = (0.52, 0.52, 0.55)
COL_PED = (0.45, 0.48, 0.52)
COL_PED_TOP = (0.58, 0.60, 0.64)


def build_roll_system():
    clear_scene()
    m_base = mat("LP_Base", COL_BASE, 0.55, 0.15)
    m_dark = mat("LP_Dark", COL_DARK, 0.4, 0.25)
    m_frame = mat("LP_Frame", COL_FRAME, 0.5, 0.1)
    m_sign = mat("LP_Sign", COL_SIGN, 0.45)
    m_red = mat("LP_Red", COL_RED, 0.35, 0.0, emit=COL_RED, emit_str=1.8)
    m_white = mat("LP_White", COL_WHITE, 0.35)
    m_ink = mat("LP_Ink", COL_INK, 0.4)

    # Wide platform (clean box — no studs)
    base = cube("Base", 3.6, 2.0, 0.35, loc=(0, 0, 0.175), material=m_base)
    # subtle bevel lip
    lip = cube("BaseLip", 3.75, 2.15, 0.08, loc=(0, 0, 0.38), material=m_frame)

    # Angled ROLL sign (left side)
    # Pivot: bottom edge sits on base
    sign_frame = cube("SignFrame", 1.7, 0.16, 1.15, loc=(-0.85, 0, 1.05), rot=(math.radians(-28), 0, 0), material=m_frame)
    sign_face = cube("SignFace", 1.45, 0.06, 0.95, loc=(-0.85, -0.08, 1.08), rot=(math.radians(-28), 0, 0), material=m_sign)
    # Text sits on face
    # Local offset along tilted plane
    txt = text_mesh(
        "RollText",
        "Roll",
        0.55,
        loc=(-0.85, -0.14, 1.12),
        rot=(math.radians(62), 0, 0),
        material=m_white,
        extrude=0.04,
    )

    # Red button housing + octagon button (RIGHT)
    housing = cube("ButtonHousing", 1.05, 1.05, 0.28, loc=(1.05, 0, 0.52), material=m_dark)
    # Button: 8-sided cylinder — origin at TOP so depress moves -Z in local / -Y world height
    btn = cylinder("RollButton", 0.42, 0.22, loc=(1.05, 0, 0.78), verts=8, material=m_red)
    # Origin at resting top-center for clean vertical press
    set_origin(btn, (1.05, 0, 0.78))

    objs = [base, lip, sign_frame, sign_face, txt, housing, btn]
    save("RollButtonSystem.blend")
    export_fbx(os.path.join(OUT, "RollButtonSystem.fbx"), objs)
    return objs


def build_side_button():
    clear_scene()
    m_base = mat("LP_SideBase", COL_BASE, 0.55, 0.15)
    m_dark = mat("LP_SideDark", COL_DARK, 0.4, 0.25)
    m_yel = mat("LP_Yellow", COL_YELLOW, 0.35, 0.0, emit=COL_YELLOW, emit_str=1.6)
    m_accent = mat("LP_Accent", COL_FRAME, 0.5)

    # Tall clean pedestal (no brick recesses)
    column = cube("Pedestal", 1.15, 1.15, 2.2, loc=(0, 0, 1.1), material=m_base)
    band = cube("Band", 1.25, 1.25, 0.12, loc=(0, 0, 1.9), material=m_accent)
    top = cube("TopPlate", 1.2, 1.2, 0.18, loc=(0, 0, 2.25), material=m_base)
    housing = cube("ButtonHousing", 0.95, 0.95, 0.22, loc=(0, 0, 2.42), material=m_dark)
    btn = cylinder("SideButton", 0.38, 0.2, loc=(0, 0, 2.62), verts=8, material=m_yel)
    set_origin(btn, (0, 0, 2.62))

    objs = [column, band, top, housing, btn]
    save("SideButton.blend")
    export_fbx(os.path.join(OUT, "SideButton.fbx"), objs)
    return objs


def build_pedestal():
    clear_scene()
    m_ped = mat("LP_Ped", COL_PED, 0.6)
    m_top = mat("LP_PedTop", COL_PED_TOP, 0.5, 0.05)
    m_dark = mat("LP_PedDark", (0.28, 0.30, 0.34), 0.55)

    base = cylinder("Base", 1.05, 0.22, loc=(0, 0, 0.11), verts=8, material=m_dark)
    mid = cylinder("Column", 0.48, 0.85, loc=(0, 0, 0.65), verts=8, material=m_ped)
    top = cylinder("Top", 0.88, 0.18, loc=(0, 0, 1.15), verts=8, material=m_top)
    # small ring
    ring = cylinder("Ring", 0.62, 0.08, loc=(0, 0, 1.28), verts=8, material=m_dark)

    objs = [base, mid, top, ring]
    save("PickaxePedestal.blend")
    export_fbx(os.path.join(OUT, "PickaxePedestal.fbx"), objs)
    return objs


def build_ore():
    clear_scene()
    m_rock = mat("LP_Rock", COL_ROCK, 0.85)
    # Simple low-poly rock only — no shards/crystals
    rock = ico("Rock", 1.2, loc=(0, 0, 0.85), subdiv=1, material=m_rock, squash=(1.25, 1.05, 0.9))
    # Extra faceting: decimate-ish via limited dissolve? keep ico subdiv1
    # Second overlapping rock lump for silhouette interest (still rock, no spikes)
    lump = ico("Lump", 0.55, loc=(0.45, -0.25, 0.7), subdiv=1, material=m_rock, squash=(1.1, 0.9, 0.8))
    lump2 = ico("Lump2", 0.4, loc=(-0.5, 0.3, 0.55), subdiv=1, material=m_rock, squash=(1.0, 1.15, 0.75))

    objs = [rock, lump, lump2]
    save("OreSimple.blend")
    export_fbx(os.path.join(OUT, "OreSimple.fbx"), objs)
    export_fbx(os.path.join(OUT, "OreTier1.fbx"), objs)
    return objs


def main():
    build_roll_system()
    build_side_button()
    build_pedestal()
    build_ore()
    # Leave ore scene; reopen roll for user preview at end
    build_roll_system()
    with open(os.path.join(OUT, "_roll_build_ok.txt"), "w", encoding="utf-8") as f:
        f.write("RollButtonSystem|SideButton|PickaxePedestal|OreSimple")
    print("BUILT_OK")


if __name__ == "__main__":
    main()
