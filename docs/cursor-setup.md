# Cursor setup (Mode B — local MCP)

**Cursor agents need no OpenAI/Anthropic API key** to use DesktopUseAgent. Only DesktopUseAgent (Control Center + Windows Agent) must be running, and the `desktopuseagent` MCP server must be configured. Cursor spawns the Node stdio gateway; the gateway talks to the agent over the local named pipe.

## Prerequisites

1. DesktopUseAgent installed: `.\scripts\install.ps1` or release zip `install.ps1`
2. **Node.js 20+** on `PATH` (Cursor invokes `node`)
3. MCP bundled at `%LOCALAPPDATA%\DesktopUseAgent\current\mcp\dist\index.js`

`install.ps1` **auto-runs** `configure-cursor.ps1 -Scope user` unless you pass `-SkipCursorConfig`.

## Recommended: user scope

Use **user** scope so MCP is available across all Cursor workspaces:

```powershell
.\scripts\configure-cursor.ps1 -Scope user
```

From a release zip (after install, script is also under the install root):

```powershell
& "$env:LOCALAPPDATA\DesktopUseAgent\current\scripts\configure-cursor.ps1" -Scope user
```

Or project-only:

```powershell
.\scripts\configure-cursor.ps1 -Scope project
```

Behavior:

- Resolves absolute `node.exe` and MCP entrypoint (Cursor may **not** expand `%LOCALAPPDATA%` — prefer this script over hand-editing env placeholders)
- Requires Node major version ≥ 20 when detectable
- **Merges** into existing MCP config — other servers are preserved
- Backs up an existing file to `mcp.json.bak-<timestamp>` before writing
- **Skill install:** on by default for `-Scope user`; for project, pass `-InstallSkill`. Disable with `-SkipSkill`
  - User: `%USERPROFILE%\.cursor\skills\desktopuseagent\SKILL.md`
  - Project: `<ProjectRoot>\.cursor\skills\desktopuseagent\SKILL.md`

Restart Cursor or reload MCP servers after running the script. Enable **`desktopuseagent`** in Cursor Settings → MCP. Open Control Center so the agent is running.

Control Center → **Health** also offers **Configure Cursor MCP (user)**.

## Manual template

See [`.cursor/mcp.json.example`](../.cursor/mcp.json.example). Use **absolute paths** with your real username (placeholders like `C:/Users/YOU/...`). Do not rely on `%LOCALAPPDATA%` expansion in Cursor MCP config.

## Verify

1. Open Control Center → **Health** — Windows Agent connected, MCP gateway ready
2. Restart Cursor; enable `desktopuseagent` MCP
3. In Agent/Composer, call `desktop_get_state` (no cloud API key required)

## Development layout

```powershell
cd apps\mcp-server
npm ci
npm run build
.\scripts\configure-cursor.ps1 -Scope user -McpEntryPath (Resolve-Path apps\mcp-server\dist\index.js)
```

## Troubleshooting

| Symptom | Check |
| --- | --- |
| MCP server fails to start | Node 20+; `dist/index.js` exists; re-run configure script |
| Tools timeout | Agent pipe — Health → Ensure agent process |
| Wrong path after update | Re-run `configure-cursor.ps1` or `install.ps1` |
| Skill missing | Re-run with `-Scope user` or `-InstallSkill` |

Do **not** commit machine-specific `.cursor/mcp.json`; keep the example, rules, and skills tracked.
