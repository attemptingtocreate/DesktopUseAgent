# Architecture

DesktopUseAgent connects AI clients to a local Windows automation runtime through a small set of stable boundaries.

## Components

```
┌─────────────────┐     stdio MCP      ┌──────────────────┐
│ Cursor / other  │ ────────────────► │  MCP gateway     │
│ MCP hosts       │                   │  (Node, local)   │
└─────────────────┘                   └────────┬─────────┘
                                               │ named pipe RPC
┌─────────────────┐                            ▼
│ Control Center  │ ─────────────────► ┌──────────────────┐
│ (WinUI host)    │                     │  Windows Agent   │
└─────────────────┘                     │  (.NET, pipe)    │
                                        └────────┬─────────┘
                                                 │
                    ┌────────────────────────────┼────────────────────────────┐
                    ▼                            ▼                            ▼
              UIA / Win32                   Browser CDP                   Adapters
              filesystem                    (Chrome/Edge)            Roblox, Blender,
              permissions audit                                      VS Code, VS
```

### Windows Agent

- Single-user process listening on `semantic-desktop-agent` (override: `DESKTOPUSEAGENT_PIPE`)
- Dispatches RPC methods: desktop state, UI Automation, browser, adapters, permissions, system.*
- Persists policy, audit logs, optional telemetry counters under **data root** (`DataRootResolver`)
- Install layout (binaries, MCP bundle, plugins) lives under **install root** (`%LOCALAPPDATA%\DesktopUseAgent\current`)

### MCP gateway

- TypeScript stdio server (`apps/mcp-server`) translating MCP tools → agent RPC
- Production dependencies bundled into release `layout/mcp/node_modules`
- Cursor spawns `node …/mcp/dist/index.js`; no inbound network port

### Control Center

- WinUI shell: chat, agents, MCP config, permissions, **Health**, settings
- Starts/observes agent process; optional OpenAI Secure MCP Tunnel lifecycle
- Never bypasses the agent pipe for automation mutations

### Adapters and bridges

- **Roblox Studio:** HTTP bridge + Studio plugin (loopback, token)
- **Blender:** HTTP bridge + Python add-on (loopback, token)
- **VS Code / Visual Studio:** CLI-driven where installed

## Control hierarchy (semantic vs fallback)

1. Adapter / UIA / browser semantic tools
2. Planned batch and graph operations
3. Raw input and vision capture (permission-gated, user-visible)

## Versioning

| Concern | Location |
| --- | --- |
| Product release | `VERSION` → manifest, zip name, `RuntimeCompat.ProductVersion` |
| RPC protocol | `RuntimeCompat.ApiVersion` (`1.12.0`) |
| Persisted schema | `RuntimeCompat.SchemaVersion` (`1`) |

## Packaging flow

`pack.ps1` → `artifacts/layout/` → `install.ps1` copies to `current/` and writes `install-manifest.json` → `release.ps1` zips layout + scripts + docs.

Integrity checks (`system.integrity`) verify manifest hashes against the **install root**, not the data root.

## Data locations

| Path | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\DesktopUseAgent\current` | Binaries, MCP, plugins, manifest |
| `%LOCALAPPDATA%\DesktopUseAgent` (or legacy `SemanticDesktop`) | `state.json`, `policy.json`, `logs/`, `crashes/` |

Override data: `DESKTOPUSEAGENT_DATA` or `--data=`.

See [production.md](./production.md) for operational commands.
