# DesktopUseAgent Blender Add-on

Opt-in loopback bridge for live Blender scene/selection/render operations from DesktopUseAgent.

## Requirements

- Blender 3.6+
- DesktopUseAgent Windows agent running locally
- User must enable bridge polling from the View3D sidebar (DesktopUseAgent tab)

## Install

```powershell
.\scripts\install-blender-addon.ps1
```

Then in Blender: Edit → Preferences → Add-ons → search "DesktopUseAgent" → enable.

## Enable live control

1. Open Blender with your scene.
2. Open the View3D sidebar (N) → DesktopUseAgent tab.
3. Click **Enable Agent Bridge**.

The add-on polls `127.0.0.1` only. Live `execute_python` requires `confirm=true`, a 32KB source cap, and restricted builtins; prefer structured mesh ops when possible.

## Security

- Loopback-only HTTP to the agent-owned bridge
- Authenticated with a DPAPI-protected token (never logged)
- Allowlisted structured operations only
- Opt-in enable; no remote control until the user enables polling
