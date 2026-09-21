"""
Build low-poly pickaxe-set props in Blender:
SpinLever, OreTier1-10, PickaxePedestal, RefineSign, UpgradeSign
Style: chunky low-poly like the pickaxe/ore reference sheet.
"""
import bpy
import math
import os
from mathutils import Vector, Euler

OUT = r"C:\Users\Administrator\Desktop\DesktopUseAgent\assets\pickaxe-set"
os.makedirs(OUT, exist_ok=True)

def clear_scene():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.curves, bpy.data.fonts):
        for b in list(block):
            block.remove(b)
    # purge unused materials only (avoid dangling refs mid-build)
    for b in list(bpy.data.materials):
        if b.users == 0:
            bpy.data.materials.remove(b)

def mat(name, color, roughness=0.55, metallic=0.0, emission=None, emission_strength=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    if "Metallic" in bsdf.inputs:
        bsdf.inputs["Metallic"].default_value = metallic
    if emission and "Emission Color" in bsdf.inputs:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
        bsdf.inputs["Emission Strength"].default_value = emission_strength
    elif emission and "Emission" in bsdf.inputs:
        bsdf.inputs["Emission"].default_value = (*emission, 1.0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return m

def link_mat(obj, material):
    if obj.data.materials:
        obj.data.materials[0] = material
    else:
        obj.data.materials.append(material)

def apply_obj(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    obj.select_set(False)

def shade_flat(obj):
    for poly in obj.data.polygons:
        poly.use_smooth = False

def make_cube(name, size, loc=(0, 0, 0), rot=(0, 0, 0), material=None):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = size if isinstance(size, Vector) else Vector(size)
    bpy.ops.object.transform_apply(scale=True)
    if material:
        link_mat(obj, material)
    shade_flat(obj)
    return obj

def make_cylinder(name, radius, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=8, material=None):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=radius, depth=depth, location=loc, rotation=rot)
    obj = bpy.context.active_object
    obj.name = name
    if material:
        link_mat(obj, material)
    shade_flat(obj)
    apply_obj(obj)
    return obj

def make_cone(name, radius1, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=6, material=None):
    bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=radius1, radius2=0.0, depth=depth, location=loc, rotation=rot)
    obj = bpy.context.active_object
    obj.name = name
    if material:
        link_mat(obj, material)
    shade_flat(obj)
    apply_obj(obj)
    return obj

def make_ico(name, radius, loc=(0, 0, 0), subdivisions=1, material=None):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdivisions, radius=radius, location=loc)
    obj = bpy.context.active_object
    obj.name = name
    if material:
        link_mat(obj, material)
    shade_flat(obj)
    # squash a bit for rockiness
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.transform.resize(value=(1.15, 0.95, 0.85))
    bpy.ops.object.mode_set(mode='OBJECT')
    apply_obj(obj)
    return obj

def make_text(name, body, size, loc, rot, material, extrude=0.04):
    curve = bpy.data.curves.new(name=name + "_curve", type='FONT')
    curve.body = body
    curve.size = size
    curve.extrude = extrude
    curve.align_x = 'CENTER'
    curve.align_y = 'CENTER'
    obj = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(obj)
    obj.location = loc
    obj.rotation_euler = Euler(rot)
    link_mat(obj, material)
    # convert to mesh for FBX reliability
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.convert(target='MESH')
    obj = bpy.context.active_object
    obj.name = name
    shade_flat(obj)
    apply_obj(obj)
    obj.select_set(False)
    return obj

def join_selected(name):
    objs = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    if not objs:
        return None
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    obj = bpy.context.active_object
    obj.name = name
    return obj

def export_selection(filepath, objects):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:
        if o:
            o.select_set(True)
            bpy.context.view_layer.objects.active = o
    bpy.ops.export_scene.fbx(
        filepath=filepath,
        use_selection=True,
        apply_scale_options='FBX_SCALE_ALL',
        axis_forward='-Z',
        axis_up='Y',
        mesh_smooth_type='FACE',
        add_leaf_bones=False,
        path_mode='COPY',
        embed_textures=False,
    )

def save_blend(name):
    path = os.path.join(OUT, name)
    bpy.ops.wm.save_as_mainfile(filepath=path)
    return path


# ---------- SPIN LEVER ----------
def build_spin_lever():
    clear_scene()
    wood = mat("Wood", (0.42, 0.26, 0.14), 0.7)
    metal = mat("Metal", (0.55, 0.55, 0.58), 0.35, metallic=0.65)
    dark = mat("DarkMetal", (0.18, 0.18, 0.2), 0.4, metallic=0.7)
    plate = mat("Plate", (0.75, 0.72, 0.65), 0.45)
    ink = mat("Ink", (0.05, 0.05, 0.05), 0.3)

    base = make_cube("LeverBase", (1.6, 1.2, 0.35), loc=(0, 0, 0.175), material=dark)
    post = make_cylinder("LeverPost", 0.18, 0.55, loc=(0, 0, 0.55), verts=8, material=metal)
    hinge = make_cylinder("LeverHinge", 0.22, 0.55, loc=(0, 0, 0.95), rot=(math.radians(90), 0, 0), verts=8, material=metal)

    # Arm pivots at hinge — keep as separate object named LeverArm for Roblox animation
    arm = make_cube("LeverArm", (0.22, 0.22, 1.7), loc=(0, 0, 1.75), material=wood)
    # Move origin to bottom (hinge) so CFrame rotation works in Roblox
    bpy.context.view_layer.objects.active = arm
    arm.select_set(True)
    bpy.context.scene.cursor.location = Vector((0, 0, 0.95))
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    arm.select_set(False)

    knob = make_ico("LeverKnob", 0.28, loc=(0, 0, 2.55), subdivisions=1, material=mat("Knob", (0.85, 0.15, 0.12), 0.35))
    # parent knob to arm visually by parenting
    knob.parent = arm
    knob.matrix_parent_inverse = arm.matrix_world.inverted()

    # Sign plate on base front
    sign = make_cube("SpinLabelPlate", (1.5, 0.08, 0.55), loc=(0, -0.7, 0.55), material=plate)
    txt = make_text("SpinLabelText", "Spin Pickaxes", 0.22, loc=(0, -0.76, 0.55), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)

    save_blend("SpinLever.blend")
    export_selection(os.path.join(OUT, "SpinLever.fbx"), [base, post, hinge, arm, knob, sign, txt])
    return "SpinLever"


# ---------- ORES ----------
def rock_base(name, radius, z, grey, subdiv=1):
    return make_ico(name, radius, loc=(0, 0, z), subdivisions=subdiv, material=grey)

def build_ores():
    built = []

    def M():
        return {
            "grey": mat("RockGrey", (0.47, 0.47, 0.49), 0.85),
            "grey2": mat("RockDark", (0.32, 0.32, 0.35), 0.85),
            "gold": mat("GoldCrystal", (1.0, 0.78, 0.2), 0.25, metallic=0.4, emission=(1.0, 0.7, 0.15), emission_strength=0.35),
            "orange": mat("OrangeVein", (1.0, 0.45, 0.12), 0.3, emission=(1.0, 0.4, 0.1), emission_strength=0.5),
            "purple": mat("PurpleCrystal", (0.55, 0.25, 0.9), 0.2, emission=(0.5, 0.2, 0.95), emission_strength=0.6),
            "bluev": mat("BlueVein", (0.25, 0.55, 1.0), 0.25, emission=(0.2, 0.5, 1.0), emission_strength=0.4),
            "green": mat("GreenCrystal", (0.25, 0.9, 0.35), 0.25, emission=(0.2, 0.95, 0.3), emission_strength=0.45),
            "teal": mat("TealCrystal", (0.15, 0.9, 0.85), 0.18, emission=(0.1, 0.95, 0.9), emission_strength=0.7),
            "hell": mat("HellCore", (1.0, 0.12, 0.05), 0.2, emission=(1.0, 0.15, 0.05), emission_strength=2.5),
            "black": mat("BlackRock", (0.08, 0.07, 0.09), 0.7),
            "horn": mat("HornWhite", (0.92, 0.92, 0.95), 0.35),
            "gem": mat("OrangeGem", (1.0, 0.55, 0.1), 0.15, emission=(1.0, 0.5, 0.05), emission_strength=1.2),
            "ice": mat("IceBlue", (0.45, 0.7, 1.0), 0.12, emission=(0.4, 0.65, 1.0), emission_strength=0.8),
            "myth": mat("Mythic", (1.0, 0.92, 0.35), 0.12, emission=(1.0, 0.9, 0.4), emission_strength=2.0),
        }

    # Tier 1 — bare grey rock
    clear_scene()
    m = M()
    r = rock_base("Rock", 1.15, 0.9, m["grey"], 1)
    save_blend("OreSet.blend")  # keep evolving
    export_selection(os.path.join(OUT, "OreTier1.fbx"), [r])
    built.append(1)

    # Tier 2 — jagged larger grey
    clear_scene(); m = M()
    r = rock_base("Rock", 1.25, 0.95, m["grey2"], 1)
    s1 = make_cone("Spike1", 0.35, 1.1, loc=(0.55, 0.2, 1.7), rot=(math.radians(18), 0, math.radians(25)), material=m["grey2"])
    s2 = make_cone("Spike2", 0.28, 0.9, loc=(-0.45, -0.25, 1.55), rot=(math.radians(-15), math.radians(10), math.radians(-20)), material=m["grey"])
    export_selection(os.path.join(OUT, "OreTier2.fbx"), [r, s1, s2])
    built.append(2)

    # Tier 3 — gold crystals + orange veins
    clear_scene(); m = M()
    r = rock_base("Rock", 1.2, 0.95, m["grey"], 1)
    c1 = make_cone("Crystal1", 0.28, 0.95, loc=(0.45, 0.15, 1.75), rot=(math.radians(12), 0, 0), verts=5, material=m["gold"])
    c2 = make_cone("Crystal2", 0.22, 0.75, loc=(-0.35, 0.35, 1.55), rot=(math.radians(-10), 0, math.radians(30)), verts=5, material=m["gold"])
    v1 = make_cube("Vein1", (0.12, 0.12, 1.4), loc=(-0.55, -0.1, 1.1), rot=(math.radians(20), math.radians(15), 0), material=m["orange"])
    export_selection(os.path.join(OUT, "OreTier3.fbx"), [r, c1, c2, v1])
    built.append(3)

    # Tier 4 — purple hex crystals + blue veins
    clear_scene(); m = M()
    r = rock_base("Rock", 1.25, 1.0, m["grey2"], 1)
    p1 = make_cylinder("Crystal1", 0.32, 1.2, loc=(0.35, 0.1, 1.85), verts=6, material=m["purple"])
    p2 = make_cylinder("Crystal2", 0.22, 0.9, loc=(-0.5, 0.25, 1.65), verts=6, material=m["purple"])
    p3 = make_cylinder("Crystal3", 0.18, 0.7, loc=(0.1, -0.55, 1.5), verts=6, material=m["purple"])
    bv = make_cube("Vein", (0.1, 0.1, 1.5), loc=(0.6, -0.2, 1.15), rot=(math.radians(-25), 0, math.radians(10)), material=m["bluev"])
    export_selection(os.path.join(OUT, "OreTier4.fbx"), [r, p1, p2, p3, bv])
    built.append(4)

    # Tier 5 — basalt pillars + green crystals
    clear_scene(); m = M()
    pillars = []
    for i, (x, y, h) in enumerate([(-0.55, 0.2, 2.4), (0.15, -0.45, 2.8), (0.55, 0.35, 2.2), (-0.15, 0.55, 2.0), (0.0, 0.0, 1.6)]):
        pillars.append(make_cylinder(f"Pillar{i}", 0.28 if i < 4 else 0.45, h, loc=(x, y, h * 0.5), verts=6, material=m["grey2"] if i < 4 else m["grey"]))
    g1 = make_cone("Green1", 0.18, 0.55, loc=(-0.55, 0.2, 2.55), verts=5, material=m["green"])
    g2 = make_cone("Green2", 0.15, 0.45, loc=(0.55, 0.35, 2.35), verts=5, material=m["green"])
    g3 = make_cone("Green3", 0.2, 0.5, loc=(0.15, -0.45, 2.95), verts=5, material=m["green"])
    export_selection(os.path.join(OUT, "OreTier5.fbx"), pillars + [g1, g2, g3])
    built.append(5)

    # Tier 6 — large teal crystal on grey base
    clear_scene(); m = M()
    r = rock_base("Rock", 1.15, 0.85, m["grey"], 1)
    t1 = make_cone("TealMain", 0.7, 2.2, loc=(0, 0, 2.2), verts=6, material=m["teal"])
    t2 = make_cone("TealSide", 0.35, 1.3, loc=(0.55, 0.25, 1.7), rot=(math.radians(25), 0, math.radians(30)), verts=6, material=m["teal"])
    export_selection(os.path.join(OUT, "OreTier6.fbx"), [r, t1, t2])
    built.append(6)

    # Tier 7 — hell ore: black base, red core, white horns
    clear_scene(); m = M()
    r = rock_base("Rock", 1.3, 0.95, m["black"], 1)
    core = make_ico("Core", 0.55, loc=(0, 0, 1.35), subdivisions=1, material=m["hell"])
    horns = []
    for i in range(4):
        ang = i * (math.pi * 0.5) + 0.4
        x, y = math.cos(ang) * 0.95, math.sin(ang) * 0.95
        horns.append(make_cone(f"Horn{i}", 0.28, 1.8, loc=(x, y, 2.1), rot=(math.radians(-35), 0, ang), verts=5, material=m["horn"]))
    export_selection(os.path.join(OUT, "OreTier7.fbx"), [r, core] + horns)
    built.append(7)

    # Tier 8 — glowing orange gem
    clear_scene(); m = M()
    r = rock_base("Rock", 1.0, 0.75, m["grey"], 1)
    g = make_cube("Gem", (1.3, 1.3, 1.3), loc=(0, 0, 1.85), rot=(math.radians(45), math.radians(35), math.radians(20)), material=m["gem"])
    export_selection(os.path.join(OUT, "OreTier8.fbx"), [r, g])
    built.append(8)

    # Tier 9 — ice shards
    clear_scene(); m = M()
    r = rock_base("Rock", 1.15, 0.85, m["grey2"], 1)
    shards = []
    for i in range(5):
        ang = i * (math.pi * 2 / 5)
        x, y = math.cos(ang) * 0.55, math.sin(ang) * 0.55
        shards.append(make_cone(f"Shard{i}", 0.22, 1.6 + (i % 3) * 0.25, loc=(x, y, 1.7), rot=(math.radians(8), 0, ang), verts=4, material=m["ice"]))
    export_selection(os.path.join(OUT, "OreTier9.fbx"), [r] + shards)
    built.append(9)

    # Tier 10 — mythic glowing core + halo
    clear_scene(); m = M()
    r = rock_base("Rock", 1.2, 0.9, m["black"], 1)
    core = make_ico("MythCore", 0.65, loc=(0, 0, 1.5), subdivisions=1, material=m["myth"])
    halo = make_cylinder("Halo", 1.15, 0.12, loc=(0, 0, 2.15), verts=12, material=mat("HaloGold", (1.0, 0.95, 0.55), 0.2, emission=(1, 0.9, 0.4), emission_strength=1.5))
    spires = []
    for i in range(6):
        ang = i * (math.pi / 3)
        x, y = math.cos(ang) * 0.85, math.sin(ang) * 0.85
        spires.append(make_cone(f"Spire{i}", 0.16, 1.4, loc=(x, y, 2.3), rot=(math.radians(-20), 0, ang), verts=5, material=m["myth"]))
    save_blend("OreSet.blend")
    export_selection(os.path.join(OUT, "OreTier10.fbx"), [r, core, halo] + spires)
    built.append(10)
    return built


# ---------- PEDESTAL ----------
def build_pedestal():
    clear_scene()
    stone = mat("PedestalStone", (0.4, 0.4, 0.43), 0.7)
    rim = mat("PedestalRim", (0.55, 0.55, 0.58), 0.45, metallic=0.2)
    dark = mat("PedestalDark", (0.22, 0.22, 0.25), 0.65)

    base = make_cylinder("Base", 1.15, 0.28, loc=(0, 0, 0.14), verts=8, material=dark)
    mid = make_cylinder("Column", 0.55, 0.9, loc=(0, 0, 0.75), verts=8, material=stone)
    top = make_cylinder("Top", 0.95, 0.22, loc=(0, 0, 1.3), verts=8, material=rim)
    lip = make_cylinder("Lip", 0.75, 0.12, loc=(0, 0, 1.45), verts=8, material=stone)
    # slight bevel look via inset cube accents
    a1 = make_cube("Accent1", (0.18, 0.18, 0.7), loc=(0.55, 0, 0.75), material=rim)
    a2 = make_cube("Accent2", (0.18, 0.18, 0.7), loc=(-0.55, 0, 0.75), material=rim)

    save_blend("PickaxePedestal.blend")
    export_selection(os.path.join(OUT, "PickaxePedestal.fbx"), [base, mid, top, lip, a1, a2])
    return "PickaxePedestal"


# ---------- REFINE SIGN ----------
def build_refine_sign():
    clear_scene()
    wood = mat("SignWood", (0.45, 0.28, 0.15), 0.7)
    board = mat("SignBoard", (0.72, 0.68, 0.58), 0.5)
    ink = mat("SignInk", (0.08, 0.08, 0.1), 0.3)
    metal = mat("SignMetal", (0.5, 0.5, 0.52), 0.4, metallic=0.5)

    leg_l = make_cube("LegL", (0.22, 0.22, 2.6), loc=(-1.35, 0, 1.3), material=wood)
    leg_r = make_cube("LegR", (0.22, 0.22, 2.6), loc=(1.35, 0, 1.3), material=wood)
    panel = make_cube("Board", (2.8, 0.18, 1.1), loc=(0, 0, 2.5), material=board)
    brace = make_cube("Brace", (2.7, 0.12, 0.15), loc=(0, 0, 1.95), material=metal)
    txt = make_text("RefineText", "REFINE", 0.55, loc=(0, -0.12, 2.5), rot=(math.radians(90), 0, 0), material=ink, extrude=0.03)

    save_blend("RefineSign.blend")
    export_selection(os.path.join(OUT, "RefineSign.fbx"), [leg_l, leg_r, panel, brace, txt])
    return "RefineSign"


# ---------- UPGRADE SIGN ----------
def build_upgrade_sign():
    clear_scene()
    wood = mat("UpWood", (0.4, 0.25, 0.14), 0.7)
    board = mat("UpBoard", (0.25, 0.28, 0.35), 0.55)
    panel = mat("UpPanel", (0.18, 0.22, 0.32), 0.5)
    ink = mat("UpInk", (0.95, 0.95, 0.98), 0.3)
    accent = mat("UpAccent", (0.35, 0.55, 0.95), 0.35)

    leg_l = make_cube("LegL", (0.28, 0.28, 3.2), loc=(-2.1, 0, 1.6), material=wood)
    leg_r = make_cube("LegR", (0.28, 0.28, 3.2), loc=(2.1, 0, 1.6), material=wood)
    main = make_cube("Board", (4.4, 0.22, 2.6), loc=(0, 0, 2.4), material=board)
    header = make_cube("Header", (4.2, 0.12, 0.45), loc=(0, -0.14, 3.45), material=accent)
    # three slot panels (Luck / Pickaxes / Rolls)
    s1 = make_cube("LuckPanel", (1.25, 0.08, 1.7), loc=(-1.4, -0.16, 2.2), material=panel)
    s2 = make_cube("PickaxesPanel", (1.25, 0.08, 1.7), loc=(0.0, -0.16, 2.2), material=panel)
    s3 = make_cube("RollsPanel", (1.25, 0.08, 1.7), loc=(1.4, -0.16, 2.2), material=panel)
    t0 = make_text("HeaderText", "UPGRADES", 0.28, loc=(0, -0.22, 3.45), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)
    t1 = make_text("LuckText", "Luck", 0.28, loc=(-1.4, -0.22, 2.85), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)
    t2 = make_text("PickaxesText", "Pickaxes", 0.22, loc=(0, -0.22, 2.85), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)
    t3 = make_text("RollsText", "Rolls", 0.28, loc=(1.4, -0.22, 2.85), rot=(math.radians(90), 0, 0), material=ink, extrude=0.02)

    save_blend("UpgradeSign.blend")
    export_selection(os.path.join(OUT, "UpgradeSign.fbx"), [leg_l, leg_r, main, header, s1, s2, s3, t0, t1, t2, t3])
    return "UpgradeSign"


def main():
    results = []
    results.append(build_spin_lever())
    results.append("ores:" + ",".join(str(x) for x in build_ores()))
    results.append(build_pedestal())
    results.append(build_refine_sign())
    results.append(build_upgrade_sign())
    # leave UpgradeSign open as last scene; also reopen OreSet for user
    print("BUILT:" + "|".join(results))
    # reopen SpinLever in GUI-friendly final state - actually leave UpgradeSign
    with open(os.path.join(OUT, "_build_ok.txt"), "w", encoding="utf-8") as f:
        f.write("|".join(results))

if __name__ == "__main__":
    main()
