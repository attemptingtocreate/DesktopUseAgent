# Agent guidance (DesktopUseAgent)

DesktopUseAgent is available to Cursor agents via the MCP server id **`desktopuseagent`** when configured (Mode B: Cursor spawns the Node stdio gateway → named pipe → Windows Agent).

**No API key required** for Cursor agents to call desktop tools. You do **not** need OpenAI or Anthropic keys in Control Center for Mode B. Control Center + Windows Agent must be running.

## Tool preference

Prefer semantic tools (`desktop_*`, `ui_*`, `window_*`, `browser_*`, adapters) over raw `input_*` / `vision_*`. Inspect before acting; do not invent coordinates.

**Discord (speed path):** For “open Discord and join voice channel X in server Y”, call **`discord_join_voice`** once (`channel` required, `server` / `monitor` / `placement` optional). Do **not** screenshot-hunt server icons, do **not** `ui_find` Discord (empty Chromium tree), and do **not** multi-step Ctrl+K yourself — the adapter owns that playbook.

Consequential actions may need Control Center approval — honor allow/deny.

## Setup

See [docs/cursor-setup.md](docs/cursor-setup.md). Recommended: `.\scripts\configure-cursor.ps1 -Scope user`.
