# Local authority and online command queue

The hospital database is authoritative. LAN clients use the published PWA and the hospital API without external internet. A disconnected phone cannot write without reaching the hospital server; patient data is not cached in the service worker.

`Sync:Url` and `Sync:Key` enable the existing signed replication worker (five minutes by default). Local capture stays on even while the destination is unconfigured. `SyncChanges` commits with every tracked business write; the worker removes entries only after the online acknowledgement. Existing snapshot, schema checks, attachment verification and retry behavior are retained.

`Mirror:Enabled=true` now means online mode, not view-only permissions. Keep the existing read-only application database account and the separate `Mirror:SyncConnection` writer. `OnlineAccess` still provisions independent passwords and mandatory 2FA. Role permissions remain unchanged: a Manager receives only the actions already granted to that role.

Online database workflows return **202 Accepted**, a command ID and a status URL. The UI shows an explicit pending notice and the last 50 requests belonging to the signed-in user. Queuing is never reported as a successful patient/payment save. The command table is not replicated, pruned or reset with mirrored business data.

The same sync worker fetches pending commands over HMAC-authenticated HTTPS. The local server resolves the current local user and runs the actual route, permission checks, validation and workflow. Its outer transaction includes the business effects, existing idempotency record, replication outbox and command receipt. Nested workflow transactions use savepoints. A crash before commit leaves no business effects; a lost result after commit replays the receipt without executing again. Results are acknowledged online after replication completes.

Conflict handling uses the existing local workflow: versioned edits check the entity version; reviewed create/payment routes validate duplicates, current balances and stock locally under their existing locks. Unrelated local activity therefore does not reject these commands. Commands without that concurrency contract use a conservative fallback: the last acknowledged sequence must match and the local outbox must be empty, locked serializably during execution. Conflicts are final rejections without partial writes; refresh, inspect current data and submit a new request with a new key. There is no automatic last-writer-wins merge of financial, stock or patient data. The original client idempotency key is retained across both transports. A retry after confirmation receives the original response.

Supported online commands cover patient/reception, clinical, finance, stock, staff and directory database workflows. Physical call/audio control, files, credentials, backups, imports and host configuration remain local operations. This restriction preserves their existing behavior and avoids queuing nontransactional effects. Original routes remain fully available locally.

## Deployment boundary

This change does not migrate an installed database or deploy either server. Take verified backups and migrate both copies to `BidirectionalCommands` through the existing guarded deployment process. Deploy online first, then local; schema mismatch stops replication safely during the transition. Do not drop or purge local command receipts while corresponding online requests may be retried. Existing audit, ledger and outbox retention rules continue to apply.

Before activation verify command permissions, patient conflict rejection, payment/stock deduplication, rollback, restart, loss of the result acknowledgement, internet recovery and PWA behavior on a real phone. Offline development tests are not physical-site acceptance.

## Trusted LAN address

The approved local address is `https://lan-shafa.novatechsoft.com/login`; the existing online/sync hostname remains `shafa.novatechsoft.com`. DHCP must reserve the hospital server address and advertise internal DNS resolving only the LAN hostname to that address.

`prepare-trusted-lan.ps1` now prepares the network plan or, with `-Activate`, provisions public DNS-01 certificates directly for the existing Kestrel endpoint. The former Caddy template is replaced; no reverse proxy or parallel HTTPS service is needed. The installer invokes the same activation flow. A pinned Posh-ACME package and a protected SYSTEM task handle issuance/renewal with the zone-scoped Cloudflare token. Endpoint configuration changes atomically and previous configuration is retained. Database and Sync configuration are not rewritten.

Clients install only the existing PWA. The service worker retains no patient/API data. External internet is needed on the server for issuance and timely renewal, while LAN operation uses internal DNS and the already valid certificate. See [the deployment and operator guide](windows-lan-installation.md).
