import bpy

from . import validation

ALLOWLIST = {
    "ping",
    "get_scene",
    "get_objects",
    "select_object",
    "export",
    "save",
    "render",
    "import_mesh",
    "apply_transform",
    "join",
}


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
                {"name": o.name, "type": o.type, "location": list(o.location)}
                for o in bpy.data.objects
            ]
        }

    if operation == "select_object":
        name = params.get("name") or params.get("object")
        obj = bpy.data.objects.get(name)
        if obj is None:
            raise RuntimeError("not found")
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
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
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
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

    raise RuntimeError(f"unsupported operation: {operation}")
