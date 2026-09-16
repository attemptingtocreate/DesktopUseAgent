# Security Policy

## Supported versions

| Version | Supported | Notes |
| --- | --- | --- |
| `0.1.x-preview.*` | Yes | Windows x64 developer preview |
| Older / dev builds | No | Build from source at your own risk |

Security fixes target the current developer-preview line on `main`. RPC `apiVersion` `1.12.0` is the supported protocol surface for this preview.

## Reporting a vulnerability

**Do not** open a public GitHub issue for security-sensitive reports.

1. If this repository has [GitHub private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) enabled, use **Report a vulnerability** on the Security tab.
2. Otherwise, contact the maintainers through an existing private channel you already use for this project (for example a maintainer DM or organization security contact). If no channel exists yet, open a **non-security** issue asking maintainers to enable private reporting — do not include vulnerability details in that public issue.

Include:

- Affected version / commit
- Component (Agent pipe, MCP gateway, Control Center, tunnel client, plugin bridge)
- Reproduction steps or proof-of-concept
- Impact assessment (local privilege, data exposure, remote code execution, etc.)

## Response expectations

- We aim to acknowledge reports within a reasonable timeframe, but **no SLA or fixed timeline** is guaranteed for this volunteer preview project.
- We will coordinate on fix validation and disclosure when a fix is available.
- Critical local-only issues (e.g. pipe ACL bypass) are prioritized over optional advanced features (ChatGPT tunnel).

## Threat model summary

DesktopUseAgent is a **local automation agent** with these trust boundaries:

| Boundary | Trust assumption |
| --- | --- |
| Named pipe (`semantic-desktop-agent`) | Same-machine, same-user clients only (`CurrentUserOnly` ACL) |
| MCP stdio | Started by user-approved clients (Cursor, tunnel profile) |
| Data root (`%LOCALAPPDATA%\DesktopUseAgent` or legacy `SemanticDesktop`) | Stores policy, audit logs, optional telemetry counters — **local only** |
| ChatGPT Secure MCP Tunnel | Optional outbound HTTPS to OpenAI; requires Platform runtime key (DPAPI-stored in Control Center) |
| Roblox / Blender bridges | Loopback HTTP with token auth between agent and plugins |

**Primary risks:** malicious or compromised AI clients invoking permitted tools; user-approved path rules that are too broad; exposure of Platform tunnel keys on disk (DPAPI-protected); dependency vulnerabilities in Node/.NET stacks.

**Non-goals for this preview:** multi-user server hardening, internet-exposed MCP HTTP, sandbox escape from arbitrary desktop apps.

## Secrets and ports

- **Do not** commit API keys, tunnel runtime keys, or Roblox/Blender bridge tokens.
- Default bridges bind **loopback** ports; tokens are generated locally.
- Control Center stores provider and tunnel keys with **DPAPI** where implemented — still protect the Windows user account.
- Release CI does not require signing or deployment secrets.

## Secure defaults

- Telemetry **off** by default
- Unknown commands denied at permission layer
- Emergency stop available from Control Center
- Integrity verification via `install-manifest.json` under the install root (`current/`)

See [docs/threat-model.md](docs/threat-model.md) for detail.
