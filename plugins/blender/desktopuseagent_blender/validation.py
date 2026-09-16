import os

MESH_FORMATS = {"obj", "fbx", "gltf", "glb", "stl"}
EXPORT_EXTENSIONS = {".obj", ".fbx", ".gltf", ".glb", ".stl"}
RENDER_EXTENSIONS = {".png", ".jpg", ".jpeg", ".exr", ".tiff", ".tif", ".tga", ".bmp"}


def require_absolute(path, name="path"):
    if not path:
        raise RuntimeError(f"{name} is required")
    if not os.path.isabs(path):
        raise RuntimeError(f"{name} must be absolute")


def validate_output_file(output, overwrite=False, allowed_extensions=None):
    require_absolute(output, "output")
    ext = os.path.splitext(output)[1].lower()
    if allowed_extensions is not None and ext not in allowed_extensions:
        raise RuntimeError(f"output extension '{ext}' is not supported")
    if not overwrite and os.path.exists(output):
        raise RuntimeError("output file exists; set overwrite:true to replace")


def validate_blend_destination(destination, overwrite=False):
    require_absolute(destination, "destination")
    if not destination.lower().endswith(".blend"):
        raise RuntimeError("destination must be a .blend file path")
    if not overwrite and os.path.exists(destination):
        raise RuntimeError("destination file exists; set overwrite:true to replace")


def validate_mesh_format(fmt):
    if fmt not in MESH_FORMATS:
        raise RuntimeError(f"format '{fmt}' is not supported")


def ensure_parent_directory(output_path):
    parent = os.path.dirname(os.path.abspath(output_path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent, exist_ok=True)
