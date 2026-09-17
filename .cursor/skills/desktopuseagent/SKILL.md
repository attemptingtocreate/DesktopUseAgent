---
name: desktopuseagent
description: >-
  Control the Windows desktop via DesktopUseAgent MCP tools (desktop automation,
  Windows UI, apps, browser, files). Use when the user asks for DesktopUseAgent,
  MCP desktop tools, UI Automation, or local Windows control without cloud APIs.
---

# DesktopUseAgent (Cursor MCP)

## When to use

Apply this skill when the user wants to inspect or control Windows UI, apps, browser tabs, or files through **DesktopUseAgent** from Cursor (Composer/Agent).

## Prerequisites

- MCP server id **`desktopuseagent`** enabled in Cursor
- **Control Center** open so the Windows Agent named pipe is up
- **No** OpenAI/Anthropic API key required for Cursor → MCP → Agent (Mode B)

If MCP tools are missing: ask the user to run `.\scripts\configure-cursor.ps1 -Scope user`, restart Cursor, and start Control Center.

## Speed doctrine

Everyday intents should finish in **&lt;10s**. Prefer **one foundation/adapter verb** that owns the whole playbook. Do not burn turns on vision → click → vision loops when a deep link, shell open, COM adapter, or OCR exists.

## Preferred tool order

1. **Foundation / adapters:** `app_launch`, `shell_open`, `search_files`, `filesystem_*`, `clipboard_*`, `browser_*`, `office_*`, `media_*`, `discord_*`, `vscode_*`, …
2. **Semantic inspect:** `desktop_get_state`, `window_list`, `ui_*`, **`vision_ocr`** (structured text)
3. **Last resort:** `input_*` / `vision_capture_*` — never invent coordinates

### Common intents → tools

| User says | Tool |
|-----------|------|
| Open Notepad / Calculator / Store apps | `app_launch` `{ name }` (Win32 + Appx/AppsFolder) |
| Open Wi‑Fi settings / PDF / mailto | `shell_open` `{ target }` |
| Find “budget.xlsx” | `search_files` → `filesystem_open` |
| Email bob@… | `office_mail_compose` `{ to, subject?, body? }` |
| What’s on my calendar this week? | `office_calendar_week` |
| Open Word/Excel/PPT | `office_open` `{ app, path? }` |
| Read text on screen | `vision_ocr` (not full-window PNG loops) |
| Play/pause / set volume | `media_transport` / `media_volume` |
| Join Discord voice | `discord_join_voice` once |
| Lock / sleep / shut down | `system_power` (`confirm: true` for destructive) |

### Discord

Call **`discord_join_voice`** once. Do not vision-hunt the server rail; UIA is empty.

## Permissions

Consequential actions may need Control Center approval. Never auto-click UAC.

## Vision

- Prefer **`vision_ocr`** → `{ lines, text }` for reading UI.
- `vision_capture_*` returns path + thumbnail by default (`returnBase64: true` only if needed).

## More

See `docs/cursor-setup.md`. Control Center defaults to Mode B UI (Permissions / Health / Settings).
