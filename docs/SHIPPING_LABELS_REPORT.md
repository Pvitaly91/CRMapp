# Shipping labels — implementation and acceptance

## Direct NP / Prom follow-up — NOT COMPLETE

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
