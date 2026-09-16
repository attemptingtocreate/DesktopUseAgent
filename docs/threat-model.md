# Threat model

This document summarizes security-relevant design choices for the DesktopUseAgent developer preview on **Windows x64**.

## Assets

- User desktop session (windows, input, filesystem within policy)
- Browser tabs attached via CDP
- Roblox / Blender documents via plugin bridges
- Local policy, audit logs, conversation history (Control Center)
- Optional OpenAI Platform tunnel runtime key (DPAPI)

## Actors

| Actor | Capability |
| --- | --- |
| Local user | Approves/denies actions; configures policy |
| AI client (Cursor MCP, native chat, tunnel-backed ChatGPT) | Invokes tools allowed by session + policy |
| Malware on same machine | Same-user access; competes with user session |
| Remote attacker | No direct agent port; must compromise client or tunnel path |

## Trust boundaries

### Named pipe (primary)

- **ACL:** `PipeOptions.CurrentUserOnly`
- **Assumption:** Only same-user processes connect
- **Failure mode:** Malware running as the user can call the pipe if it discovers the name — treat as equivalent to local code execution risk

### MCP stdio

- Child process of Cursor (or tunnel profile)
- User explicitly configures MCP server entry
- Compromise of MCP host ⇒ tool invocation within policy

### Data root vs install root

- **Data root:** mutable policy/state; not used for binary integrity
- **Install root:** `install-manifest.json` SHA-256 layout verification

### Optional ChatGPT tunnel

- Outbound HTTPS only (official `tunnel-client`)
- Runtime key scoped to Tunnels Read + Use
- Does not expose a public inbound MCP HTTP endpoint

### Plugin bridges (Roblox, Blender)

- Loopback HTTP with per-bridge token
- Plugins poll or receive commands from agent only

## Controls

- Permission engine: capability defaults, app rules, path prefixes
- Live approval UI for Ask decisions
- Emergency stop clears active automation posture
- Telemetry off by default; no off-box exfiltration in core agent
- `system.security_review` for pre-release checklist

## Out of scope (preview)

- Cross-user isolation on shared machines
- Securing against malicious LLM prompt injection beyond permission prompts
- Supply-chain signing (Authenticode optional via `sign.ps1`)

## Reporting

See [SECURITY.md](../SECURITY.md).
