import bmesh
import bpy

from . import validation

ALLOWLIST = {
    "ping",
    "get_scene",
    "get_objects",
    "select_object",
    "export",
    "export_for_roblox",
    "save",
    "render",
    "import_mesh",
    "create_mesh",
    "apply_transform",
    "join",
    "mesh_extrude",
    "mesh_inset",
    "mesh_bevel",
    "mesh_loop_cut",
    "modifier_boolean",
    "modifier_mirror",
    "modifier_array",
    "material_set",
    "uv_unwrap",
    "select_geometry",
    "execute_python",
    "animation_apply",
    "animation_inspect",
    "animation_preview",
    "asset_validate",
}

CREATE_MESH_KINDS = {
    "cube": "primitive_cube_add",
    "uv_sphere": "primitive_uv_sphere_add",
    "ico_sphere": "primitive_ico_sphere_add",
    "cylinder": "primitive_cylinder_add",
    "cone": "primitive_cone_add",
    "plane": "primitive_plane_add",
    "torus": "primitive_torus_add",
}

MAX_EXECUTE_PYTHON_BYTES = 32 * 1024
BOOLEAN_OPS = {"UNION", "DIFFERENCE", "INTERSECT"}
MIRROR_AXES = {"X", "Y", "Z"}
UV_METHODS = {"ANGLE_BASED", "CONFORMAL", "SMART"}


def _require_object(params, mesh_only=True):
    name = params.get("name") or params.get("object")
    obj = bpy.data.objects.get(name) if name else bpy.context.view_layer.objects.active
    if obj is None:
        raise RuntimeError("object not found")
    if mesh_only and obj.type != "MESH":
        raise RuntimeError("object must be a MESH")
    return obj


def _activate_object(obj):
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def _enter_edit(obj):
    _activate_object(obj)
    bpy.ops.object.mode_set(mode="EDIT")


def _ensure_bmesh(obj):
    _enter_edit(obj)
    bm = bmesh.from_edit_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.edges.ensure_lookup_table()
    bm.verts.ensure_lookup_table()
    return bm


def _select_elements_safe(obj, mode, indices):
    bm = _ensure_bmesh(obj)
    bpy.ops.mesh.select_all(action="DESELECT")
    mode = (mode or "FACE").upper()
    selected = 0
    if mode == "VERT":
        bpy.ops.mesh.select_mode(type="VERT")
        for i in indices:
            if 0 <= int(i) < len(bm.verts):
                bm.verts[int(i)].select = True
                selected += 1
    elif mode == "EDGE":
        bpy.ops.mesh.select_mode(type="EDGE")
        for i in indices:
            if 0 <= int(i) < len(bm.edges):
                bm.edges[int(i)].select = True
                selected += 1
    else:
        bpy.ops.mesh.select_mode(type="FACE")
        for i in indices:
            if 0 <= int(i) < len(bm.faces):
                bm.faces[int(i)].select = True
                selected += 1
    bmesh.update_edit_mesh(obj.data)
    return selected


def execute(operation, params):
    if operation not in ALLOWLIST:
        raise RuntimeError(f"operation not allowed: {operation}")

    params = params or {}
    if operation == "ping":
        return {"pong": True, "blenderVersion": bpy.app.version_string}

    if operation == "get_scene":
        scene = bpy.context.scene
        return {
            "name": scene.name,
            "frame_current": scene.frame_current,
            "frame_start": scene.frame_start,
            "frame_end": scene.frame_end,
            "objects": len(bpy.data.objects),
        }

    if operation == "get_objects":
        return {
            "objects": [
                {
                    "name": o.name,
                    "type": o.type,
                    "location": list(o.location),
                    "verts": len(o.data.vertices) if o.type == "MESH" else 0,
                    "faces": len(o.data.polygons) if o.type == "MESH" else 0,
                }
                for o in bpy.data.objects
            ]
        }

    # Declarative animation operations intentionally accept a compact batch of
    # bone poses/keyframes.  This keeps the bridge semantic and avoids one RPC
    # per bone or frame while retaining normal Blender Actions/F-curves.
    if operation == "animation_inspect":
        armature = bpy.data.objects.get(params.get("armature")) if params.get("armature") else bpy.context.object
        if armature is None or armature.type != "ARMATURE":
            raise RuntimeError("armature not found")
        action = armature.animation_data.action if armature.animation_data else None
        return {"armature": armature.name, "bones": [b.name for b in armature.pose.bones],
                "action": action.name if action else None,
                "frameRange": list(action.frame_range) if action else None,
                "markers": [{"name": m.name, "frame": m.frame} for m in bpy.context.scene.timeline_markers]}

    if operation == "animation_apply":
        armature = bpy.data.objects.get(params.get("armature"))
        if armature is None or armature.type != "ARMATURE":
            raise RuntimeError("armature must name an ARMATURE")
        spec = params.get("spec") or params
        name = spec.get("name") or params.get("name")
        if not name:
            raise RuntimeError("animation name is required")
        fps = int(spec.get("fps") or 30)
        duration = float(spec.get("duration") or 0)
        if fps < 1 or fps > 240 or duration <= 0 or duration > 600:
            raise RuntimeError("fps must be 1..240 and duration must be 0..600")
        action = bpy.data.actions.get(name) or bpy.data.actions.new(name)
        action.fcurves.clear()
        if armature.animation_data is None:
            armature.animation_data_create()
        armature.animation_data.action = action
        scene = bpy.context.scene
        scene.render.fps = fps
        scene.frame_start = int(spec.get("frameStart") or 1)
        scene.frame_end = scene.frame_start + max(1, round(duration * fps))
        inserted = 0
        for pose in spec.get("poses", []):
            frame = float(pose.get("frame") or (scene.frame_start + float(pose.get("time", 0)) * fps))
            for bone in pose.get("bones", []):
                pb = armature.pose.bones.get(bone.get("name"))
                if pb is None:
                    raise RuntimeError("missing bone: " + str(bone.get("name")))
                if bone.get("location") is not None: pb.location = bone["location"]
                if bone.get("rotation") is not None:
                    pb.rotation_mode = bone.get("rotationMode", "XYZ")
                    pb.rotation_euler = bone["rotation"]
                if bone.get("scale") is not None: pb.scale = bone["scale"]
                for path in ("location", "rotation_euler", "scale"):
                    if bone.get(path.replace("rotation_euler", "rotation")) is not None or (path == "location" and bone.get("location") is not None) or (path == "scale" and bone.get("scale") is not None):
                        pb.keyframe_insert(data_path=path, frame=frame); inserted += 1
        interpolation = spec.get("interpolation", "BEZIER").upper()
        for curve in action.fcurves:
            for point in curve.keyframe_points: point.interpolation = interpolation
        for marker in spec.get("markers", []):
            marker_name = marker.get("name")
            if not marker_name: continue
            old = scene.timeline_markers.get(marker_name)
            if old: scene.timeline_markers.remove(old)
            scene.timeline_markers.new(marker_name, frame=scene.frame_start + round(float(marker.get("time", 0)) * fps))
        action["desktopuseagent.priority"] = spec.get("priority", "Action")
        action["desktopuseagent.loop"] = bool(spec.get("loop", False))
        return {"action": action.name, "armature": armature.name, "keyframes": inserted, "frameRange": [scene.frame_start, scene.frame_end]}

    if operation == "animation_preview":
        frame = int(params.get("frame") or bpy.context.scene.frame_current)
        bpy.context.scene.frame_set(frame)
        return execute("render", {"output": params.get("output"), "frame": frame, "overwrite": params.get("overwrite", False)})

    if operation == "asset_validate":
        armature = bpy.data.objects.get(params.get("armature")) if params.get("armature") else None
        required = params.get("requiredBones") or []
        missing = [name for name in required if armature is None or armature.pose.bones.get(name) is None]
        output = params.get("output")
        return {"valid": not missing and (not output or __import__('os').path.isfile(output)), "missingBones": missing, "exportExists": bool(output and __import__('os').path.isfile(output))}

    if operation == "select_object":
        name = params.get("name") or params.get("object")
        obj = bpy.data.objects.get(name)
        if obj is None:
            raise RuntimeError("not found")
        _activate_object(obj)
        return {"selected": name}

    if operation == "export":
        output = params.get("output") or params.get("destination")
        fmt = (params.get("format") or "").lower()
        overwrite = bool(params.get("overwrite", False))
        validation.validate_mesh_format(fmt or "obj")
        validation.validate_output_file(
            output,
            overwrite=overwrite,
            allowed_extensions=validation.EXPORT_EXTENSIONS,
        )
        validation.ensure_parent_directory(output)
        if fmt == "fbx":
            bpy.ops.export_scene.fbx(filepath=output)
        elif fmt in ("gltf", "glb"):
            bpy.ops.export_scene.gltf(filepath=output)
        elif fmt == "stl":
            bpy.ops.export_mesh.stl(filepath=output)
        else:
            bpy.ops.wm.obj_export(filepath=output)
        return {"exported": output, "format": fmt}

    if operation == "export_for_roblox":
        output = params.get("output") or params.get("destination")
        overwrite = bool(params.get("overwrite", False))
        fmt = (params.get("format") or "fbx").lower()
        if fmt not in ("fbx", "obj", "gltf", "glb"):
            raise RuntimeError("export_for_roblox format must be fbx, obj, gltf, or glb")
        validation.validate_mesh_format(fmt)
        validation.validate_output_file(
            output,
            overwrite=overwrite,
            allowed_extensions=validation.EXPORT_EXTENSIONS,
        )
        validation.ensure_parent_directory(output)
        mesh_objs = [o for o in bpy.context.scene.objects if o.type == "MESH"]
        if mesh_objs:
            bpy.ops.object.select_all(action="DESELECT")
            for o in mesh_objs:
                o.select_set(True)
            bpy.context.view_layer.objects.active = mesh_objs[0]
            bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        if fmt == "fbx":
            bpy.ops.export_scene.fbx(
                filepath=output,
                use_selection=False,
                apply_scale_options="FBX_SCALE_ALL",
                axis_forward="-Z",
                axis_up="Y",
            )
        elif fmt in ("gltf", "glb"):
            bpy.ops.export_scene.gltf(filepath=output, export_format="GLB" if fmt == "glb" else "GLTF_SEPARATE")
        else:
            bpy.ops.wm.obj_export(filepath=output)
        return {"exported": output, "format": fmt, "preset": "roblox"}

    if operation == "create_mesh":
        kind = (params.get("kind") or params.get("primitive") or "cube").lower()
        op_name = CREATE_MESH_KINDS.get(kind)
        if op_name is None:
            raise RuntimeError(f"unsupported mesh kind: {kind}")
        location = params.get("location") or [0, 0, 0]
        scale = params.get("scale") or [1, 1, 1]
        size = params.get("size")
        kwargs = {
            "location": (float(location[0]), float(location[1]), float(location[2])),
        }
        if size is not None and kind == "cube":
            kwargs["size"] = float(size)
        getattr(bpy.ops.mesh, op_name)(**kwargs)
        obj = bpy.context.view_layer.objects.active
        if obj is None:
            raise RuntimeError("create_mesh failed")
        if scale:
            obj.scale = (float(scale[0]), float(scale[1]), float(scale[2]))
        name = params.get("name")
        if name:
            obj.name = name
            if obj.data:
                obj.data.name = name
        return {
            "created": obj.name,
            "kind": kind,
            "location": list(obj.location),
            "scale": list(obj.scale),
        }

    if operation == "save":
        dest = params.get("destination") or params.get("path")
        overwrite = bool(params.get("overwrite", False))
        validation.validate_blend_destination(dest, overwrite=overwrite)
        validation.ensure_parent_directory(dest)
        bpy.ops.wm.save_as_mainfile(filepath=dest)
        return {"saved": dest}

    if operation == "render":
        output = params.get("output")
        overwrite = bool(params.get("overwrite", False))
        validation.validate_output_file(
            output,
            overwrite=overwrite,
            allowed_extensions=validation.RENDER_EXTENSIONS,
        )
        engine = params.get("engine")
        frame = params.get("frame")
        if engine:
            bpy.context.scene.render.engine = engine
        if frame:
            bpy.context.scene.frame_set(int(frame))
        validation.ensure_parent_directory(output)
        bpy.context.scene.render.filepath = output
        bpy.ops.render.render(write_still=True)
        return {"rendered": output}

    if operation == "import_mesh":
        path = params.get("path") or params.get("input")
        fmt = (params.get("format") or "").lower()
        validation.require_absolute(path, "path")
        validation.validate_mesh_format(fmt or "obj")
        if fmt == "fbx":
            bpy.ops.import_scene.fbx(filepath=path)
        elif fmt in ("gltf", "glb"):
            bpy.ops.import_scene.gltf(filepath=path)
        elif fmt == "stl":
            bpy.ops.import_mesh.stl(filepath=path)
        else:
            bpy.ops.wm.obj_import(filepath=path)
        name = params.get("name")
        if name and bpy.context.selected_objects:
            bpy.context.selected_objects[0].name = name
        collection = params.get("collection")
        if collection and bpy.context.selected_objects:
            coll = bpy.data.collections.get(collection)
            if coll is None:
                coll = bpy.data.collections.new(collection)
                bpy.context.scene.collection.children.link(coll)
            for obj in bpy.context.selected_objects:
                for c in obj.users_collection:
                    c.objects.unlink(obj)
                coll.objects.link(obj)
        return {"imported": path, "format": fmt}

    if operation == "apply_transform":
        name = params.get("name")
        obj = bpy.data.objects.get(name) if name else bpy.context.view_layer.objects.active
        if obj is None:
            raise RuntimeError("object not found")
        _activate_object(obj)
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        return {"applied": obj.name}

    if operation == "join":
        names = params.get("names") or []
        objs = [bpy.data.objects.get(n) for n in names]
        objs = [o for o in objs if o is not None]
        if len(objs) < 2:
            raise RuntimeError("need at least two objects")
        bpy.ops.object.select_all(action="DESELECT")
        for o in objs:
            o.select_set(True)
        bpy.context.view_layer.objects.active = objs[0]
        bpy.ops.object.join()
        return {"joined": objs[0].name}

    if operation == "select_geometry":
        obj = _require_object(params)
        mode = (params.get("element") or params.get("geometryMode") or params.get("mode") or "FACE")
        if isinstance(mode, str):
            mode = mode.upper()
        if mode in ("AUTO", "LIVE", "BACKGROUND"):
            mode = "FACE"
        if mode not in ("VERT", "EDGE", "FACE"):
            raise RuntimeError("mode must be VERT, EDGE, or FACE")
        select_all = bool(params.get("selectAll", False))
        indices = params.get("indices") or params.get("index") or []
        if isinstance(indices, int):
            indices = [indices]
        _enter_edit(obj)
        if select_all:
            bpy.ops.mesh.select_mode(type=mode)
            bpy.ops.mesh.select_all(action="SELECT")
            count = {"VERT": len(obj.data.vertices), "EDGE": len(obj.data.edges), "FACE": len(obj.data.polygons)}[mode]
        else:
            if not indices:
                raise RuntimeError("indices or selectAll required")
            count = _select_elements_safe(obj, mode, indices)
        return {"object": obj.name, "mode": mode, "selected": count}

    if operation == "mesh_extrude":
        obj = _require_object(params)
        _enter_edit(obj)
        geom = params.get("element") or params.get("geometryMode") or params.get("mode") or "FACE"
        if isinstance(geom, str):
            geom = geom.upper()
        if geom in ("AUTO", "LIVE", "BACKGROUND"):
            geom = "FACE"
        if params.get("indices"):
            _select_elements_safe(obj, geom, params.get("indices"))
        value = float(params.get("value") or params.get("offset") or 0.0)
        bpy.ops.mesh.extrude_region_move(
            TRANSFORM_OT_translate={"value": (0.0, 0.0, value)}
        )
        bpy.ops.object.mode_set(mode="OBJECT")
        return {"object": obj.name, "extruded": value}

    if operation == "mesh_inset":
        obj = _require_object(params)
        _enter_edit(obj)
        if params.get("indices"):
            _select_elements_safe(obj, "FACE", params.get("indices"))
        thickness = float(params.get("thickness") or 0.1)
        depth = float(params.get("depth") or 0.0)
        bpy.ops.mesh.inset(thickness=thickness, depth=depth)
        bpy.ops.object.mode_set(mode="OBJECT")
        return {"object": obj.name, "thickness": thickness, "depth": depth}

    if operation == "mesh_bevel":
        obj = _require_object(params)
        _enter_edit(obj)
        geom = params.get("element") or params.get("geometryMode") or params.get("mode") or "EDGE"
        if isinstance(geom, str):
            geom = geom.upper()
        if geom in ("AUTO", "LIVE", "BACKGROUND"):
            geom = "EDGE"
        if params.get("indices"):
            _select_elements_safe(obj, geom, params.get("indices"))
        offset = float(params.get("offset") or params.get("width") or 0.05)
        segments = max(1, int(params.get("segments") or 1))
        bpy.ops.mesh.bevel(offset=offset, segments=segments, affect="EDGES")
        bpy.ops.object.mode_set(mode="OBJECT")
        return {"object": obj.name, "offset": offset, "segments": segments}

    if operation == "mesh_loop_cut":
        obj = _require_object(params)
        _enter_edit(obj)
        cuts = max(1, min(64, int(params.get("cuts") or 1)))
        edge_index = params.get("edgeIndex")
        if edge_index is not None:
            _select_elements_safe(obj, "EDGE", [int(edge_index)])
        # loopcut_and_slide needs a VIEW3D region; fall back to subdivide if unavailable
        try:
            bpy.ops.mesh.loopcut_and_slide(MESH_OT_loopcut={"number_cuts": cuts})
        except Exception:
            bpy.ops.mesh.subdivide(number_cuts=cuts)
        bpy.ops.object.mode_set(mode="OBJECT")
        return {"object": obj.name, "cuts": cuts}

    if operation == "modifier_boolean":
        obj = _require_object(params)
        target_name = params.get("target") or params.get("operand")
        target = bpy.data.objects.get(target_name) if target_name else None
        if target is None or target.type != "MESH":
            raise RuntimeError("target mesh object required")
        op = (params.get("operation") or "DIFFERENCE").upper()
        if op not in BOOLEAN_OPS:
            raise RuntimeError("operation must be UNION, DIFFERENCE, or INTERSECT")
        _activate_object(obj)
        mod = obj.modifiers.new(name=params.get("modifierName") or "Boolean", type="BOOLEAN")
        mod.operation = op
        mod.object = target
        if bool(params.get("apply", True)):
            bpy.ops.object.modifier_apply(modifier=mod.name)
            return {"object": obj.name, "operation": op, "target": target.name, "applied": True}
        return {"object": obj.name, "operation": op, "target": target.name, "applied": False, "modifier": mod.name}

    if operation == "modifier_mirror":
        obj = _require_object(params)
        axis = (params.get("axis") or "X").upper()
        if axis not in MIRROR_AXES:
            raise RuntimeError("axis must be X, Y, or Z")
        _activate_object(obj)
        mod = obj.modifiers.new(name=params.get("modifierName") or "Mirror", type="MIRROR")
        mod.use_axis[0] = axis == "X"
        mod.use_axis[1] = axis == "Y"
        mod.use_axis[2] = axis == "Z"
        if bool(params.get("apply", True)):
            bpy.ops.object.modifier_apply(modifier=mod.name)
            return {"object": obj.name, "axis": axis, "applied": True}
        return {"object": obj.name, "axis": axis, "applied": False, "modifier": mod.name}

    if operation == "modifier_array":
        obj = _require_object(params)
        count = max(2, min(64, int(params.get("count") or 2)))
        relative = params.get("relativeOffset") or params.get("offset") or [1, 0, 0]
        _activate_object(obj)
        mod = obj.modifiers.new(name=params.get("modifierName") or "Array", type="ARRAY")
        mod.count = count
        mod.relative_offset_displace = (float(relative[0]), float(relative[1]), float(relative[2]))
        if bool(params.get("apply", True)):
            bpy.ops.object.modifier_apply(modifier=mod.name)
            return {"object": obj.name, "count": count, "applied": True}
        return {"object": obj.name, "count": count, "applied": False, "modifier": mod.name}

    if operation == "material_set":
        obj = _require_object(params)
        mat_name = params.get("material") or params.get("materialName") or f"{obj.name}_Mat"
        color = params.get("color") or params.get("baseColor") or [0.8, 0.8, 0.8, 1.0]
        if len(color) == 3:
            color = list(color) + [1.0]
        mat = bpy.data.materials.get(mat_name)
        if mat is None:
            mat = bpy.data.materials.new(name=mat_name)
        mat.use_nodes = True
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        if bsdf:
            bsdf.inputs["Base Color"].default_value = (
                float(color[0]),
                float(color[1]),
                float(color[2]),
                float(color[3]),
            )
            if params.get("roughness") is not None:
                bsdf.inputs["Roughness"].default_value = float(params["roughness"])
            if params.get("metallic") is not None:
                bsdf.inputs["Metallic"].default_value = float(params["metallic"])
        if obj.data.materials:
            obj.data.materials[0] = mat
        else:
            obj.data.materials.append(mat)
        return {"object": obj.name, "material": mat.name, "color": list(color)}

    if operation == "uv_unwrap":
        obj = _require_object(params)
        method = (params.get("method") or "ANGLE_BASED").upper()
        if method not in UV_METHODS:
            raise RuntimeError("method must be ANGLE_BASED, CONFORMAL, or SMART")
        _enter_edit(obj)
        bpy.ops.mesh.select_all(action="SELECT")
        if method == "SMART":
            bpy.ops.uv.smart_project(angle_limit=float(params.get("angleLimit") or 66.0))
        else:
            bpy.ops.uv.unwrap(method=method, margin=float(params.get("margin") or 0.001))
        bpy.ops.object.mode_set(mode="OBJECT")
        return {"object": obj.name, "method": method}

    if operation == "execute_python":
        if params.get("confirm") is not True:
            raise RuntimeError("execute_python requires confirm=true")
        source = params.get("source") or params.get("code") or ""
        if not isinstance(source, str) or not source:
            raise RuntimeError("source is required")
        if len(source.encode("utf-8")) > MAX_EXECUTE_PYTHON_BYTES:
            raise RuntimeError(f"source exceeds max of {MAX_EXECUTE_PYTHON_BYTES} bytes")
        # Restricted globals: bpy + common builtins only (no import/open by default in exec locals)
        safe_builtins = {
            "abs": abs,
            "min": min,
            "max": max,
            "range": range,
            "len": len,
            "enumerate": enumerate,
            "list": list,
            "dict": dict,
            "float": float,
            "int": int,
            "str": str,
            "bool": bool,
            "True": True,
            "False": False,
            "None": None,
            "print": print,
        }
        local_ns = {}
        exec(source, {"__builtins__": safe_builtins, "bpy": bpy, "bmesh": bmesh}, local_ns)
        result = local_ns.get("result")
        return {"executed": True, "result": result if result is None or isinstance(result, (str, int, float, bool, list, dict)) else str(result)}

    raise RuntimeError(f"unsupported operation: {operation}")
