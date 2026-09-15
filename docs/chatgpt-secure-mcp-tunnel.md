# ChatGPT Secure MCP Tunnel

Connect ChatGPT to the local **semantic-desktop** MCP server through OpenAI's official [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) and [`tunnel-client`](https://github.com/openai/tunnel-client). DesktopUseAgent keeps MCP on **local stdio → named pipe** (`semantic-desktop-agent`); `tunnel-client` adds outbound HTTPS to OpenAI's control plane. No custom public HTTP endpoint or ngrok is required.

## Account and entitlement caveat

OpenAI's current [Developer Mode documentation](https://developers.openai.com/api/docs/guides/developer-mode) explicitly lists **ChatGPT Plus, Pro, Business, Enterprise, and Education** as eligible for custom MCP apps on ChatGPT web, including **read and write tools** (write actions still require confirmation by default). Enable Developer Mode under Settings → Security and login, then select Developer Mode apps from the composer + menu in conversations.

That MCP app eligibility is separate from **Secure MCP Tunnel transport**. The [Secure MCP Tunnel guide](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) describes an outbound-only path for private MCP servers and was announced for enterprise rollout; it may not be provisioned for every Platform organization—including some personal Plus accounts. Seeing Developer Mode does **not** guarantee Platform **Tunnels** access, Tunnels management in Platform settings, or a **Connection: Tunnel** option when creating an app. Missing tunnel transport is a Platform/workspace provisioning and association issue, not a DesktopUseAgent stdio server bug.

Platform tunnel permissions (Read, Use, Manage) and ChatGPT Developer Mode are distinct controls. You may need both workspace Developer Mode and Platform tunnel roles, plus tunnel association with the target ChatGPT workspace, before ChatGPT can discover or use a tunnel-backed app.

Some older Help Center articles describe narrower plan eligibility; treat the [developer docs](https://developers.openai.com/api/docs/guides/developer-mode) as canonical for MCP app plans and the [Secure MCP Tunnel guide](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) for transport requirements.

The **Platform runtime API key** is separate from your ChatGPT subscription. Secure MCP Tunnel itself does **not** bill ChatGPT model usage when ChatGPT invokes your app, but a Platform key with tunnel permissions is required for the control plane.

## Prerequisites

1. **Windows 10/11** with DesktopUseAgent installed and running (Control Center + Windows Agent).
2. **Node.js 20+** on `PATH` for the MCP stdio entrypoint.
3. **Built MCP server** at `%LOCALAPPDATA%\DesktopUseAgent\current\mcp\dist\index.js` (via `scripts/install.ps1`) or `apps\mcp-server\dist\index.js` from a dev build.
4. **OpenAI Platform access** with permission to create tunnels and runtime keys.
5. Official **`tunnel-client`** (installed locally; not bundled in this repo).

## Platform URLs

| Resource | URL |
| --- | --- |
| Tunnels management | https://platform.openai.com/settings/organization/tunnels |
| Runtime API keys | https://platform.openai.com/settings/organization/api-keys |
| Organization roles (Tunnels) | https://platform.openai.com/settings/organization/people/roles |
| Organization groups | https://platform.openai.com/settings/organization/people/groups |
| Admin API keys (tunnel CRUD only) | https://platform.openai.com/settings/organization/admin-keys |
| Secure MCP Tunnel guide | https://developers.openai.com/api/docs/guides/secure-mcp-tunnels |
| tunnel-client repository | https://github.com/openai/tunnel-client |

## tunnel_id vs runtime key

| Name | Purpose | Used by |
| --- | --- | --- |
| `tunnel_id` (`tunnel_<32 lowercase hex>`) | Identifies the tunnel object in Platform and ChatGPT | `setup-chatgpt-tunnel.ps1`, profile YAML, ChatGPT connector |
| `CONTROL_PLANE_API_KEY` | Restricted **runtime** key for the long-lived daemon | `tunnel-client doctor`, `tunnel-client run` |
| `OPENAI_ADMIN_KEY` | Admin key for tunnel CRUD only | `tunnel-client admin tunnels …` — **not** for `run` |

When creating `CONTROL_PLANE_API_KEY`, choose **Restricted** and grant Tunnels **Read** + **Use**. Do not use an admin key or an "All" scope key for the daemon.

## Install tunnel-client

From the repo root:

```powershell
.\scripts\install-openai-tunnel-client.ps1
```

This downloads the latest official Windows full-client zip from GitHub releases, verifies SHA-256 against release metadata, extracts to `%LOCALAPPDATA%\DesktopUseAgent\tools\tunnel-client\<tag>`, and maintains a stable junction at `...\tunnel-client\current`. Re-running is idempotent.

## Configure the stdio profile

Create a tunnel in Platform Tunnels settings and copy its id. Platform tunnel IDs must match `^tunnel_[0-9a-f]{32}$` (the literal prefix `tunnel_` followed by exactly 32 lowercase hexadecimal digits).

```powershell
.\scripts\setup-chatgpt-tunnel.ps1 -TunnelId tunnel_0123456789abcdef0123456789abcdef
```

Optional flags:

- `-ProfileName desktopuseagent` (default; must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`)
- `-McpEntryPath C:\path\to\dist\index.js`
- `-ProfileDir C:\path\to\profiles`
- `-TunnelClientPath C:\path\to\tunnel-client.exe`
- `-OpenWebUi` — opens Platform tunnel settings
- `-Force` — regenerate an existing profile

The script generates the official `sample_mcp_stdio_local` profile with the resolved absolute Node executable and MCP entrypoint (forward slashes, quoted only when needed), for example:

```text
C:/Program Files/nodejs/node.exe C:/Users/you/AppData/Local/DesktopUseAgent/current/mcp/dist/index.js
```

It never accepts, prints, or persists API keys. If `CONTROL_PLANE_API_KEY` is already set in your shell, it runs `tunnel-client doctor --explain` locally.

## Start the tunnel

Set the runtime key for **this shell session only**:

```powershell
$env:CONTROL_PLANE_API_KEY = "<your-restricted-runtime-key>"
.\scripts\start-chatgpt-tunnel.ps1
```

Keep this terminal open. Also keep **DesktopUseAgent Control Center** and the **Windows Agent** running — MCP still uses the local named pipe.

## ChatGPT connector setup

1. With `tunnel-client run` healthy, open ChatGPT **Settings → Connectors** (or Developer Mode app creation, depending on plan).
2. Select your Platform tunnel / custom MCP app and confirm the **same `tunnel_id`** as the profile.
3. Grant the app permission to use tools. Consequential DesktopUseAgent actions may still require **local permission confirmation** in Control Center.

## Validate locally

After `tunnel-client run` starts, the daemon exposes operator endpoints on loopback (default health listener):

- `GET /healthz` — process health
- `GET /readyz` — readiness (MCP + control plane)
- `GET /ui` — lightweight admin UI

Example:

```powershell
Invoke-WebRequest http://127.0.0.1:8080/readyz -UseBasicParsing
Start-Process "http://127.0.0.1:8080/ui"
```

The exact port is printed by `tunnel-client doctor --explain` or shown in the run logs. Adjust the port if your profile overrides `health.listen-addr`.

Confirm MCP independently (agent must be running):

```powershell
cd apps\mcp-server
node dist\index.js
# Connect via your usual local MCP client, or rely on tunnel-client stdio wiring.
```

## Troubleshooting

| Symptom | Likely cause | Action |
| --- | --- | --- |
| `doctor` says runtime key missing | `CONTROL_PLANE_API_KEY` unset | Create restricted runtime key; set env var in the same shell before `start-chatgpt-tunnel.ps1` |
| ChatGPT cannot see connector | Daemon not running or wrong `tunnel_id` | Keep `tunnel-client run` foreground; verify Platform tunnel id matches profile |
| Tools fail immediately | Agent not running | Start Control Center; confirm pipe `semantic-desktop-agent` |
| MCP build missing | `dist\index.js` absent | `cd apps\mcp-server; npm install; npm run build` or run `scripts\install.ps1` |
| Write tools blocked or custom app unused | Wrong conversation mode or policy | **Agent Mode** does not use custom developer-mode apps. **Deep Research** invokes only `search`/`fetch` tools. Switch to **Developer Mode** from the composer + menu and select your app explicitly. If write tools still fail, check workspace Developer Mode policy with your admin—not DesktopUseAgent. |
| No **Connection: Tunnel** option | Transport not provisioned | Secure MCP Tunnel may be unavailable for your Platform org/workspace even with Developer Mode. Confirm tunnel association with the target ChatGPT workspace in Platform Tunnels settings and Tunnels Read + Use permissions. Fallback: connect via a conventional **public HTTPS Streamable HTTP** MCP endpoint (SSE or streaming HTTP) that ChatGPT can reach directly—this reflects transport entitlement, not a failure of the local stdio server. **Never** expose full desktop control on an unauthenticated public endpoint; require strong authentication (OAuth, mTLS, network allowlists). Do not use an open ngrok tunnel without access controls. |
| `/readyz` not ready | MCP command failed | Check `node` version ≥ 20; verify MCP path in profile YAML |

## Security boundaries

- **Local trust:** named pipe `semantic-desktop-agent` stays on the current user session; MCP stdio is spawned by `tunnel-client` on the same machine.
- **Outbound only:** `tunnel-client` initiates HTTPS to OpenAI; you do not expose MCP on a public port.
- **Keys:** store runtime keys in your OS secret manager or session env — not in git, profiles committed to the repo, or DesktopUseAgent config.
- **Permissions:** DesktopUseAgent permission policies still gate consequential RPC calls even when ChatGPT invokes tools through the tunnel.
- **No bundled binaries:** OpenAI binaries are downloaded on demand by `install-openai-tunnel-client.ps1`, not committed to this repository.

## Related docs

- [Production operations](./production.md) — install layout and IPC overview
- [ChatGPT Developer Mode](https://developers.openai.com/api/docs/guides/developer-mode)
- [OpenAI Secure MCP Tunnel guide](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)
- [tunnel-client configuration](https://github.com/openai/tunnel-client/blob/master/docs/configuration.md)
