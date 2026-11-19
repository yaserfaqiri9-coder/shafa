---
name: alshifa-safe-coding
description: Use for coding, debugging, testing, reviewing, database migration, or security work in the AlShifa eye-hospital application. Apply its Vue 3, ASP.NET Core, MySQL, privacy, clinical, finance, concurrency, and safe-test workflow before changing this project.
---

# AlShifa safe coding

## Before changing code

1. Read the project root `AGENTS.md` and relevant sections of `CLAUDE.md`; inspect current source before relying on older documentation. Keep the change scoped to the request and preserve user modifications.
2. Identify the affected feature, API contract, roles, data ownership, transaction boundaries, related Vue state, and relevant existing tests. Backend business rules and authorization are the source of truth.
3. Treat patient and financial records, screenshots, credentials, exports, backups, sync payloads, and test logs as sensitive. Do not upload, print, or copy real data into examples; use synthetic data. Do not open secrets merely to inspect the project.
4. Distinguish confirmed facts from assumptions. Ask the hospital, clinical owner, or accountant when the requested behavior changes an unresolved clinical or financial decision.

## Implementation guardrails

- Respect the modular monolith: use existing feature, DTO, service, validation and authorization patterns rather than adding extra architecture.
- Preserve server-side access policies, audit and patient-level checks. Never rely only on UI visibility. Keep confidential data out of events, logs and URLs.
- For money, stock and record edits, inspect transaction scope, locks, optimistic concurrency, HTTP 409 handling, idempotency, immutable ledger reversal, and retry behavior. Do not silently change established accounting rules.
- For schema changes, write a forward migration where appropriate; do not casually rewrite an applied migration. Real-data deployment requires explicit authorization, a verified restorable backup and a documented rollback plan. MySQL DDL cannot be assumed to roll back with the transaction.
- The online mirror supports queued commands when configured. A 202/pending response is not a confirmed business save. Check current sync behavior and deployment configuration instead of relying on older read-only descriptions.
- Respect Dari/RTL, Afghanistan calendar/timezone, accessibility and existing design conventions when relevant.

## Verification

- For `frontend` on this Windows machine, use `npm.cmd run typecheck`, `npm.cmd run lint`, and, where relevant, `npm.cmd run test:unit`. The lint command reports existing findings; do not suppress or auto-fix them merely to obtain a green result.
- Use the .NET 10 SDK pinned by `backend/global.json` via `.tools/dotnet/dotnet.exe`. `scripts/dev-env.ps1` loads a secret and database connection; do not dot-source it for static checks alone.
- Follow the focused test mapping and execution constraints in `CLAUDE.md`, including no unnecessary repeats. If a needed safety-critical class is omitted from the mapping, flag the conflict and obtain the needed instruction instead of silently omitting it.
- Before integration or E2E tests, positively verify the test server, host, port, schema, privileges, API target and lack of overlapping test runs. `ApiFixture` can empty/delete a database and currently accepts `_dev` and `_load` suffixes; suffix checks alone do not establish safety. Migration tests may use separate databases. Sync tests can create/drop a MySQL account and therefore require a dedicated isolated MySQL instance. Do not run them against the hospital server or installed database on port 3308.
- E2E can create or modify patients, users, invoices and payments. Confirm both the frontend and backend targets are isolated and use synthetic data. Avoid capturing real patient information in screenshots and reports.
- Report what passed, what failed, what was not run and why. Never imply that typechecking, linting or tests prove clinical correctness, privacy compliance or production readiness.

## Example tasks

- For a payment change, inspect idempotency and ledger reversal, identify the exact finance and concurrency tests, and verify the test environment before execution.
- For a patient-form change, inspect authorization, validate the Vue and API contract, run safe local checks, and avoid real patient records.
- For a migration, check the current model and migration history without connecting to real data, plan backward/forward compatibility, and require a separate safe deployment and backup plan.
