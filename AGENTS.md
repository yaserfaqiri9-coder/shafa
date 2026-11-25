# Repository Guidelines

## Project structure

This is AlShifa Eye Hospital's modular monolith: `backend/src/AlShifa.Api` is ASP.NET Core with feature-oriented endpoints, services, authorization and EF Core/MySQL; `backend/tests/AlShifa.Tests` contains integration tests. `frontend/src/features` contains Vue 3 screens, and shared API, stores, router and design code live under `frontend/src`. Business rules and access checks belong on the backend. The local hospital database is authoritative; inspect `docs/local-online-sync.md` and current sync code before assuming online access is read-only.

For coding or review work, read `.agents/skills/alshifa-safe-coding/SKILL.md` and the relevant portions of `CLAUDE.md`. Read feature-specific documentation and code only as needed. Preserve existing user work, project conventions, lockfiles and pinned versions; do not assume this folder is a Git repository.

## Development and checks

The backend SDK is pinned in `backend/global.json`; use `.tools/dotnet/dotnet.exe` version 10.0.403 on this machine. `scripts/dev-env.ps1` reads local secrets and sets a database connection; do not source it merely to perform static analysis.

From `frontend`, PowerShell can use `npm.cmd run typecheck`, `npm.cmd run lint`, and `npm.cmd run test:unit`. The lint baseline has existing findings; investigate rather than automatically applying fixes or suppressing rules. Vue/TypeScript changes require the checks described in `CLAUDE.md`. Build or tests must use current source; a failed build invalidates `--no-build` results.

## Safety and testing

Never expose credentials, patient records, financial data, uploads, backups, logs or sync payloads in output, commits, screenshots or external services; use synthetic fixtures. Do not run destructive database, migration, sync, or browser tests until the target host, port, schema, privileges, API and concurrent use have been verified as safe. Test fixtures can delete tables and their current guard checks database-name suffixes, not server identity. Sync tests can alter server-level MySQL accounts; require a dedicated isolated instance. Never target the installed hospital database on port 3308.

Follow the focused-test matrix in `CLAUDE.md`. Where it omits a relevant safety-critical test class, flag the gap instead of silently skipping it or running an unlisted class. Migrations of real data require explicit authorization, a verified restorable backup and a rollback plan. Preserve finance idempotency, concurrency, audit, permissions and reversal semantics; ask the relevant owner when clinical or financial policy is unresolved.

## Preserve established hospital Sync during upgrades

The hospital's online/offline Sync is already configured. Future changes and installer builds must preserve its existing URL, secret, interval, installation identity, enabled/paused state, checkpoints, pending changes and command receipts. Never reset, reenroll or regenerate these as part of an application upgrade, and never package another machine's configuration or identity.

An established installation must resume with its original Sync settings after upgrade health checks, including when internet is unavailable during installation. A temporary maintenance hold must not become a permanent compatibility hold. Keep protocol/schema/source validation in the worker; coordinate online and hospital releases before introducing incompatible schema or protocol changes. Build hospital installers with `-UpgradeOnly` and run `scripts/test-sync-upgrade-preservation.ps1` plus `scripts/test-sync-bootstrap.ps1` before delivery. Verify preservation against a restored, isolated database; local/synthetic checks do not establish acceptance on the actual hospital computer.
