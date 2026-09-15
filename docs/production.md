# Production operations

Runtime `apiVersion` is `1.12.0` (`schemaVersion` 1). Persisted state lives under `%LOCALAPPDATA%\SemanticDesktop` (override with `SEMANTIC_DESKTOP_DATA` or `--data=`).

## Install / update / sign

- `scripts/pack.ps1` publishes an unpackaged layout to `artifacts/layout`.
- `scripts/install.ps1` copies that layout to `%LOCALAPPDATA%\SemanticDesktop\current` and writes `install-manifest.json` (path + SHA-256).
- `scripts/uninstall.ps1` removes the install root.
- `scripts/sign.ps1` Authenticode-signs layout binaries when `signtool` and `SIGN_THUMBPRINT` or `SIGN_CERT_PATH` are present. Unsigned local builds are expected.

Agent commands: `system.update.check`, `system.update.apply` (staged directory + manifest hashes), `system.integrity`.

## Crash recovery

The agent process stays up if a dispatch throws: the crash is written under `crashes/`, the dispatcher is recreated, and the client receives `INTERNAL` with `recovered` semantics. Unhandled exceptions and unobserved tasks are recorded the same way. UI Automation init failures do not take down the process (`uiaAvailable` on `system.status`).

## Telemetry and logs

Telemetry is **off** until `system.telemetry.set { enabled: true }`. Counters stay on disk; nothing is sent off-box. Audit JSONL under `logs/` is pruned by `logRetentionDays` (default 14).

## Schema / permission migration

Missing `state.json` or `schemaVersion: 0` migrates to v1 (install id, telemetry off, retention). Permission policies gain newly introduced capability defaults while preserving existing allow/ask/deny entries.

## IPC

Named pipes use `PipeOptions.CurrentUserOnly`. MCP remains local stdio to that pipe.

## Security review

`system.security_review` reports pipe ACL, telemetry default, unknown-command deny, persisted policy, schema version, and UIA availability. Do not ship a production channel until that review is green and binaries are signed.
