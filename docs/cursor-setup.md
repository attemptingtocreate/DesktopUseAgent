# Cursor setup (portable)

DesktopUseAgent integrates with Cursor through the **MCP stdio** gateway. Cursor spawns the Node process; the gateway talks to the Windows Agent over the local named pipe.

## Prerequisites

1. DesktopUseAgent installed: `.\scripts\install.ps1` or release zip `install.ps1`
2. **Node.js 20+** on `PATH`
3. MCP built and bundled at `%LOCALAPPDATA%\DesktopUseAgent\current\mcp\dist\index.js`

## Recommended: configure script

From the repository (or copy `configure-cursor.ps1` from a release zip):

```powershell
# Project-scoped (creates/merges .cursor/mcp.json in this repo)
.\scripts\configure-cursor.ps1 -Scope project

# User-scoped (merges %USERPROFILE%\.cursor\mcp.json)
.\scripts\configure-cursor.ps1 -Scope user
```

Behavior:

- Resolves `node.exe` and the MCP entrypoint automatically
- **Merges** into existing MCP config — other servers are preserved
- Backs up an existing file to `mcp.json.bak-<timestamp>` before writing
- Never commits machine-specific paths to the repo

Restart Cursor or reload MCP servers after running the script.

## Manual template

See [`.cursor/mcp.json.example`](../.cursor/mcp.json.example). Replace the placeholder path with your installed MCP entrypoint (forward slashes are fine on Windows).

Example after install:

```json
{
  "mcpServers": {
    "desktopuseagent": {
      "command": "C:/Program Files/nodejs/node.exe",
      "args": [
        "C:/Users/you/AppData/Local/DesktopUseAgent/current/mcp/dist/index.js"
      ]
    }
  }
}
```

## Verify

1. Open Control Center → **Health**
2. Confirm **Windows Agent: connected** and **MCP gateway: ready**
3. In Cursor, enable the `desktopuseagent` MCP server and run a simple tool (e.g. `desktop_get_state`)

## Development layout

When running from source without installing:

```powershell
cd apps\mcp-server
npm ci
npm run build
.\scripts\configure-cursor.ps1 -Scope project -McpEntryPath (Resolve-Path apps\mcp-server\dist\index.js)
```

Ensure the Windows Agent is running (Control Center starts it, or `dotnet run` the agent project).

## Troubleshooting

| Symptom | Check |
| --- | --- |
| MCP server fails to start | Node 20+ installed; `dist/index.js` exists |
| Tools timeout | Agent pipe — Health view; `Ensure agent process` |
| Wrong path after update | Re-run `configure-cursor.ps1` or reinstall |

Do **not** check machine-specific `.cursor/mcp.json` into git; use the example template and this doc instead.
