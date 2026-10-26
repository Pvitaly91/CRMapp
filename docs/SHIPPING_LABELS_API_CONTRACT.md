# Shipping-label contracts and SDK diagnostic candidates

Checked 2026-09-27; direct NP live-response follow-up 2026-10-01. Existing documents only; no shipment, registry, order or receipt mutations. POST /sites is authentication.

## Current direct NP implementation: SDK-derived, one live document verified

The user explicitly authorized research of a pinned **unofficial** SDK after documentation commit `2bb0740`. The following code was read from revision `1c7027da068363d9d06792a1e698b025d852b41d`; PHP/SDK was **not installed**:

- [Marking.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/Marking.php): `printZebraMarking` takes an array of public numbers, using `orders[]` for one number and `orders` plus comma-separated numbers for multiple. No scan-sheet/document creation routes were implemented.
- [InternetDocument.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/InternetDocument/InternetDocument.php) and [GetDocumentListRequest.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/InternetDocument/RequestData/GetDocumentListRequest.php): model/method, StudlyCase properties, `d.m.Y` dates and string page.
- [BaseModel.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/BaseModel.php), [Request.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/Request.php), [DocumentListData.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/InternetDocument/ResponseData/DocumentListData.php): JSON envelope and IntDocNumber / Ref / SeatsAmount / DeletionMark. Unneeded buyer/address fields are discarded, not copied to diagnostics/history.

**Evidence levels:** the key/HTTPS JSON entry point are confirmed by the official integration overview; the specific methods, parameters and marking route were **found in SDK source** and are **not confirmed by current official documentation**. On 2026-10-01 the user's selected existing TTN was found through a completed three-page lookup using its explicitly bound local account, and the route returned a one-place/one-page PDF with matching text identity and actual 101 × 101.5 mm size. This is live evidence for one document, not all accounts, formats or multi-place shipments. No physical print was performed. The historical documentation-access observations below remain separate from this acceptance.

### Restricted requests

| Request | Implemented candidate / guard |
|---|---|
| POST `https://api.novaposhta.ua/v2.0/json/` | Exact root properties `apiKey`, `modelName="InternetDocument"`, `calledMethod="getDocumentList"`, `methodProperties`. Only `DateTimeFrom`, `DateTimeTo` (`dd.MM.yyyy`), `GetFullList=false`, `Page` (string 1–10); backend accepts only 1–7 days. Since 1.0.12 the simplified UI supplies the displayed last seven Kyiv dates without date editors. Duplicate/extra keys, guessed TTN/Ref search parameters, all save/update/delete/registry methods and other HTTP methods are rejected before sending. Request body must be buffered JSON ≤4096 bytes. |
| GET `https://my.novaposhta.ua/orders/printMarking100x100/orders[]/{PUBLIC_TTN}/type/pdf/zebra/zebra/apiKey/{KEY}` | SDK candidate for one 14-digit public TTN (`[]` URL-encoded). Diagnostics uses it after lookup; explicit printing requests the PDF directly. Ref and order ID are never substituted in this route. Exact HTTPS host/path and key-character bounds, no query/fragment/userinfo/nondefault port. No redirects, including to another NP host. |
| GET `https://my.novaposhta.ua/orders/printMarking100x100/orders/{TTN1},{TTN2}/type/pdf/zebra/zebra/apiKey/{KEY}` | Since 1.0.15, one PDF request for distinct selected public TTNs bound to one NP account. Literal commas, only 14 ASCII digits per number; app bound 100 distinct TTNs, not a claimed carrier quota. No list lookup or per-number fallback. |

These operations do not create/edit shipments, registries, addresses, afterpayment, orders or fiscal receipts. There is no generic API console or account enumeration. The transport's 20 MiB response limit, 45-second per-request timeout, cancellation and whole-operation five-minute diagnostic timeout remain in use. Since 1.0.15 direct NP requests do not automatically retry 5xx either; rate limits, errors and network failures stop the request without trying alternate routes. HTTP 429 retains the shared per-key cooldown. RD/Seller bounded retries are unchanged. API/HTTP/JSON error bodies and credential-bearing URLs never appear in messages/logs; exceptions do not retain secret-bearing inner network exceptions.

### Lookup and PDF acceptance

The user selects one existing NP TTN from the currently selected Prom order and explicitly starts the check. Its NP connection is bound to that enabled Prom store, independent of Seller. Since 1.0.12 the duplicate account/key/binding/date editor is removed, not hidden. The sole key editor remains in the main label settings; checking reloads that shop's latest persisted binding/key and never writes settings. The date interval is the displayed last seven Kyiv days, refreshed on each manual check; it is **not inferred from order creation time**. No lookup runs on startup, opening settings or saving a key. Opening diagnostics from the main form requires the key shop to match the selected order's shop.

The response must contain boolean success, array data and array errors. Validate exact string IntDocNumber, separate UUID Ref when supplied, optional positive SeatsAmount and DeletionMark. No missing values are invented. A found public number without Ref can be inspected because the SDK marking route uses NUMBER, not Ref; internal document identity is explicitly `ttn:{PUBLIC_NUMBER}`, never presented as a UUID Ref. A supplied shipment Ref must agree.

The observed `DeletionMark` representation is string `"0"`, not a JSON boolean. Version 1.0.10 accepts only exact `"0"`/`false` as active and `"1"`/`true` as deleted. An absent optional flag remains allowed as before; an explicit unknown/null/object/numeric value is not silently coerced to active. Deleted/invalid selected records cannot proceed to marking. The real response also uses a string positive `SeatsAmount`, already supported by the original parser.

The SDK does not establish page size/total/last-page semantics. A short page is **not** terminal: continue until an empty successful page. Repeated pages or page-limit exhaustion mean **incomplete**, even if the number was encountered; no marking request follows. A complete empty match means **“Не знайдено у перевіреній вибірці цього акаунта”**, not nonexistent TTN or invalid key. The simplified diagnostic does not expand the recent period or check older TTNs; other accounts/periods are never automatically scanned.

Failed HTTP 200 envelopes whose errors are exclusively the exact messages `Too many requests` / `To many requests` stop as `Temporary` with a fixed rate-limit explanation. They do not establish invalid credentials, completed pagination or PDF eligibility, and they do not trigger automatic retries. Unknown/mixed errors still fail closed; raw errors are never displayed. A later live recheck encountered throttling and was stopped; no rate-limit duration/code was inferred.

Version 1.0.14 removes the fixed two-second request gap introduced in 1.0.11. JSON/PDF requests proceed immediately after the preceding response completes. One process-local policy still serializes requests by memory-only API-key fingerprint. HTTP 429 and supported HTTP 200 limit envelopes record the maximum of existing cooldown, server `Retry-After` and local 60-second fallback; only an actual limit response activates that pause. The UI shows a countdown and prevents immediate reentry; reopening the dialog retains state, expiry does not auto-retry, and cancellation does not erase it. No server quota is inferred and no request/response contract or alternate-account fallback changes.

The marking response requires successful HTTP, application/pdf, PDF header and native PDFium parse; HTTP 200 alone does not establish identity. The PDFium inspector reads each page's physical dimensions and operation-local text. It excludes text outside page bounds and compares maximal numeric runs (not 14-digit prefixes of longer numbers); conflicting numbers fail closed. Missing text/unknown formatting remains unconfirmed and goes only to **diagnostic preview**. Raw extracted buyer text/PDF/PNG is never written to disk or a profile.

Multi-page diagnostics retain all pages and display API place count, PDF count and each actual page size. **Multi-place completeness is not established by count alone**; seat-number correspondence has not yet been verified on a real NP PDF. The diagnostic's session-approval gate still only approves its established one-place/one-page case. The print path below no longer depends on diagnostic approval and does not claim verified completeness for unknown counts.

### One-request packet source (1.0.15)

`NovaPoshtaDirectLabelSource` implements IShippingLabelBatchSource, an optional IShippingLabelSource factory receiving all selected shipment references for one preparation. The first direct-NP document requests one PDF containing the distinct TTNs bound to that account. It performs **zero getDocumentList calls**. Subsequent documents reuse the inspected PDF for that preparation, keyed by account ID, key fingerprint and the selected TTN set. New packets or account/key changes cannot reuse the previous response. No response is written to disk or saved as manual approval; no request starts on construction, startup or settings save. Account enumeration, alternate-account fallback and automatic per-TTN retries are prohibited. Final user permission to print remains the complete packet confirmation.

Pages are mapped by their printed TTN, **never by response order**: each page must identify exactly one requested TTN and every requested TTN must appear. All returned pages for a TTN are retained in relative response order, including multi-place labels. If the order supplies an expected place count, fewer pages fail; unknown counts do not become an invented one-place requirement. This is a deliberate simplification from lookup/Ref/deletion/place proof: it relies on the authenticated current PDF, preserves all returned pages and requires preview, but does not claim that unknown seat completeness or current deletion state was independently verified. Typed document IDs are `ttn:{number}`, not fabricated UUID Refs. Original PDF indices select rendering pages; TTN-local page ordinals preserve history/dedup semantics if bulk response order changes.

Supported actual sizes are the original 100×100 mm (0.8 mm tolerance) and the observed 101×101.5 mm (0.1 mm rounding tolerance); A4/other formats remain blocked. Actual page dimensions are preserved through rendering and the independent printer fit check; eligibility never implies paper fits. Missing, unidentified, conflicting or incompatible pages stop the whole packet. A preparation error identifies its selected order/TTN and preserves typed failure/cooldown data.

The SDK establishes the URL shape, not a guaranteed page order, maximum carrier batch size, partial-failure response or latency. The official [business-cabinet guide](https://my.novaposhta.ua/data/how-to-use.pdf), printed page 68, states that label count equals shipment-place count; therefore one TTN is not assumed to mean one PDF page. The application's 100-number/page bounds are resource limits, not official NP quotas. Bulk retrieval and multi-place behavior are verified synthetically only. No two-second server response guarantee is made.

`ConfiguredShippingLabelSource` routes explicitly bound Prom/NP to that direct source. Failure never falls through to other accounts/Seller. Existing explicitly selected Seller alternative and Rozetka Delivery remain available. Unbound Prom/NP without explicitly selected Seller asks for an NP store binding, not mandatory Seller.

Existing LabelBatchPreparation, rasterizer, preview, WindowsLabelPrinter and separate attempt journal remain in use: stable NP→RD→NP pages, complete preparation before confirmation, no partial transmission after a document failure. Since 1.0.91 each physical label/copy is a separate Windows job. Printer scaling, receipt geometry and fiscal headers are unchanged.

Connections (stable ID/name/key) and per-Prom-store bindings are in the existing channel-specific ShippingLabels/settings.dpapi. Blank key field preserves its old value; rotating a key retains connection ID/binding and does not alter label/receipt history. The main key settings can be saved without a printer, Checkbox or Seller; diagnostics/preparation never save them. Keys/fingerprints are not included in reports. Real direct NP acceptance status: **one selected existing TTN and its one-page PDF verified in 1.0.10; the 1.0.15 one-request bulk path, physical print and multi-place completeness are not verified with real documents**.

## Primary sources

- [Prom Swagger](https://public-api.docs.prom.ua/), schemas under `documentation/Orders/schemas/`: Order, DeliveryProvider, DeliveryOption.
- [Rozetka Seller API](https://api-seller.rozetka.com.ua/apidoc/), machine-readable `apidoc/api_data.js`.
- [Rozetka Delivery Swagger](https://rz-delivery.rozetka.ua/api/docs/), machine-readable `swagger-ui-init.js`.
- [Nova Poshta developer portal](https://developers.novaposhta.ua/) returned 403; an older official API PDF returned 404. No direct NP endpoint was inferred from third-party examples.
- [PDFiumCore source](https://github.com/Dtronix/PDFiumCore), pinned 155.0.8057; [PDFium binary build 8057](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8057), primary PDFium headers included with NuGet.

## Marketplace identities

Prom: preserve `delivery_provider_data.provider`, `declaration_number` (public TTN string), sender/recipient warehouse IDs, delivery_option.id/shipping_service and source path. No documented Ref or label endpoint was found in this schema. Exact known service names are a fallback; custom/unknown names stay Unknown. Carrier never follows marketplace/payment/matching.

Rozetka: preserve delivery.delivery_service_name/id, ttn and carrier.carrier_track_num/carrier_inner_id. Numeric unknown secondary carrier IDs are not guessed: their TTNs stay visible but currently block unsupported packets. Old cached OrderShipment records remain compatible. TTNs are strings, not marketplace order IDs.

## Nova Poshta: verified Seller alternative

| Operation | Contract |
|---|---|
| POST https://api-seller.rozetka.com.ua/sites | username, base64 UTF-8 password; success=true, content.access_token. |
| GET /ttns/ttn-list?ttn=PUBLIC_NUMBER | Bearer token; content.ttn_list; require exactly one matching IntDocNumber and valid UUID Ref; preserve SeatsAmount and check any supplied Ref. |
| GET /ttns/ttn-print/zebra?ttnNumbers%5B%5D=PUBLIC_NUMBER | Official Zebra PDF, documented 100×100 mm; one TTN per request. |

Ref and public TTN are distinct. TTN-module rights and actual document access are required. Prom origin does not prove that a Seller account can access its NP document. Seller is selected separately; existing Rozetka-origin Seller connection can be used. No carrier registry is created.

The direct NP SDK candidate is separate from this verified Seller alternative. Its one-document acceptance above is not advertised as access to all Prom shipments or other NP accounts.

### Historical documentation-only follow-up at 2bb0740 (2026-09-27)

Rechecked from the local Windows PC, not just the remote web reader:

- HTTPS GET `https://developers.novaposhta.ua/` through PowerShell returned HTTP 403.
- Ordinary navigation to that same URL in the local Codex browser displayed the Cloudflare security-check page; the documentation did not become visible. No challenge was solved, cookies inspected, TLS disabled, or protection bypassed.
- The official integration page loaded successfully. It documents an API key, the HTTPS JSON entry point `https://api.novaposhta.ua/v2.0/json/`, and links to the developer portal. It does **not** specify the required read/label methods or response shapes: [official integration overview](https://novaposhta.ua/for-business/cooperation/integration/).
- The older official `devcenter.novaposhta.ua` address mentioned in the API licence could not establish TLS from this PC. Certificate verification remained enabled. It provided no contract evidence.

This proves a documentation-access problem in this environment, **not** that direct NP label printing is impossible. No authenticated NP request was made. The following remain unverified:

| Required contract | Missing evidence |
|---|---|
| Key authentication and connection check | Exact documented read-only operation, parameters, success/error response. |
| Existing Prom-origin TTN lookup | Model/method, exact-number lookup, rights/account scope, distinction between public TTN and UUID document Ref. |
| Official thermal marking | Documented HTTPS route/model/method, PDF response, 100×100 selection, allowed parameters, handling of a key in a URL. |
| Multi-place shipment | Place-count field and whether/how all pages are returned; page/document identity, actual page dimensions. |

At `2bb0740`, the generic JSON entry point was **not** in the transport allowlist. The later user-authorized SDK implementation above adds only its specific bounded read method, not arbitrary POST access. Synthetic fixtures still do not satisfy real NP acceptance. No placeholder connection test reports success and no unchecked redirect can receive a key.

At `2bb0740`, dispatch/credentials/per-store selection/direct adapter were pending; they are now implemented conditionally as described above. Seller still cannot prove access to a Prom-origin TTN owned by another NP context. A Seller miss must never be treated as proof that the TTN does not exist.

### Draft question for NP technical support (not sent)

> Потрібна read-only інтеграція: маємо номер вже створеної через Prom.ua ТТН і API-ключ акаунта Нової пошти. Підкажіть актуальні документовані modelName/calledMethod та параметри для перевірки доступу до саме цієї ТТН й отримання її document Ref (якщо потрібний), а також точний HTTPS-запит офіційного PDF-маркування 100×100 для **всіх місць**. Просимо приклади запиту/відповіді без секретів, поле кількості місць, спосіб перевірки номера документа, правила авторизації/redirect та пояснення, чи доступна форма для ТТН, створеної Prom під іншим обліковим контекстом, і яке підключення потрібне. Нічого створювати/змінювати не потрібно; tracking або перевірка валідності ключа не замінює доступу до PDF. Де доступна актуальна офіційна документація, якщо developers.novaposhta.ua повертає 403/сторінку перевірки безпеки?

Send this only through the user's chosen official support channel. Do not include an API key, buyer information, private PDF or a credential-bearing URL. This request has **not** been sent by the application or agent.

## Rozetka Delivery

### Rozetka orders through Seller (1.0.42)

For a Rozetka order, use its exact enabled Seller connection and locally stored login/password. Never substitute the NP Seller override or try other shops. Seller auth must grant `module_octopus`. The [official list](https://api-seller.rozetka.com.ua/apidoc/#api-Octopus-OctopusTtnList) `GET /delivery-rozetka/ttn-list?order_id=ORDER&page=N&pageSize=100` returns `content.models` and `_meta`; a primary-RMP request also filters `ttn`. Verify complete stable pagination, an unambiguous `ttn` and exact `order_id`; only `carrier_track_num` proves a partner alias. Numeric Seller `id` is distinct from `ext_id`, which is not assumed to equal the tracking number. No deleted/archived/place count is documented for this list.

The [official PDF method](https://api-seller.rozetka.com.ua/apidoc/#api-Octopus-OctopusPrintTtnBatch) is `POST /delivery-rozetka/ttn-print-batch`, exact JSON `{"track_numbers":["RMP-…"]}`. This POST reads the print form of an existing document; create/edit endpoints are excluded. One verified primary TTN per PDF preserves all pages without guessing their order. A live response uses `application/octet-stream`; this MIME is accepted only for this exact route when PDF was expected, with PDF-header and native-parser validation. Other routes retain strict MIME requirements. No format, size, orientation or partner-number print parameter is invented.

Known primary and partner references from the same selected order share an operation-local PDF. The verified covered references propagate into protected print history while the physical pages are submitted once. Page deduplication is scoped to service, connection and canonical document. Raw order/cache identities remain intact; unknown partner carrier IDs are not assigned an invented carrier. Each partner number must be verified in Seller's matching order record before entering this path. Cache is limited to the current order/account/credentials and bounded to 80 MiB.

Seller history adds an optional exact order/account key to each attempt-page identity; existing records without it retain their previous behavior. Candidate state and duplicate warnings normalize only credential-proven connection aliases, then match that key. Different Seller accounts/orders with the same partner TTN cannot inherit newly recorded print state. Independent carrier documents still match their known carrier/TTN identity.

### Direct Delivery account / other marketplace orders

| Operation | Contract |
|---|---|
| GET https://rz-delivery.rozetka.ua/api/track/{id} | ID documented as EN number; statusCode=0, data.id/places/carrier_track_num. Check returned ID and selected public/partner TTN. Never substitute order ID. |
| GET /api/track/label?id=VERIFIED_ID | Swagger id query is an array of strings, form/explode. One-ID requests; statusCode=0, data.label base64 PDF. |

Separate locally entered Bearer RD token; never reuse Seller credentials on this host. This existing route remains for Prom shipments and independent Delivery documents. Swagger mentions static tokens; its separate login/activity-token lifecycle is not reimplemented. No documented size/format parameter or multi-ID page map was found. Fetch one verified EN at a time, preserve all pages, and inspect actual PDF sizes. Do not invent format=100x100 or crop A4. Generic Seller fragile/top stickers are not parcel labels and are not used.

Documented places are a minimum, not an exact PDF page count: partner legs can add pages. The UI shows PDF page/part indices, not invented physical-seat IDs. Real partner metadata/page-to-seat correspondence remains unverified.

## Errors, limits and privacy

MissingDocument / AccessDenied / ConnectionRequired / Temporary / UnsupportedFormat / InvalidDocument are separate states. Known TTN is not proof of rights. HTTP 401/403, wrong identity, HTML/base64/corrupt PDF each block printing. Seller/RD retain their exact lookup requirements; direct NP bulk printing uses the authenticated PDF response and local per-page TTN mapping instead.

Dedicated HTTPS/default-port allowlist only; real client disables redirects, transport rejects 3xx. TLS stays enabled. Exact JSON/PDF types, PDF header and native parsing validation. No API body, token, password or credential-bearing URL is logged/exposed.

Bounds: 20 MiB/response, 45 seconds/request, at most three 429/5xx attempts for RD/Seller respecting bounded Retry-After; direct NP has no automatic retries and retains unshortened rate-limit cooldowns. Five minutes/preparation, 100 selected orders, 100 distinct TTNs per direct NP request, 100 PDF pages/document, 250 pages/packet, 80 MiB compressed rasters, 25 million pixels/page, 256 MiB decoded print buffers. Auth/missing-document failures are not retried automatically. Cancellation is supported.

PDF/PNG only in memory for the current operation: no plaintext disk cache or external viewer. DPAPI protects separate ShippingLabels/settings.dpapi and attempts.dpapi under the existing channel-specific data root. Attempts retain 365 days / 10,000 entries, with attempt/job/printer/connection/carrier/document/TTN/page/fingerprint/time/state only, not buyer/PDF/image data.

## Printing

Windows 10/11 x64, WPF/STA with embedded PDFiumCore / PDFium 155.0.8057. No Adobe/viewer/process install. Native DLL is bundled/extracted by standard .NET single-file publishing; only the renderer library, never customer PDFs, is extracted. Full upstream notices are embedded and available in label settings. Source PDF points convert as mm=points×25.4/72, DIP=points×96/72, pixels=points×actual DPI/72. Rotation is native and tested with non-square pages.

The initially tested Windows.Data.Pdf backend rendered correctly but left app/test processes alive after shutdown, reproducing [Microsoft CsWinRT issue 1249](https://github.com/microsoft/CsWinRT/issues/1249). It was replaced, not shipped with forced-exit workarounds. PDFium initialization/use/disposal is serialized; document/page/bitmap and pinned buffers are released. JavaScript/form event execution is not enabled. Cancellation is checked between native operations/pages; one native parse/render is synchronous and not forcibly interrupted.

Read driver formats/capabilities, clone and validate a job-local PrintTicket→DEVMODE; validate GDI physical size, hard margins and X/Y DPI. Uniform 100% default; explicit scale/offset changes appear in preview. No global printer settings, port, calibration, spooler restart, or other queue jobs are changed. Incompatible packets block before StartDoc; the user must explicitly select compatible packets/format, never silent splitting or cropping.

Legacy drivers with empty media capabilities may use their actual sized default ticket, never an invented size. Some report a wider GDI frame than ticket paper. This is accepted only if its printable width matches selected ticket width, the extra width is accounted for by symmetric outside margins, and feed height agrees; unexplained/A4 substitution still fails. Preview separates ticket media, GDI frame and printable area. DPI changes between rasterization and submission block printing.

Since 1.0.91 each physical label/copy has its own StartDoc / EndDoc and one StartPage / EndPage. The complete packet is validated before its first job. Frozen visible-order→shipment→PDF-page→copy sequence; no fiscal order header over labels. Journal SubmissionUnknown **before** StartDoc; a positive job ID is saved while still Unknown, and Submitted is saved after successful EndDoc. Submitted does not confirm printed paper. On failure the accepted prefix is retained and the remaining jobs stop without automatic replay. Separate page attempts are combined for complete-document history, scoped to the same document and PDF fingerprint. Power-loss recovery by the driver can still repeat the interrupted label.
