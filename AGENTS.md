# Agent guidance (DesktopUseAgent)

DesktopUseAgent is available to Cursor agents via the MCP server id **`desktopuseagent`** when configured (Mode B: Cursor spawns the Node stdio gateway → named pipe → Windows Agent).

**No API key required** for Cursor agents to call desktop tools. You do **not** need OpenAI or Anthropic keys in Control Center for Mode B. Control Center + Windows Agent must be running.

## Tool preference

Prefer semantic tools (`desktop_*`, `ui_*`, `window_*`, `browser_*`, adapters) over raw `input_*` / `vision_*`. Inspect before acting; do not invent coordinates.

Consequential actions may need Control Center approval — honor allow/deny.

## Setup

See [docs/cursor-setup.md](docs/cursor-setup.md). Recommended: `.\scripts\configure-cursor.ps1 -Scope user`.
