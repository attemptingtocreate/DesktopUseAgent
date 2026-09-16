bl_info = {
    "name": "DesktopUseAgent Bridge",
    "author": "DesktopUseAgent",
    "version": (1, 0, 0),
    "blender": (3, 6, 0),
    "location": "View3D > Sidebar > DesktopUseAgent",
    "description": "Opt-in loopback bridge for DesktopUseAgent live Blender control",
    "category": "System",
}

import bpy

from . import bridge


class DESKTOPUSEAGENT_OT_EnableBridge(bpy.types.Operator):
    bl_idname = "desktopuseagent.enable_bridge"
    bl_label = "Enable Agent Bridge"
    bl_description = "Opt in to poll the local DesktopUseAgent Blender bridge (127.0.0.1 only)"

    def execute(self, context):
        bridge.set_polling_enabled(True)
        bridge.ensure_timer()
        self.report({"INFO"}, "DesktopUseAgent bridge polling enabled")
        return {"FINISHED"}


class DESKTOPUSEAGENT_OT_DisableBridge(bpy.types.Operator):
    bl_idname = "desktopuseagent.disable_bridge"
    bl_label = "Disable Agent Bridge"
    bl_description = "Stop polling the DesktopUseAgent bridge"

    def execute(self, context):
        bridge.set_polling_enabled(False)
        self.report({"INFO"}, "DesktopUseAgent bridge polling disabled")
        return {"FINISHED"}


class DESKTOPUSEAGENT_PT_Panel(bpy.types.Panel):
    bl_label = "DesktopUseAgent"
    bl_idname = "DESKTOPUSEAGENT_PT_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "DesktopUseAgent"

    def draw(self, context):
        layout = self.layout
        if bridge.is_polling_enabled():
            layout.label(text="Bridge: enabled", icon="LINKED")
            layout.operator("desktopuseagent.disable_bridge", icon="CANCEL")
        else:
            layout.label(text="Bridge: disabled", icon="UNLINKED")
            layout.operator("desktopuseagent.enable_bridge", icon="PLAY")


classes = (
    DESKTOPUSEAGENT_OT_EnableBridge,
    DESKTOPUSEAGENT_OT_DisableBridge,
    DESKTOPUSEAGENT_PT_Panel,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bridge.register()


def unregister():
    bridge.unregister()
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
