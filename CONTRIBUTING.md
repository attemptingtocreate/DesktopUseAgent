# Contributing to DesktopUseAgent

Thank you for improving DesktopUseAgent. This project ships an Apache-2.0 core with a Windows x64 developer-preview release channel.

## Getting started

1. Clone the repository on **Windows 10/11 x64**
2. Install **Node.js 20+** and **.NET 8 SDK**
3. Build and test:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
cd apps\mcp-server; npm ci; npm test
```

4. Optional packaging smoke test: `.\scripts\test-release.ps1`

## Pull requests

- Keep changes focused; match existing naming and patterns
- Update `CHANGELOG.md` under `[Unreleased]` for user-visible product changes
- Keep `VERSION`, `RuntimeCompat.ProductVersion`, and `apps/mcp-server/package.json` in sync when bumping releases
- Do **not** bump `RuntimeCompat.ApiVersion` without a compatibility plan and tests
- Add or update tests for behavior changes (.NET, MCP, PowerShell smoke, Python plugin when relevant)

## Code of conduct

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## Security

Report vulnerabilities per [SECURITY.md](SECURITY.md). Do not open public issues for sensitive reports.

## License

By contributing, you agree that your contributions are licensed under the [Apache License 2.0](LICENSE).
