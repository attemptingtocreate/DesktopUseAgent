# DesktopUseAgent

**Semantic desktop automation for Windows — Cursor-first, vision as fallback.**

DesktopUseAgent gives AI agents a structured, permissioned path to your Windows desktop: UI Automation trees, browser CDP, filesystem scopes, named-pipe RPC, and an MCP stdio gateway for Cursor. Screen coordinates and vision capture exist only when semantic paths cannot reach the target.

## Why DesktopUseAgent

| Capability | What you get |
| --- | --- |
| Semantic-first control | `ui.*`, `window.*`, `browser.*`, adapters before raw `input.*` / `vision.*` |
| Local-by-default | Named pipe on-machine; MCP stdio; telemetry off unless you opt in |
| Permissioned | Capability defaults, per-app rules, path scopes, live approval UI |
| Cursor-ready | Official MCP server maps tools to the Windows Agent pipe |
| Extensible | Roblox Studio plugin + Blender add-on bridges for creative workflows |

**License:** Apache-2.0 core. Free **Windows x64 developer-preview** releases. Optional paid support/convenience may follow later.

## Supported platform

- **OS:** Windows 10 / 11 **x64** only (this preview does not ship arm64 or x86 builds)
- **Runtime:** .NET 8 (self-contained in release zips)
- **MCP gateway:** Node.js **20+** on `PATH` (Cursor spawns the stdio server)
- **Optional:** Roblox Studio, Blender 3.x+, Chrome/Edge for CDP browser tools

## Prerequisites

1. Windows 10/11 x64 with standard user rights (admin not required for install)
2. [Node.js 20+](https://nodejs.org/) for Cursor MCP integration
3. [Cursor](https://cursor.com/) (recommended primary client)
4. For ChatGPT over the internet: OpenAI Secure MCP Tunnel — see [docs/chatgpt-secure-mcp-tunnel.md](docs/chatgpt-secure-mcp-tunnel.md) (advanced, optional)

## 5-minute install (developer preview)

From a [release zip](artifacts/release/) or after building locally:

```powershell
# Build from source (optional)
.\scripts\build.ps1
.\scripts\pack.ps1
.\scripts\install.ps1

# Or from extracted release zip
.\install.ps1
```

Install location: `%LOCALAPPDATA%\DesktopUseAgent\current`

- Control Center: `%LOCALAPPDATA%\DesktopUseAgent\current\control-center\DesktopUseAgent.exe`
- Windows Agent: `%LOCALAPPDATA%\DesktopUseAgent\current\agent\DesktopUseAgent.Agent.exe`
- MCP gateway: `%LOCALAPPDATA%\DesktopUseAgent\current\mcp\dist\index.js`

Start Menu shortcut: **DesktopUseAgent**

## Cursor setup

Portable, merge-safe MCP configuration:

```powershell
.\scripts\configure-cursor.ps1 -Scope project   # writes .cursor/mcp.json in this repo
# or
.\scripts\configure-cursor.ps1 -Scope user      # writes %USERPROFILE%\.cursor\mcp.json
```

See [docs/cursor-setup.md](docs/cursor-setup.md) for details and the checked-in [`.cursor/mcp.json.example`](.cursor/mcp.json.example) template.

Restart Cursor after configuring MCP. The built-in **desktopuseagent** server exposes semantic desktop tools via stdio → named pipe (`semantic-desktop-agent`).

## ChatGPT (advanced, optional)

Native chat providers in Control Center (OpenAI API key, Anthropic, Ollama) are separate from ChatGPT's MCP app surface.

To connect **ChatGPT** to the same local MCP server through OpenAI's official Secure MCP Tunnel:

1. Install DesktopUseAgent and build/install the MCP entrypoint (above)
2. Follow [docs/chatgpt-secure-mcp-tunnel.md](docs/chatgpt-secure-mcp-tunnel.md)
3. Enable the tunnel in Control Center → Settings only if you need it

## Safety and permissions

- **Emergency stop:** Control Center header button or `Ctrl+Alt+Shift+Esc` (configurable)
- **Default posture:** Ask before consequential actions; deny unknown high-risk paths
- **Pipe ACL:** Named pipe is **current-user only**
- **Filesystem:** Path-prefix rules; no silent full-disk access
- **Telemetry:** **Off by default.** Opt-in via Settings → Privacy or `system.telemetry.set`. Counters stay on disk; nothing is sent off-box
- **Audit:** JSONL logs under your data root (`logs/`), local retention (default 14 days)

Review live status in Control Center → **Health**.

## Roblox Studio setup

Bundled plugin artifact: `plugins/roblox-studio` in the install layout.

```powershell
.\scripts\install-roblox-plugin.ps1
```

Open Roblox Studio; the DesktopUseAgent plugin connects to the agent bridge. Health view shows bridge listening and plugin connection.

## Blender setup

Bundled add-on zip: `plugins/blender/desktopuseagent_blender.zip`.

```powershell
.\scripts\install-blender-addon.ps1
```

Enable the add-on in Blender Preferences. Live bridge status appears in Control Center → Health.

## Benchmark usage

Measure semantic vs fallback paths (local only):

```powershell
.\scripts\benchmark.ps1
```

Results help compare UI Automation / adapter latency against input and vision fallbacks on your machine. See `services/windows-agent` CLI benchmark tests for scenario definitions.

## Semantic vs vision hierarchy

1. **Structured:** UIA tree, patterns, adapter commands (Roblox, Blender, VS Code, Visual Studio)
2. **Browser CDP:** DOM queries and accessibility tree via attached browser tabs
3. **Fallback:** Synthetic input and screen/window capture when semantic paths fail or are unavailable

Control Center → Settings → **Prefer semantic interfaces over fallback input/vision** keeps agents on structured tools when possible.

## Limitations (developer preview)

- Windows **x64** only; no macOS/Linux agent
- Node 20+ still required for MCP stdio (gateway ships with production `node_modules`, but Cursor invokes `node`)
- ChatGPT Secure MCP Tunnel depends on OpenAI Platform provisioning (not guaranteed on all accounts)
- Authenticode signing is optional; unsigned local builds are expected
- UIA coverage varies by application; some apps need browser CDP or vision fallback
- Not hardened for unattended production RPA without additional review

## Roadmap

- Signed stable channel builds
- Broader adapter coverage and planner optimizations
- Optional hosted support / convenience services (does not change Apache-2.0 core)
- arm64 evaluation after x64 preview quality bar

## Versioning

| Artifact | Source |
| --- | --- |
| Product / release | [`VERSION`](VERSION) — e.g. `0.1.0-preview.1` |
| RPC `apiVersion` | `1.12.0` (`RuntimeCompat.ApiVersion`) — protocol compatibility |
| Release zip | `artifacts/release/DesktopUseAgent-<VERSION>-win-x64.zip` |

Tag releases as `v<VERSION>` (must match `VERSION`).

## Build, test, release

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\release.ps1              # builds, packs, zips, writes SHA256
.\scripts\release.ps1 -SkipTests     # packaging only
.\scripts\test-release.ps1         # CI smoke: pack + install to temp
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md).

Architecture and threat model: [docs/architecture.md](docs/architecture.md), [docs/threat-model.md](docs/threat-model.md).

Production operations: [docs/production.md](docs/production.md).
