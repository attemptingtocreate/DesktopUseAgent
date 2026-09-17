# Changelog

All notable changes to the DesktopUseAgent **product release** are documented here. RPC `apiVersion` (`1.12.0`) changes are called out separately when they affect protocol compatibility.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- Cursor production readiness (Mode B): `configure-cursor.ps1` skill install (`-InstallSkill` / `-SkipSkill`), Node 20+ check, clear next steps
- `install.ps1` auto-configures Cursor MCP (`-Scope user`) unless `-SkipCursorConfig`; ships configure script + skill under install root
- Agent guidance: `AGENTS.md`, `.cursor/rules/desktopuseagent-mcp.mdc`, `.cursor/skills/desktopuseagent/SKILL.md`
- Control Center Health: **Configure Cursor MCP (user)** runs configure script asynchronously

### Changed

- Docs lead with: Cursor agents need **no** OpenAI/Anthropic API for desktop tools — only DesktopUseAgent + MCP
- MCP `SERVER_INSTRUCTIONS` address Cursor agents and ChatGPT; `.cursor/mcp.json.example` uses absolute-path placeholders
- Fix `configure-cursor.ps1` merge for empty/`PSCustomObject` `mcpServers`

## [0.1.0-preview.1] - 2026-03-16

### Added

- Developer-preview packaging: `VERSION`, `scripts/release.ps1`, win-x64 release zip + SHA256
- Portable Cursor MCP setup: `scripts/configure-cursor.ps1`, `.cursor/mcp.json.example`
- Control Center **Health** view: agent pipe, MCP gateway, tunnel status, Roblox/Blender bridges, integrity
- Install-root integrity checks (`install-manifest.json` under `%LOCALAPPDATA%\DesktopUseAgent\current`)
- CI (`.github/workflows/ci.yml`) and tag-driven release workflow
- Project documentation: README, SECURITY, CONTRIBUTING, architecture, threat model, cursor setup

### Changed

- Telemetry toggle in Settings wires to `system.telemetry.set` with graceful local fallback
- `install.ps1` locates bundled `layout/` from release zips (no repo assumption)
- MCP and manifest versions aligned with root `VERSION`

### Security

- Telemetry remains **off by default**; local-only counters when enabled

[0.1.0-preview.1]: https://github.com/example/desktopuseagent/compare/v0.0.0...v0.1.0-preview.1
