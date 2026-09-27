# Shipping labels — implementation and acceptance

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
