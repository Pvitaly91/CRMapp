# Shipping labels — implementation and acceptance

## Current follow-up: direct NP diagnostics implemented, real acceptance awaiting key

2026-09-27. Started at clean HEAD `2bb07400558934cbd55ffe46c66739dde71659f8`, branch `codex/checkbox-marketplace-orders`; GitHub branch matched. No newer/user work reverted. Production is not a deployment/data source.

### 1. What came from SDK source

Read the user-selected SDK revision `1c7027da068363d9d06792a1e698b025d852b41d`: Marking, InternetDocument, GetDocumentListRequest plus their Request/BaseModel/DocumentListData dependencies. Implemented those candidate requests in C#, without PHP/SDK install or a new package dependency. [Exact sources, guards and limitations](SHIPPING_LABELS_API_CONTRACT.md#current-direct-np-implementation-sdk-derived-awaiting-a-local-key). Specific methods/response shapes/marking URL are **SDK-derived, not official-contract-confirmed**.

Separate NP account name/masked key/stable ID, protected storage and explicit Prom-store binding; manual “Перевірити наявну накладну” for one TTN of the selected Prom order, explicit 1–7-day TTN-creation period, ≤10 pages, cancel/reentry guard. No automatic history scan, guessed exact-search property, key-test-only success, or fallback account search. Settings/key can be saved without printer/Seller/Checkbox. Blank key preserves the old one; rotation retains history/account identity.

PDF candidate downloads use exact NP HTTPS hosts, no redirects or secret-bearing error messages. Native PDFium inspects physical pages and text TTN identity, then the diagnostic preview shows all pages without a print button. Missing text/wrong TTN/unknown completeness cannot silently enter a packet. Existing print geometry, fiscal headers, Checkbox API and receipt printer were not changed.

The separate `NovaPoshtaDirectLabelSource` is connected conditionally to the existing packet source: only an explicitly diagnosed/allowed single-place 100×100 PDF may enter a packet during this session, with a fresh download/inspection before normal batch confirmation. Restart/key rotation clears eligibility. Seller/RD remain; direct NP failure never falls back to another account. This wiring is exercised synthetically but **not yet accepted with real NP**.

### 2–4. Real API / Prom document / identity and places

- **Real NP requests executed: zero.** Current development shipping store was inspected through the existing DPAPI settings store, emitting counts/flags only: zero NP accounts/keys, zero Prom→NP bindings, no RD token. Previous check found two saved Prom tokens and zero Seller accounts. No production secret was copied. Status is **“Очікує локального введення ключа”**.
- **Real label of a Prom-origin TTN obtained: no.** NP account visibility, current marking route success, PDF text/layout/size and account-context access remain unverified. A valid key or tracking response would not prove PDF availability.
- Actual PDFium parsing/identity/size tests used synthetic 100×100/A4 and multi-page fixtures only, not real NP/customer PDFs. Diagnostic UI and images were rendered/visually inspected.
- **Multi-place fullness is not verified.** All received pages remain visible; page count cannot prove all numbered places. Multi-place and absent SeatsAmount currently stay diagnostic-only. No guessed seat-number pattern or “first page is enough” rule was added.
- **Physical print: none.** Real WindowsLabelPrinter algorithm used only its injected synthetic page device. No live StartDoc, spooler or global printer change, shipment/registry/order/fiscal write occurred.

### 5. Regression/build and remaining work

- All previous **215 tests preserved**; **18 new groups**, total **233/233 passed**, Release **0 warnings / 0 errors**. New scenarios cover bounded SDK request/empty-page termination, absent/inaccessible/incomplete samples, optional Ref without substitution, malformed envelopes, forbidden write/extra/duplicate properties, cancellation, secret-bearing failures/redirects/HTML/JSON, native PDF identity/size/off-page text/numeric boundaries, all multi-page diagnostic pages, actual masked-key UI save/manual request/reentry/cancel, DPAPI restart/key rotation/history isolation, direct Prom without Seller and NP→RD→NP through the real backend algorithm as one job. A broken packet does not submit partial pages; existing Seller and All Receipts regressions still pass.
- Separate development self-contained win-x64 single-file PDFium output: `artifacts/development/np-direct-diagnostics/CheckboxBatchPrinter.exe`. Copied-EXE offline launch/restart verifies bundled native rasterization **and the new PDFium text inspector**; no real profile/API/printer is used in this smoke mode. Package verification result is checked before publishing the commit.
- Production EXE SHA256 remains `AB087874FB49E48611A2DF0157E6C0285DB9602DCEEEE09FC200B3F53E7B1992`; production EXE/profile not overwritten, migrated or used for secrets.

Local acceptance: in the new development EXE select an existing Prom order with NP TTN, then “Налаштування наклейок → Нова пошта — пряме підключення”. Add name/key locally, explicitly bind the Prom store, choose the actual **TTN creation** date interval and click “Перевірити наявну накладну”. Inspect account result, exact number/Ref, actual page sizes, text identity and every place. Enter RD token separately for genuine mixed preview. Current SDK route/response assumptions must be revisited if the real server differs, not repaired by guessing other methods. No real API is called by automated tests. Physical printing requires a new explicit approval and user paper confirmation.

Reproducible package command (empty new output folder):

```powershell
dotnet build CheckboxBatchPrinter.sln -c Release
dotnet run --project tests/CheckboxBatchPrinter.Tests/CheckboxBatchPrinter.Tests.csproj -c Release --no-build
dotnet publish src/CheckboxBatchPrinter/CheckboxBatchPrinter.csproj -c Release -r win-x64 --self-contained true -o artifacts/development/np-direct-diagnostics -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:AppChannel=Development -p:SourceRevisionId=<current-development-commit>
```

The previous reports below are historical, not claims about the current code.

---

## Historical documentation-only follow-up at 2bb0740 — NOT COMPLETE

2026-09-27 follow-up started at clean HEAD `7966c8665a9b8667a7c1d366d16b0deabeca6377`, branch `codex/checkbox-marketplace-orders`. GitHub's branch pointed to the same commit. No newer code or user changes were reverted.

The main acceptance criterion **Prom + direct NP without Seller** is not achieved. This follow-up changes documentation only; it does not invent NP routes, add a success-reporting placeholder, change the printing pipeline or claim that the previous synthetic NP/Seller fixtures verify direct NP access.

### Evidence and settings

- Local PowerShell: official NP developer portal returned HTTP 403. Local browser: normal navigation remained on its security-check page. No protection was bypassed. The integration overview confirmed only the key/JSON entry point; it did not supply the model/method/print contract.
- Legacy official devcenter TLS failed; TLS validation was not disabled. Exact unknown operations/fields and a **not-sent** support question are recorded in [the contract follow-up](SHIPPING_LABELS_API_CONTRACT.md#direct-np-follow-up-contract-still-unverified-2026-09-27).
- The existing `JsonMarketplaceSettingsStore`, `DpapiMarketplaceSecretStore` and `DpapiShippingSettingsStore` were read for the **Development** profile (`%LOCALAPPDATA%/CheckboxBatchPrinter`). Only configured/not-configured flags/counts were emitted: two enabled Prom connections with saved tokens; zero configured Seller connections; no ShippingLabels/settings.dpapi; no Rozetka Delivery token. This is a store-level check, not an assumption based on Git. No secret values were printed or committed, and no production credentials were copied.
- No NP key field exists in the current version; therefore no direct NP credential could be validated. Adding its storage, per-store selection and read-only adapter awaits the official contract. The existing receipt/label history was neither migrated nor overwritten.

### Acceptance categories

| Category | This follow-up |
|---|---|
| Implemented in code | Existing Seller/RD adapters and PDFium packet printing unchanged. Direct NP adapter/key/store routing still pending. |
| Synthetic tests | Existing regression suite rerun below; **no new direct-NP contract test** can be legitimate without its verified contract. |
| Genuine label obtained | **None.** No current Prom TTN was submitted to an unrelated account; no live label API requests were made. NP account visibility, PDF access/identity/all places/actual dimensions remain unchecked. RD access/actual document remains unchecked without its locally entered token. |
| Physically printed | **None.** No StartDoc, printer-setting change, spooler restart, or physical print for this follow-up. |

Next required input: current official NP read/marking documentation or support response, without secrets. After the adapter is implemented, enter NP/RD credentials only locally in the development app and choose the intended NP account explicitly for each store. Real acceptance must use an existing **Prom-origin** TTN, inspect the official NP and RD PDFs and mixed preview, then request separate user approval before physical printing. Neither a tracking success nor a valid key nor a synthetic PDF completes acceptance.

### Follow-up verification / build

- Release build: **0 warnings / 0 errors**; existing executable regression suite **215/215 passed**, including Windows/STA raster/preview/backend scenarios. No current tests were removed, and no real APIs were called by unit tests.
- Separate **self-contained win-x64 single-file** PDFium build published to `artifacts/development/np-contract-review/CheckboxBatchPrinter.exe`. The copied EXE alone passed the guarded offline initial-launch and restart checks with its bundled .NET 8.0.31 runtime. The synthetic PDF packet preview was rendered and visually inspected. As source code is unchanged, the EXE intentionally retains implementation identity `7966c8665a9b8667a7c1d366d16b0deabeca6377`, channel Development, version 1.0.2; this follow-up commit contains documentation only.
- This build has the same label capabilities as `7966c86`, **not** newly implemented direct NP support. Production deployment is not part of this task.
- Production EXE SHA256 before/after remained `AB087874FB49E48611A2DF0157E6C0285DB9602DCEEEE09FC200B3F53E7B1992`. No production settings/history/credentials were opened for migration or written; production EXE was not replaced.

Reproduce the unchanged implementation's follow-up package in an empty output folder (the existing folder is not overwritten):

```powershell
dotnet build CheckboxBatchPrinter.sln -c Release
dotnet run --project tests/CheckboxBatchPrinter.Tests/CheckboxBatchPrinter.Tests.csproj -c Release --no-build
dotnet publish src/CheckboxBatchPrinter/CheckboxBatchPrinter.csproj -c Release -r win-x64 --self-contained true -o artifacts/development/np-contract-review -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:AppChannel=Development -p:SourceRevisionId=7966c8665a9b8667a7c1d366d16b0deabeca6377
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ProductionPackage.ps1 -Executable artifacts/development/np-contract-review/CheckboxBatchPrinter.exe -ExpectedCommit 7966c8665a9b8667a7c1d366d16b0deabeca6377 -ExpectedChannel Development
```

---

2026-09-27, development branch codex/checkbox-marketplace-orders. Started at clean HEAD 07882a738da0471fb6f12be3ba87b26d1330577b; no newer/user work reverted. Production EXE/profile are not deployment targets.

## Implemented

Compact carrier/TTN/preview/status columns and independent label checkboxes; select visible, clear all including hidden, cancel, separate settings, preview and print. Labels require neither Checkbox login nor receipt matching. Existing fiscal headers/printing and independent All Receipts remain unchanged.

Official NP Zebra through documented Seller TTN methods; separate RD token/details/base64 PDF. Whole-packet preparation and identity checks, stable mixed order, all places/partner pages, duplicate page prevention. Unavailable/corrupt/incompatible labels block the whole packet. Filters cannot change a frozen packet; replacement TTN requires fresh confirmation. Preview includes printer/format/DPI/scale/copies/counts/order/TTN and explicit repeat-risk acknowledgement.

Real driver formats and GDI hard-margin/DPI checks; one multipage job, not browser-per-label or continuous receipt layout. Separate DPAPI journal, unique job names, pre-transfer Unknown, known ID Submitted, no automatic uncertain retry. PDF remains in memory. Details: [verified contracts and limitations](SHIPPING_LABELS_API_CONTRACT.md).

## Tests

All 198 existing tests are retained; 17 new shipping/STA scenario groups exercise official API shapes, carrier/Ref/TTN/token identity, adapters, missing/denied/wrong/HTML documents, allowlist/redirects, NP/RD/NP order, multiple places, duplicate connections, legacy driver defaults and separate DPAPI persistence.

The actual WindowsLabelPrinter algorithm is exercised with an injected page device: one job / N pages; registration followed by exception persists Unknown for the whole packet; preparation error/cancellation never begins a job. Actual workspace/ViewModel scenarios cover no Checkbox credentials, hidden selection, filter changes during preparation/confirmation, cancelled confirmation, inaccessible labels, changed TTN and repeat warning after restart.

Native Windows/STA PDF tests cover 100/101.5 mm, 203/300 DPI, four rotations, non-square rotation, all four edge strips/barcode-like ink, corrupt PDFs, missing places, extra partner pages, A4 rejection on thermal paper and hard margins. A synthetic raster is visually inspected using the PDF skill render/verify workflow. Synthetic fixtures are never used as official-label fallbacks.

No fiscal operation, shipment/order change, registry creation or physical print is performed by tests.

## Remaining real acceptance / limitations

No real label read has been completed: the development profile lacks configured Seller/RD shipping access. Enter credentials only locally, never in chat or Git. Direct NP integration remains blocked by official portal 403 and unavailable old PDF; current official contract was requested. Seller access to Prom-created TTNs is not guaranteed or verified. Unknown secondary carrier identifiers remain fail-closed, not silently dropped.

RD actual paper size, partner pages and physical-seat mapping require real read-only acceptance. Xprinter XP-420B LAN was inspected read-only: empty advertised format list, current sized ticket 101.5×101.5 mm / 203 DPI. CreateDC/GetDeviceCaps succeeded without StartDoc: virtual GDI frame 105.6×101.6 mm, printable area 101.6×101.6 mm, left/top 2/0 mm. The actual sized default is supported; symmetric external frame padding is distinguished from substituted media, and A4/unexplained frame substitutions remain rejected. No global printer settings were changed.

Xprinter feed, order, copies, no extra blanks, full content and barcode scan/readability are **not physically tested/confirmed**. Windows job ID does not prove paper output.

Acceptance: preview existing accessible NP, RD and a mixed packet; verify identities/pages/paper; after explicit approval print one label then a small mixed packet. User confirms quantity/order/edges/no extra blank labels/barcode readability. Production stays unchanged.

## Reproducible development build

SDK is pinned in global.json; target net8.0-windows. PDFiumCore / native PDFium 155.0.8057 are pinned NuGet dependencies and full licenses are embedded. The Windows.Data.Pdf prototype was rejected after reproducible zombie-process shutdown failures; the final PDFium runner and copied EXE exit normally, without forced-exit workarounds. Git contains source/resources/tests, no private documents or generated binaries. Existing build.ps1 refuses nonempty output folders:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory artifacts\development\shipping-labels
```

Separate output: artifacts/development/shipping-labels/CheckboxBatchPrinter.exe. Nothing is copied to D:\Portable Soft\CheckboxBatchPrinter.exe. Existing channel data roots and receipt history are unchanged.

### Final results

- Release build: **0 warnings / 0 errors**.
- Full executable suite: **215/215 passed**, including all previous 198.
- Development framework-dependent single-file: only copied EXE, separate synthetic profile, initial launch + restart passed; real bundled PDFium raster contents, two pages, inside-edge barcode-like ink, embedded licenses and compiled XAML/preview checked.
- Separate self-contained win-x64 development single-file: published under artifacts/development/shipping-labels; copied EXE initial launch + restart passed with its bundled runtime. The final build is stamped with the development commit, not installed over production.
- Synthetic raw raster and packet preview visually inspected: four edges, quiet space, stripe content and labels' order visible; header metadata stays outside the paper.
- Read-only Xprinter driver smoke passed; **no physical job sent**. Real NP/RD document access remains unverified for the reasons above.
- Production EXE SHA256 checked: AB087874FB49E48611A2DF0157E6C0285DB9602DCEEEE09FC200B3F53E7B1992. No production deployment, settings/history migration or production-profile write was performed.
