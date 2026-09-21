import bpy, math, os, json
from mathutils import Euler, Vector
OUT = r"C:\Users\Administrator\Desktop\DesktopUseAgent\assets\pickaxe-set"

def clear():
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    for coll in (bpy.data.meshes, bpy.data.curves, bpy.data.fonts):
        for b in list(coll):
            try: coll.remove(b)
            except: pass

def mat(name, rgb, rough=0.45, metal=0.0, emit=None, estr=0.0):
    m=bpy.data.materials.new(name); m.use_nodes=True
    nt=m.node_tree
    for n in list(nt.nodes): nt.nodes.remove(n)
    out=nt.nodes.new('ShaderNodeOutputMaterial'); bsdf=nt.nodes.new('ShaderNodeBsdfPrincipled')
    bsdf.inputs['Base Color'].default_value=(*rgb,1); bsdf.inputs['Roughness'].default_value=rough
    if 'Metallic' in bsdf.inputs: bsdf.inputs['Metallic'].default_value=metal
    if emit and 'Emission Color' in bsdf.inputs:
        bsdf.inputs['Emission Color'].default_value=(*emit,1); bsdf.inputs['Emission Strength'].default_value=estr
    nt.links.new(bsdf.outputs['BSDF'], out.inputs['Surface']); return m

def assign(o,m):
    if o.data.materials: o.data.materials[0]=m
    else: o.data.materials.append(m)
    for p in o.data.polygons: p.use_smooth=False

def bevel(o,w=0.04,s=2):
    bpy.context.view_layer.objects.active=o; o.select_set(True)
    bpy.ops.object.modifier_add(type='BEVEL'); mod=o.modifiers[-1]; mod.width=w; mod.segments=s; mod.limit_method='ANGLE'
    bpy.ops.object.modifier_apply(modifier=mod.name); o.select_set(False)

def cube(name,sx,sy,sz,loc=(0,0,0),rot=(0,0,0),material=None,do_bevel=True):
    bpy.ops.mesh.primitive_cube_add(size=1,location=loc,rotation=rot)
    o=bpy.context.active_object; o.name=name; o.scale=(sx,sy,sz); bpy.ops.object.transform_apply(scale=True)
    if do_bevel: bevel(o)
    if material: assign(o,material); return o

def text_mesh(name,body,size,loc,rot,material,extrude=0.04):
    curve=bpy.data.curves.new(name+'_c',type='FONT'); curve.body=body; curve.size=size; curve.extrude=extrude
    curve.align_x='CENTER'; curve.align_y='CENTER'
    o=bpy.data.objects.new(name,curve); bpy.context.collection.objects.link(o)
    o.location=loc; o.rotation_euler=Euler(rot)
    o.data.materials.append(material)
    bpy.context.view_layer.objects.active=o; o.select_set(True); bpy.ops.object.convert(target='MESH')
    o=bpy.context.active_object; o.name=name
    for p in o.data.polygons: p.use_smooth=False
    bpy.ops.object.transform_apply(location=False,rotation=True,scale=True); o.select_set(False); return o

def mat_color(obj):
    m=obj.data.materials[0] if obj.data.materials else None
    if not m: return (0.7,0.7,0.7)
    if m.use_nodes:
        for n in m.node_tree.nodes:
            if n.type=='BSDF_PRINCIPLED':
                c=n.inputs['Base Color'].default_value; return (float(c[0]),float(c[1]),float(c[2]))
    return (0.7,0.7,0.7)

def package(name):
    parts=[]
    for obj in bpy.context.scene.objects:
        if obj.type!='MESH': continue
        dg=bpy.context.evaluated_depsgraph_get(); eo=obj.evaluated_get(dg); mesh=eo.to_mesh(); mesh.transform(obj.matrix_world); mesh.calc_loop_triangles()
        verts=[[round(v.co.x,4),round(v.co.z,4),round(-v.co.y,4)] for v in mesh.vertices]
        faces=[[int(t.vertices[0]),int(t.vertices[1]),int(t.vertices[2])] for t in mesh.loop_triangles]
        c=mat_color(obj); emit=0.0
        parts.append({'name':obj.name,'verts':verts,'faces':faces,'color':[round(c[0],4),round(c[1],4),round(c[2],4)],'emit':emit})
        eo.to_mesh_clear()
    path=os.path.join(OUT,name+'.mesh.json')
    with open(path,'w',encoding='utf-8') as f: json.dump({'name':name,'parts':parts},f,separators=(',',':'))
    print(name, len(parts), os.path.getsize(path))

# RefineSign
clear()
wood=mat('W',(0.42,0.26,0.14),0.7); board=mat('B',(0.72,0.68,0.58),0.5); ink=mat('I',(0.12,0.18,0.35),0.3); metal=mat('M',(0.5,0.5,0.52),0.4,0.5)
cube('LegL',0.22,0.22,2.6,loc=(-1.35,0,1.3),material=wood)
cube('LegR',0.22,0.22,2.6,loc=(1.35,0,1.3),material=wood)
cube('Board',2.8,0.18,1.1,loc=(0,0,2.5),material=board)
cube('Brace',2.7,0.12,0.15,loc=(0,0,1.95),material=metal,do_bevel=False)
text_mesh('RefineText','REFINE',0.55,loc=(0,-0.12,2.5),rot=(math.radians(90),0,0),material=ink)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT,'RefineSign.blend'))
package('RefineSign')

# UpgradeSign
clear()
wood=mat('UW',(0.42,0.26,0.14),0.7); board=mat('UB',(0.25,0.28,0.35),0.55); panel=mat('UP',(0.18,0.22,0.32),0.5)
ink=mat('UI',(0.98,0.98,1.0),0.3); accent=mat('UA',(0.35,0.55,0.95),0.35)
cube('LegL',0.28,0.28,3.2,loc=(-2.1,0,1.6),material=wood)
cube('LegR',0.28,0.28,3.2,loc=(2.1,0,1.6),material=wood)
cube('Board',4.4,0.22,2.6,loc=(0,0,2.4),material=board)
cube('Header',4.2,0.12,0.45,loc=(0,-0.14,3.45),material=accent)
cube('LuckPanel',1.25,0.08,1.7,loc=(-1.4,-0.16,2.2),material=panel,do_bevel=False)
cube('PickaxesPanel',1.25,0.08,1.7,loc=(0,-0.16,2.2),material=panel,do_bevel=False)
cube('RollsPanel',1.25,0.08,1.7,loc=(1.4,-0.16,2.2),material=panel,do_bevel=False)
text_mesh('HeaderText','UPGRADES',0.28,loc=(0,-0.22,3.45),rot=(math.radians(90),0,0),material=ink,extrude=0.02)
text_mesh('LuckText','Luck',0.28,loc=(-1.4,-0.22,2.85),rot=(math.radians(90),0,0),material=ink,extrude=0.02)
text_mesh('PickaxesText','Pickaxes',0.22,loc=(0,-0.22,2.85),rot=(math.radians(90),0,0),material=ink,extrude=0.02)
text_mesh('RollsText','Rolls',0.28,loc=(1.4,-0.22,2.85),rot=(math.radians(90),0,0),material=ink,extrude=0.02)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT,'UpgradeSign.blend'))
package('UpgradeSign')

# Roll text simpler too
clear()
# reopen roll from file and re-export package only if needed - rebuild roll with simple text
m_base=mat('LB',(0.55,0.62,0.70),0.55,0.15); m_dark=mat('LD',(0.22,0.24,0.28),0.4,0.25)
m_frame=mat('LF',(0.40,0.48,0.58),0.5,0.1); m_sign=mat('LS',(0.48,0.58,0.70),0.45)
m_red=mat('LR',(0.95,0.18,0.18),0.35,0.0,emit=(0.95,0.18,0.18),estr=1.8); m_white=mat('LW',(0.98,0.98,1.0),0.35)
cube('Base',3.6,2.0,0.35,loc=(0,0,0.175),material=m_base)
cube('BaseLip',3.75,2.15,0.08,loc=(0,0,0.38),material=m_frame)
tilt=math.radians(-28)
cube('SignFrame',1.7,0.16,1.15,loc=(-0.85,0,1.05),rot=(tilt,0,0),material=m_frame)
cube('SignFace',1.45,0.06,0.95,loc=(-0.85,-0.08,1.08),rot=(tilt,0,0),material=m_sign,do_bevel=False)
text_mesh('RollText','Roll',0.55,loc=(-0.85,-0.14,1.12),rot=(math.radians(62),0,0),material=m_white)
cube('ButtonHousing',1.05,1.05,0.28,loc=(1.05,0,0.52),material=m_dark)
bpy.ops.mesh.primitive_cylinder_add(vertices=12,radius=0.42,depth=0.22,location=(1.05,0,0.78))
btn=bpy.context.active_object; btn.name='RollButton'; assign(btn,m_red); bevel(btn,0.02,1)
bpy.ops.object.transform_apply(rotation=True,scale=True)
bpy.context.scene.cursor.location=Vector((1.05,0,0.78)); bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT,'RollButtonSystem.blend'))
package('RollButtonSystem')
print('SIGNS_OK')
