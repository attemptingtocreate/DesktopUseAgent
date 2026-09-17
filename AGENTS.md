# Agent guidance (DesktopUseAgent)

DesktopUseAgent is available to Cursor agents via the MCP server id **`desktopuseagent`** when configured (Mode B: Cursor spawns the Node stdio gateway → named pipe → Windows Agent).

**No API key required** for Cursor agents to call desktop tools. You do **not** need OpenAI or Anthropic keys in Control Center for Mode B. Control Center + Windows Agent must be running.

## Speed doctrine

Prefer **one high-level verb** that owns the playbook over multi-step `ui_*` / `vision_*` loops. Target sub-10s wall clock for common intents.

## Tool preference

1. **Foundation / adapters:** `app_launch`, `shell_open`, `search_files`, `filesystem_*`, `clipboard_*`, `browser_*`, `office_*`, `media_*`, `discord_*`, `vscode_*`, …
2. **Inspect when needed:** `desktop_get_state`, `window_list`, `ui_*`, `vision_ocr` (structured text — not screenshots to the LLM)
3. **Last resort:** `input_*` / `vision_capture_*` — never invent coordinates

| Intent | Call |
|--------|------|
| Open any app (Win32 or Store/Appx) | `app_launch` `{ name }` |
| Settings / mailto / file / URI | `shell_open` `{ target }` |
| Find a file | `search_files` → `filesystem_open` |
| Email someone | `office_mail_compose` `{ to, subject?, body? }` |
| This week’s calendar | `office_calendar_week` |
| Open Word/Excel/PPT/Outlook | `office_open` `{ app, path? }` |
| Read on-screen text | `vision_ocr` `{ windowId` / `monitor` / `region` / `path` }` |
| Play/pause / volume | `media_transport` / `media_volume` |
| Discord voice | `discord_join_voice` once |
| Lock / sleep / shutdown | `system_power` (`confirm: true` for destructive) |

Consequential actions may need Control Center approval — honor allow/deny. Never auto-click UAC.

## Setup

See [docs/cursor-setup.md](docs/cursor-setup.md). Recommended: `.\scripts\configure-cursor.ps1 -Scope user`.
Control Center default UI is Mode B (Permissions / Health / Settings). Enable **Show advanced UI** only if you need in-app Chat, Agents, MCP tunnel, or Inspector.
