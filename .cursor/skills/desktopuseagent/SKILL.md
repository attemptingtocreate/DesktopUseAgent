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

## Preferred tool order

1. **Semantic inspect:** `desktop_get_state`, `desktop_describe`, `window_list`, `ui_*` find/tree
2. **Act:** `ui_*`, `window_*`, `browser_*`, adapter tools, `process_launch` as appropriate
3. **Avoid inventing coordinates;** use semantic refs from inspection
4. **Last resort:** `input_*` / `vision_*` only when semantic paths fail

Speed exception: simple “open this URL” may use `process_launch` or `browser_open_tab` immediately.

## First calls

Typical start:

1. `desktop_get_state` — confirm agent + desktop context
2. Then `window_list` or targeted `ui_*` / `browser_*` for the task

## Permissions

Consequential actions (clicks, writes, launches, `plan_execute`, `desktop_batch`) may need **Control Center approval**. Honor denials; never bypass the agent gate.

## More

See `docs/cursor-setup.md` in the DesktopUseAgent repo or release docs.
