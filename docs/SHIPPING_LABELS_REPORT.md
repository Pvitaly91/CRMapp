# Shipping labels — implementation and acceptance

## 1.0.14 — immediate NP requests and one lookup per packet/account (2026-10-01)

The user requested removal of the fixed two-second delay and repeated label preparation checks. Starting from `82464da6a338099e884ebc524abd3ece6836e2e9`, the transport now sends the next request as soon as the preceding response completes. Per-key serialization and pauses after actual HTTP 429 / supported HTTP 200 rate-limit responses remain, including server Retry-After and the existing fallback. Cancellation and other carriers' retry behavior are preserved.

Each explicit label preparation owns a fresh lookup snapshot. The first unverified TTN loads its account's recent seven-day list once; subsequent TTNs from that account reuse the completed list. Snapshots are separated by account ID, key fingerprint and period, contain only shipment metadata, and are discarded after preparation. Every selected TTN still resolves its own deletion/Ref/places metadata and receives its own current PDF. Receipt printing, history, credentials and printer settings are unchanged.

For the synthetic two-label scenario with one nonempty page and an empty terminal page, preparation now needs four HTTP requests (two list pages and two PDFs), instead of six. There are no fixed inter-request sleeps. No live API speed measurement or physical print is claimed; remaining elapsed time depends on the server, network and PDF processing.

Release Development verification: **308/308 tests passed**. All previous groups remain, with pacing-specific expectations updated to immediate requests and three new batch snapshot groups. Regression coverage includes one scan for two TTNs, fresh scans for later packets, missing/deleted/conflicting second records, key rotation/account rebinding, active/queued cancellation and preserved rate-limit deadlines. The final build has only the pre-existing WFAC010 warning. Production packaging uses the existing clean-commit workflow and records its checks separately in package metadata/build logs.

---

## 1.0.13 — verify every selected NP label during packet preparation (2026-10-01)

Started at clean `2b11038a222c9cb4f5e0084a2202f7d34487751b`. The two-label screenshot was caused by `NovaPoshtaDirectLabelSource.GetAsync` requiring individual manual session approval for every TTN. The settings diagnostic operates on the current order only, so approving one label still blocked a packet containing another existing label.

Reproduced before the production fix through the actual `ShippingLabelsViewModel` → configured/direct NP source → synthetic HTTP → PDFium/render pipeline. The first TTN was manually checked through the real diagnostics path; selecting both then reproduced the exact "Цю ТТН спочатку явно перевірте..." error. The regression groups failed **0/2** before the fix and passed **2/2** after it. Initial fixture compilation needed the existing PDF helper's namespace; this was a test setup repair before the observed product failure.

Explicit packet preparation now verifies a missing TTN proof using the existing read-only, recent seven-Kyiv-day lookup plus PDF identity/places/format checks. Prior approved evidence still receives a fresh PDF inspection and exact Ref guard. New evidence remains local to that operation, never a manual approval or saved PDF/cache. The existing complete-packet preview/confirmation gates one Windows job. A second-document failure cancels the entire packet and identifies its order/TTN; rate-limit deadlines are retained. Shop routing, shared pacing/cooldown, driver geometry, receipt printing and profile/history paths are unchanged. Unknown/multi-place/incomplete/wrong-identity/incompatible PDFs remain blocked.

Release Development verification: **305/305 full-suite tests passed**, preserving all previous 295 and adding eight direct-source plus two workspace/viewmodel regression groups. Tests cover one or neither TTN previously approved, whole-packet confirmation/cancellation, wrong/missing/incomplete/multi-place/incompatible second documents, cancelled lookup, retained 429 deadlines, key rotation, exact shop binding/Ref and changed PDF after prior approval. Isolated compilation has no errors; the full rebuild retains the pre-existing WFAC010 warning. Production packaging uses the unchanged clean-commit workflow; its exact results are recorded separately in the package metadata and local build log. This work uses synthetic keys/TTNs/PDFs and a fake printer backend; real two-document NP retrieval and paper output have not been tested.

---

## 1.0.12 — one NP key editor, read-only diagnostic (2026-10-01)

Started at clean `419e98aa32df20e3e9907faa5b138aeea75dc468`. The user's second screenshot showed a duplicate NP editor inside the diagnostic. Removed that entire control tree and its account/key/binding save paths: account selectors, account creation/name, second password field, binding selector, date pickers and save button. Removed the duplicate advanced entry point from the main label settings. The one primary masked NP-key field remains per explicitly selected Prom shop; existing account records, stable bindings, encrypted settings and history are not deleted or migrated.

The diagnostic now displays only the selected order's shop, its existing NP shipments, a read-only period, manual check/result/cancel/close. It resolves the enabled Prom shop by stable order connection ID, reloads that shop's latest saved key/binding before each explicit check and never saves a stale snapshot. Missing context/binding/key cannot fall back to another account. Main-form verification is disabled when its key shop differs from the selected order's shop, with a handler guard before any save. The former default recent seven-day TTN-creation window is now fixed, shown in Kyiv dates and refreshed per check; old TTNs outside that window cannot be checked here. No inference from order date, automatic history expansion or extra API operation was introduced.

Request pacing/cooldown, cancellation/reentry, strict lookup/PDF/places validation and explicit session approval remain unchanged. Release Development verification: **295/295 full-suite tests passed**, including all prior scenarios adapted to the removed editor and three new STA groups; **40/40 focused NP/settings tests passed**. Tests assert absence of the entire advanced control tree, no diagnostic save/open-time API calls, fresh key/binding loading, correct shop isolation, missing-context refusal and disabled/programmatically blocked mismatched verification. Main-form masked key save/rotation/restart tests and cooldown/cancellation tests remain. The first focused render attempt lacked its output folder; creating that test-only folder resolved the fixture failure before the clean focused/full runs. Synthetic diagnostic and cooldown views were rendered at 660×340 and visually checked: no duplicate editor or clipping. Build has the pre-existing WFAC010 warning and no errors. No real API calls, print jobs or production-profile writes were performed in these checks. Production packaging, if prepared, uses the unchanged clean-commit script and records exact results in its build log/package metadata; that alone does not install the EXE. Earlier sections describe historical releases, including their former editable advanced NP controls.

---

## 1.0.11 — NP pacing and rate-limit cooldown (2026-10-01)

Started at clean `e634b214a4f3dd676cfdf05f0dbd1b72de38ad15`. The follow-up screenshot showed a genuine NP rate-limit response, not the earlier deletion-flag parsing defect. Inspection found that successive lookup pages were sent without spacing; only a currently running window blocked duplicate clicks, and closing/reopening the dialog or retrying after failure did not retain a cooldown. The shared label transport also retried HTTP 429 internally. No additional real NP request was made while diagnosing this report.

- Direct NP JSON and PDF requests now share per-key coordination for the lifetime of the app's shipping transport. Account IDs with the same key share spacing/cooldown; another key has independent state. Key fingerprints are memory-only and never displayed, logged or written to the profile. No response/PDF cache or cross-account document reuse was added.
- A minimum **2-second local spacing** is applied after each direct NP request completes, including failed requests and transport retries. This is a conservative app policy, **not a documented NP quota or promise of acceptance**. Concurrent account scans are rejected instead of queuing duplicate full lookups; cancellation remains available while waiting.
- HTTP 429 and the supported HTTP 200 rate-error envelopes stop immediately, with no automatic NP retry. The cooldown retains the latest of the existing deadline, any server `Retry-After` (delta or date), and the **60-second local fallback**. Long server deadlines are not shortened to the old 45-second retry bound. A failed/limited empty page never means the lookup is complete and cannot lead to a PDF request.
- The verification button shows the remaining seconds and stays disabled during cooldown. Reopening its window uses the same shared state. Expiry only re-enables the button; it does not start a request. The timer stops when the window closes. Server limits can also depend on other software/account activity and may last longer; no claim that local expiry clears a server limit.
- RD/Seller transport behavior, NP endpoints and request contracts, document identity/places checks, manual session approval, physical print geometry, receipt/history data and profile paths are unchanged. Cooldown is process-local, not a persisted NP limit or an authorization to retry through another account.

Verification: **292/292 Release Development tests passed** (all previous 284 plus six request-policy groups and two Windows/STA UI groups). Controlled clocks cover post-completion JSON/PDF spacing, shared-key coordination, independent keys, delta/date/missing/overflow Retry-After, queued and cancelled operations, scan reentry and unchanged RD/Seller retry behavior. Actual STA windows cover HTTP 200/429 cooldown, repeated clicks, close/reopen, timer expiry without a request and cancellation while pacing; both UI groups passed and the synthetic countdown screenshot was visually inspected. The initial new overflow fixture used a delta rejected by .NET before any product code; it was corrected to a valid maximum delta with a near-maximum clock, and the final suite passed. Full compilation retains only the existing WFAC010 DPI-manifest warning, with no errors.

Production packaging uses the unchanged clean-commit `scripts/Build-Production.ps1` workflow: both channel suites, immutable Production-FDD output and copied-EXE launch/restart/isolation checks with synthetic offline profiles. The authorized deployment follows the existing private profile/EXE backup, atomic EXE replacement and all-files-unchanged verification workflow; the package metadata, build log and local deployment record identify the actual outcome. No real NP recheck was made after this change, so neither live server-limit expiry nor a successful real PDF retrieval is claimed. No fiscal/shipment mutations or physical printing are part of this work.

---

## 1.0.10 — live NP response compatibility (2026-10-01)

Started at clean `35255488faf5fdb89d57cfe16325f3a8f4050ef3`, branch `codex/checkbox-marketplace-orders`. A read-only diagnostic of the existing TTN selected by the user confirmed that the locally saved, explicitly bound NP key works for that document. Neither the real number, account IDs, key nor customer data is included here or in test fixtures.

- Root cause of the screenshot error: `getDocumentList` returned `DeletionMark: "0"`, but the parser accepted only JSON booleans. The client now explicitly supports `"0"`/`false` (active) and `"1"`/`true` (deleted). Unknown representations still stop before any PDF request; a missing optional field retains previous compatibility. Error messages are plain Ukrainian and never echo response values.
- The full real lookup completed after three pages (100, 48 and 0 rows), found the exact requested document and reported one place. The existing read-only marking route returned a one-page PDF whose text matched that TTN. Its physical size was **101 × 101.5 mm**, exposing a second gate that only accepted nominal 100 × 100 mm. This observed format is supported explicitly with 0.1 mm rounding tolerance, in addition to the existing 100 × 100 tolerance. No general tolerance increase, cropping, PDF editing or printer-setting change. The renderer retains actual dimensions; the existing printer imageable-area/fit checks still decide whether it can be printed on the selected stock.
- No change to account routing, endpoints, pagination completion, identity/places verification, per-session approval, matching, receipt printing or history paths. Other accounts/TTNs and multi-place documents were not tried. A valid one-place sample does not establish universal account access or multi-place completeness.
- A later live recheck reached the server's rate limit (HTTP 200 / failed JSON envelope with a “many requests” error), so further live requests were stopped. Exact `Too many requests` / `To many requests` errors are now reported as a temporary rate limit, not wrong credentials. No automatic retry, ignored failure, shortened pagination or invented Retry-After was added. An unknown/mixed error still cannot be interpreted as successful termination.

Regression before the flag fix: **280/282 passed**; the new active string-0 case reproduced the screenshot error, and the string-1 case failed correct deletion classification. The unknown-value guard passed. After the flag fix: **282/282 passed**. The format regression then failed with **282/283**, and passed with **283/283** after the narrow size fix. Final Release Development suite: **284/284 passed** (all original 279 plus five new groups); rate-limit tests also verify no PDF/automatic retry after a failed third page, no false key advice and rejection of mixed errors. Tests use synthetic TTNs/keys/PDFs and the actual client → diagnostics → PDFium pipeline, not hand-built successful results. The geometry regression checks preserved millimetres/raster dimensions, both supported shapes, neighboring unsupported sizes/A4, and rejection on undersized stock or hard margins. Build has no errors; full compilation retains the pre-existing WFAC010 DPI-manifest warning.

Production packaging uses the unchanged `scripts/Build-Production.ps1` on a clean release commit: both channel test suites, immutable Production-FDD output, copied single-EXE launch/restart with synthetic offline profiles and cross-channel isolation. Exact packaging/deployment results are in the local build log, package metadata/checksums and deployment record; real user profiles are not used for smoke tests.

Only existing documents were read from NP. Raw responses/PDFs/extracted text stayed in memory, with no files or production-profile writes; the local diagnostic prints only fixed schema facts and verification results. No fiscal operations, shipment changes, session approval or physical printing occurred. The user explicitly requested installation with the app closed. Deployment must back up the existing profile/EXE and verify profile files unchanged; preparing this source is not itself proof of installation.

---

## 1.0.9 — simplified label / NP settings (2026-09-30)

Started at clean `e6a9234346cc62bcb3679a2f1d45026059671d74`, branch `codex/checkbox-marketplace-orders`. Only settings UX, its key-application helper, tests, version and explanations changed; no API/backend/matching contracts changed.

- Primary label settings: printer, actual driver format, copies, and **one masked NP API-key field for the selected enabled Prom shop**. A single shop needs no account/binding selector. Multiple shops require an explicit selection (or the selected order's shop); typed drafts never migrate between shops. Saving creates the explicit account/binding internally, not an HTTP connection test or permission to print a PDF.
- Scale, offsets, orientation, RD token, Seller routing, account management and technical notes remain available under a collapsed **Розширені налаштування**. Existing values are not reset. Stored secrets are never populated into password boxes. Trim-empty input preserves the previous key/token. Key-only save reads latest settings, preserving newer printer, Seller and RD settings; it also works without a label printer.
- Existing private NP account rotates in place; changing a shared account for one shop creates a separate account/binding for that shop only. Unbound legacy accounts are not guessed, reused or deleted. Account limits still apply. Existing settings schema/DPAPI protection and stable history paths are unchanged; no receipt/label history migration is needed.
- The NP diagnostic window primarily shows the selected shop/TTN, a clearly displayed bounded period and a manual check button. Account/name/key/binding/date controls are collapsed. Its default TTN-creation window is the last **7 days**, not a new automatic scan; the user can change it explicitly within the existing 1–7-day bound. Saving settings makes no API request. The concrete PDF identity/completeness checks, session approval, rotation invalidation and all-or-nothing packet guard are unchanged.

Verification: **279/279 Release Development tests passed** (all previous 267 plus 12 new groups). New tests exercise pure key transitions, private/shared/unbound store identities, invalid keys/overflow, encrypted restart with unchanged history, actual Windows/STA simplified layout, shop-switch drafts, cancellation, latest-settings preservation and no-printer save. Existing manual NP request/reentry/cancellation and PDF/backend tests remain. Synthetic settings views were rasterized and visually inspected. Build has the existing WFAC010 DPI-manifest warning, no errors. Normal save tests use unshown windows; end-user modal interaction with real credentials remains to be accepted locally.

Published a self-contained win-x64 single-file Development preview into `artifacts/development/1.0.9-simple-np-settings`. Copying only that EXE into a fresh directory and launching/restarting it with the guarded synthetic offline profile passed (`Test-ProductionPackage.ps1`, .NET 8.0.31). Its revision metadata carries the `-dirty-simple-np-settings` suffix because that preview was built before the release commit. It is not the Production release payload.

No real NP/RD/Checkbox requests, fiscal operations or physical print were performed. During development the installed Production 1.0.8 EXE/profile were not replaced or migrated (EXE SHA256 `2A5B2EB6BEEBDF2862FB4E7A0CF3903351A0334FC23676C48FAE8926CF8EAD72`). The user's subsequent explicit production-update request uses the standard clean-commit `scripts/Build-Production.ps1` workflow: both channel test suites, copied-EXE restart/isolation checks and separate immutable Production-FDD output. Deployment creates a private profile backup and old-EXE backup, atomically replaces only the EXE with the app closed, then verifies every existing profile file unchanged. Package `build-info.json`, `SHA256SUMS.txt` and the local deployment record identify the actual release/result; this paragraph alone does not confirm installation. Real NP account access, genuine PDFs and paper output remain unverified. The following sections are historical.

---

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
