# Mobile installation and responsive verification

Date: 2026-11-18. Workspace: `F:\eye-main`.

## Implemented

- Root-scoped RTL Persian web app manifest, standalone launch, theme metadata, Apple home-screen metadata, and 192/512px application icons derived from the existing hospital logo. Maskable artwork has safe padding.
- Install action on sign-in and the application header. Native prompt is used when provided by the browser; otherwise the dialog explains Android or iPhone installation. Dismissal, repeated clicks, installed display mode, focus restoration, and insecure-context guidance are handled.
- A production-only network service worker provides a static 503 connection page when document navigation fails. It does not cache resources, authentication, patient data, API responses, or attachments, and does not intercept API, hub or print navigation. No background write queue or forced reload of open forms was added.
- Shared phone/tablet layout improvements: safe-area spacing, a two-row phone header that cannot shrink over content, readable dashboard amounts and wrapping statistics, 44px principal touch controls, 16px mobile inputs, wrapping filters/tabs/actions, contained table scrolling and constrained grids.
- Mobile navigation is inert while closed. Opening traps keyboard focus, disables background interaction/scrolling, and Escape restores focus to the opener.
- Form dialogs retain unsaved-edit protection and receive a sticky close header and mobile padding. Floating calendars, menus and selectors are constrained to the visual viewport and reposition when the mobile keyboard changes it.
- Invoice and optical order entry tables now scroll locally. Clinical custom inputs and dossier order controls receive mobile sizing. Print previews scroll inside their own region without resizing the printed sheet.
- ASP.NET explicitly serves `.webmanifest` as `application/manifest+json`, preserving existing no-cache revalidation of unhashed files and immutable hashed assets.

The existing palette, Persian RTL, routes, permission checks, business operations, audio-station ownership behavior and read-only mirror restriction were retained. No database migration was needed.

## Completed checks

| Check | Result | Evidence |
| --- | --- | --- |
| Frontend TypeScript and production Vite build | Passed | `.work/mobile-pwa-20261118/frontend-build.txt` |
| .NET 10 API build, no restore | Passed, zero errors; three warnings in pre-existing inventory code | Build command recorded below |
| Existing sidebar regression suite | 21 passed | `.work/mobile-pwa-20261118/regression-tests.txt` (sidebar cases) |
| New PWA and dashboard regression suite | 9 passed | `.work/mobile-pwa-20261118/pwa-regression-final.txt` |
| 38 production application routes at 320, 390, 768, 1024, 1440, 1920px | 228 route/viewport checks; zero runtime errors or unexpected root/main horizontal overflow | `.work/mobile-pwa-20261118/responsive-audit-consolidated.json` |
| Dialogs, unsaved edits, settings/accounting tabs, date picker, selector, mobile navigation and restricted-user routing | 28 passed | `.work/mobile-pwa-20261118/interactions.json` |
| 15 report views and three clinical order dialogs at 320, 390, 768px, with runtime checks | 57 passed | `.work/mobile-pwa-20261118/sections.json` |
| Five print page types at 320, 390, 768, 1440px | 20 passed; no unexpected document overflow; A4/A5 sheet dimensions and vertical fit checked in print media | `.work/mobile-pwa-20261118/print-check.json` |
| Built application service worker, offline navigation, reconnect/retry, icon sizes, prompt dismissal and iPhone instructions | Passed | `.work/mobile-pwa-20261118/pwa-check.json` |
| Browser installability in a separate persistent Edge profile | No installability errors | `.work/mobile-pwa-20261118/installability.json` |

The 228-check record combines the four wider viewport runs with the final two phone viewport runs after the hidden-label containment fix. The print checks cover dossier, refraction slip, optical slip, operation card and invoice. The sign-in page is covered separately by the PWA tests.

Responsive fixes were made from source inspection, DOM measurements and direct screenshot review. An independent evaluator agent was attempted but had no executable/file/browser tools; no independent evaluator verdict is claimed.

## Reproduce focused regression checks

Start the development frontend in one terminal:

```powershell
Set-Location F:\eye-main\frontend
npm.cmd run dev -- --host 127.0.0.1 --port 5179 --strictPort
```

In another terminal:

```powershell
Set-Location F:\eye-main\frontend
$env:ALSHIFA_E2E_BASE = 'http://127.0.0.1:5179'
npx.cmd playwright test e2e/mobile-pwa.spec.ts e2e/sidebar.spec.ts --reporter=line
npm.cmd run build
Set-Location F:\eye-main
.\.tools\dotnet\dotnet.exe build backend/src/AlShifa.Api/AlShifa.Api.csproj --no-restore
```

The focused browser tests mock API responses and require no hospital credentials or database. Extended audit scripts are retained with the local evidence under `.work/mobile-pwa-20261118/scripts/`.

## Screenshots

- [Phone dashboard](../.work/mobile-pwa-20261118/dashboard-390.png)
- [Desktop dashboard](../.work/mobile-pwa-20261118/dashboard-1440.png)
- [Phone reception](../.work/mobile-pwa-20261118/_reception-390.png)
- [Clinical dossier](../.work/mobile-pwa-20261118/clinical-dossier-320.png)
- [iPhone install instructions](../.work/mobile-pwa-20261118/iphone-install-guide.png)
- [Disconnected launch](../.work/mobile-pwa-20261118/offline-390.png)

The application has an internal scrolling surface; these screenshots show the current viewport, not every vertical section of the page at once. All screenshot records use synthetic patients and amounts.

## Limits and release boundary

- Browser runs used desktop Microsoft Edge with resized viewports and touch simulation where specified. The iPhone guide was checked with an iPhone user agent, not the Safari engine or a physical phone. Physical Android/iPhone installation, the on-screen keyboard, camera/file selection and hardware printing remain deployment/device acceptance checks.
- Tests exercised representative populated records, empty states, long labels and large amounts; they do not prove every possible medical record, business state or form-submission workflow.
- The live domain, TLS certificate, reverse proxy, production accounts and actual read-only/operational topology were not accessed or changed. No production deployment was performed.
- The director's address may point to the read-only mirror. Installing a home-screen icon does not change it into the hospital's operational server. Staff write access requires the correct operational address and their existing permissions.
- This checkout has no `.git` directory. Existing source files were copied before modification into `.work/mobile-pwa-20261118/before/`; unrelated releases, databases and installer packages were left intact.

See [installation guide](mobile-installation.md) for staff and post-deployment checks.
