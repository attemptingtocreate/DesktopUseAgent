# Production operations

Product release version comes from repo [`VERSION`](../VERSION) (e.g. `0.1.0-preview.1`). RPC `apiVersion` is `1.12.0` (`schemaVersion` 1) — do not confuse the two.

Persisted **state** lives under `%LOCALAPPDATA%\DesktopUseAgent` for new installs (or legacy `%LOCALAPPDATA%\SemanticDesktop`). **Binaries** install to `%LOCALAPPDATA%\DesktopUseAgent\current` with `install-manifest.json` at that root. Override data with `DESKTOPUSEAGENT_DATA`, then `SEMANTIC_DESKTOP_DATA` (compat), or `--data=`. Override install layout with `DESKTOPUSEAGENT_INSTALL`.

Executables: `DesktopUseAgent.exe` (Control Center) and `DesktopUseAgent.Agent.exe` (Windows Agent). The named pipe remains `semantic-desktop-agent`. Override the pipe with `DESKTOPUSEAGENT_PIPE`, then `SEMANTIC_DESKTOP_PIPE` (compat).

## Install / update / sign / release

- `scripts/pack.ps1` publishes win-x64 layout to `artifacts/layout` (`agent`, `control-center`, `mcp`, `plugins`, Cursor `scripts/configure-cursor.ps1` + skill template + `mcp.json.example`).
- `scripts/install.ps1` copies layout to `%LOCALAPPDATA%\DesktopUseAgent\current`, writes `install-manifest.json` (path + SHA-256), creates Start Menu shortcut, copies Cursor support files under `current/scripts` / `mcp/cursor-skill`, and **auto-runs** `configure-cursor.ps1 -Scope user` unless `-SkipCursorConfig`. Works from repo or extracted release zip (`layout/` sibling).
- `scripts/release.ps1` builds/tests (optional), packs, optionally signs, emits `artifacts/release/DesktopUseAgent-<VERSION>-win-x64.zip` + SHA256 (includes configure script, skill template, docs).
- `scripts/configure-cursor.ps1` merge-safe Cursor MCP config (project or user scope). **Prefer `-Scope user`** for all Cursor workspaces. Installs the `desktopuseagent` skill by default for user scope (`-SkipSkill` / `-InstallSkill` for project). Node 20+ required. Cursor agents need **no** OpenAI/Anthropic API key for Mode B.
- `scripts/uninstall.ps1` removes install + data folders and Start Menu shortcut.
- `scripts/sign.ps1` Authenticode-signs layout binaries when `signtool` and `SIGN_THUMBPRINT` or `SIGN_CERT_PATH` are present. Unsigned local builds are expected.

Agent commands: `system.update.check`, `system.update.apply` (staged directory + manifest hashes; default target is **install root**), `system.integrity` (defaults to install root, explicit `root` param still supported).

## Crash recovery

The agent process stays up if a dispatch throws: the crash is written under `crashes/`, the dispatcher is recreated, and the client receives `INTERNAL` with `recovered` semantics. Unhandled exceptions and unobserved tasks are recorded the same way. UI Automation init failures do not take down the process (`uiaAvailable` on `system.status`).

## Telemetry and logs

Telemetry is **off** until `system.telemetry.set { enabled: true }`. Counters stay on disk; nothing is sent off-box. Audit JSONL under `logs/` is pruned by `logRetentionDays` (default 14).

## Schema / permission migration

Missing `state.json` or `schemaVersion: 0` migrates to v1 (install id, telemetry off, retention). Permission policies gain newly introduced capability defaults while preserving existing allow/ask/deny entries.

## IPC

Named pipes use `PipeOptions.CurrentUserOnly`. MCP remains local stdio to that pipe (`semantic-desktop-agent`). For ChatGPT over the internet, use the official OpenAI Secure MCP Tunnel (`tunnel-client`) — see [chatgpt-secure-mcp-tunnel.md](./chatgpt-secure-mcp-tunnel.md). Stdio and the named pipe stay on-machine; `tunnel-client` provides outbound HTTPS to OpenAI's control plane.

## Security review

`system.security_review` reports pipe ACL, telemetry default, unknown-command deny, persisted policy, schema version, and UIA availability. Do not ship a production channel until that review is green and binaries are signed.
