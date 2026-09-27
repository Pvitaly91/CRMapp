# Shipping-label contracts and SDK diagnostic candidates

Checked 2026-09-27. Existing documents only; no shipment, registry, order or receipt mutations. POST /sites is authentication.

## Current direct NP implementation: SDK-derived, awaiting a local key

The user explicitly authorized research of a pinned **unofficial** SDK after documentation commit `2bb0740`. The following code was read from revision `1c7027da068363d9d06792a1e698b025d852b41d`; PHP/SDK was **not installed**:

- [Marking.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/Marking.php): single-number `printZebraMarking` URL below. No scan-sheet/document creation routes were implemented.
- [InternetDocument.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/InternetDocument/InternetDocument.php) and [GetDocumentListRequest.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/InternetDocument/RequestData/GetDocumentListRequest.php): model/method, StudlyCase properties, `d.m.Y` dates and string page.
- [BaseModel.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/BaseModel.php), [Request.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/Request.php), [DocumentListData.php](https://github.com/sashalenz/nova-poshta-api/blob/1c7027da068363d9d06792a1e698b025d852b41d/src/ApiModels/InternetDocument/ResponseData/DocumentListData.php): JSON envelope and IntDocNumber / Ref / SeatsAmount / DeletionMark. Unneeded buyer/address fields are discarded, not copied to diagnostics/history.

**Evidence levels:** the key/HTTPS JSON entry point are confirmed by the official integration overview; the specific methods, parameters, response assumptions and marking route are **found in SDK source**, exercised synthetically, **not yet checked with a real NP response**, and **not confirmed by current official documentation**. The historical documentation-access observations below do not substitute for live acceptance.

### Restricted requests

| Request | Implemented candidate / guard |
|---|---|
| POST `https://api.novaposhta.ua/v2.0/json/` | Exact root properties `apiKey`, `modelName="InternetDocument"`, `calledMethod="getDocumentList"`, `methodProperties`. Only `DateTimeFrom`, `DateTimeTo` (`dd.MM.yyyy`), `GetFullList=false`, `Page` (string 1–10); 1–7 explicitly chosen days. Duplicate/extra keys, guessed TTN/Ref search parameters, all save/update/delete/registry methods and other HTTP methods are rejected before sending. Request body must be buffered JSON ≤4096 bytes. |
| GET `https://my.novaposhta.ua/orders/printMarking100x100/orders[]/{PUBLIC_TTN}/type/pdf/zebra/zebra/apiKey/{KEY}` | SDK candidate for one 14-digit public TTN, only after its account lookup. Ref and order ID are never substituted in this route. Exact HTTPS host/path and key-character bounds, no query/fragment/userinfo/nondefault port. No redirects, including to another NP host. |

Neither operation creates/edits shipments, registries, addresses, afterpayment, orders or fiscal receipts. There is no generic API console or account enumeration. The existing transport's 20 MiB response limit, 45-second request timeout, bounded 429/5xx retries/cancellation and whole-operation five-minute timeout remain in use. API/HTTP/JSON error bodies and credential-bearing URLs never appear in messages/logs; exceptions do not retain secret-bearing inner network exceptions.

### Lookup and PDF acceptance

The user selects one existing NP TTN from the currently selected Prom order and explicitly starts the check. Its NP connection is bound to that Prom store, independent of Seller. The date interval defaults to today and is explicitly editable; it is **not inferred from order creation time**. No lookup runs on startup, opening settings, selecting an account or saving a key.

The response must contain boolean success, array data and array errors. Validate exact string IntDocNumber, separate UUID Ref when supplied, optional positive SeatsAmount and DeletionMark. No missing values are invented. A found public number without Ref can be inspected because the SDK marking route uses NUMBER, not Ref; internal document identity is explicitly `ttn:{PUBLIC_NUMBER}`, never presented as a UUID Ref. A supplied shipment Ref must agree.

The SDK does not establish page size/total/last-page semantics. A short page is **not** terminal: continue until an empty successful page. Repeated pages or page-limit exhaustion mean **incomplete**, even if the number was encountered; no marking request follows. A complete empty match means **“Не знайдено у перевіреній вибірці цього акаунта”**, not nonexistent TTN or invalid key. Period expansion is a separate manual check.

The marking response requires successful HTTP, application/pdf, PDF header and native PDFium parse; HTTP 200 alone does not establish identity. The PDFium inspector reads each page's physical dimensions and operation-local text. It excludes text outside page bounds and compares maximal numeric runs (not 14-digit prefixes of longer numbers); conflicting numbers fail closed. Missing text/unknown formatting remains unconfirmed and goes only to **diagnostic preview**. Raw extracted buyer text/PDF/PNG is never written to disk or a profile.

Multi-page diagnostics retain all pages and display API place count, PDF count and each actual page size. **Multi-place completeness is not established by count alone**; seat-number correspondence has not yet been verified on a real NP PDF. Current multi-place/unknown-count documents are diagnostic-only, not eligible for the packet. This limitation is explicit rather than printing just the first page.

### Conditional packet source

`NovaPoshtaDirectLabelSource` implements IShippingLabelSource. After a completed diagnostic for a single-place/one-page, text-identified, actual 100×100 mm PDF, the user may explicitly allow **that TTN** in the current session. Session evidence holds account/TTN/lookup metadata, not PDF or buyer text; restart/changed key/another account/unverified result cannot inherit permission. The label is fetched and inspected again for the confirmed packet. Multi-place, unidentified or incompatible PDF cannot silently enter it.

`ConfiguredShippingLabelSource` routes explicitly bound Prom/NP to that direct source. Failure never falls through to other accounts/Seller. Existing explicitly selected Seller alternative and Rozetka Delivery remain available. Unbound Prom/NP without explicitly selected Seller asks for an NP store binding, not mandatory Seller.

Existing LabelBatchPreparation, rasterizer, preview, WindowsLabelPrinter and separate attempt journal remain in use: stable NP→RD→NP pages, one job, complete preparation before confirmation, no partial transmission after a document failure. Printer scaling, receipt geometry and fiscal headers are unchanged.

Connections (stable ID/name/key) and per-Prom-store bindings are in the existing channel-specific ShippingLabels/settings.dpapi. Blank key field preserves its old value; rotating a key retains connection ID/binding and does not alter label/receipt history. Diagnostic settings can be saved without a printer, Checkbox or Seller. Keys/fingerprints are not included in reports. Real direct NP acceptance status: **“Очікує локального введення ключа”**.

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

The direct NP SDK candidate is separate from this verified Seller alternative. It is not advertised as access to all Prom shipments: real NP/account/PDF acceptance is still pending, as described above.

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

| Operation | Contract |
|---|---|
| GET https://rz-delivery.rozetka.ua/api/track/{id} | ID documented as EN number; statusCode=0, data.id/places/carrier_track_num. Check returned ID and selected public/partner TTN. Never substitute order ID. |
| GET /api/track/label?id=VERIFIED_ID | Swagger id query is an array of strings, form/explode. One-ID requests; statusCode=0, data.label base64 PDF. |

Separate locally entered Bearer RD token; never reuse Seller credentials on this host. Swagger mentions static tokens; its separate login/activity-token lifecycle is not reimplemented. No documented size/format parameter or multi-ID page map was found. Fetch one verified EN at a time, preserve all pages, and inspect actual PDF sizes. Do not invent format=100x100 or crop A4. Generic Seller fragile/top stickers are not parcel labels and are not used.

Documented places are a minimum, not an exact PDF page count: partner legs can add pages. The UI shows PDF page/part indices, not invented physical-seat IDs. Real partner metadata/page-to-seat correspondence remains unverified.

## Errors, limits and privacy

MissingDocument / AccessDenied / ConnectionRequired / Temporary / UnsupportedFormat / InvalidDocument are separate states. Known TTN is not proof of rights. HTTP 401/403, missing exact lookup, wrong identity, HTML/base64/corrupt PDF each block printing.

Dedicated HTTPS/default-port allowlist only; real client disables redirects, transport rejects 3xx. TLS stays enabled. Exact JSON/PDF types, PDF header and native parsing validation. No API body, token, password or credential-bearing URL is logged/exposed.

Bounds: 20 MiB/response, 45 seconds/request, at most three 429/5xx attempts respecting bounded Retry-After, five minutes/preparation, 100 selected orders, 100 PDF pages/document, 250 pages/packet, 80 MiB compressed rasters, 25 million pixels/page, 256 MiB decoded print buffers. Auth/missing-document failures are not retried automatically. Cancellation is supported.

PDF/PNG only in memory for the current operation: no plaintext disk cache or external viewer. DPAPI protects separate ShippingLabels/settings.dpapi and attempts.dpapi under the existing channel-specific data root. Attempts retain 365 days / 10,000 entries, with attempt/job/printer/connection/carrier/document/TTN/page/fingerprint/time/state only, not buyer/PDF/image data.

## Printing

Windows 10/11 x64, WPF/STA with embedded PDFiumCore / PDFium 155.0.8057. No Adobe/viewer/process install. Native DLL is bundled/extracted by standard .NET single-file publishing; only the renderer library, never customer PDFs, is extracted. Full upstream notices are embedded and available in label settings. Source PDF points convert as mm=points×25.4/72, DIP=points×96/72, pixels=points×actual DPI/72. Rotation is native and tested with non-square pages.

The initially tested Windows.Data.Pdf backend rendered correctly but left app/test processes alive after shutdown, reproducing [Microsoft CsWinRT issue 1249](https://github.com/microsoft/CsWinRT/issues/1249). It was replaced, not shipped with forced-exit workarounds. PDFium initialization/use/disposal is serialized; document/page/bitmap and pinned buffers are released. JavaScript/form event execution is not enabled. Cancellation is checked between native operations/pages; one native parse/render is synchronous and not forcibly interrupted.

Read driver formats/capabilities, clone and validate a job-local PrintTicket→DEVMODE; validate GDI physical size, hard margins and X/Y DPI. Uniform 100% default; explicit scale/offset changes appear in preview. No global printer settings, port, calibration, spooler restart, or other queue jobs are changed. Incompatible packets block before StartDoc; the user must explicitly select compatible packets/format, never silent splitting or cropping.

Legacy drivers with empty media capabilities may use their actual sized default ticket, never an invented size. Some report a wider GDI frame than ticket paper. This is accepted only if its printable width matches selected ticket width, the extra width is accounted for by symmetric outside margins, and feed height agrees; unexplained/A4 substitution still fails. Preview separates ticket media, GDI frame and printable area. DPI changes between rasterization and submission block printing.

One StartDoc / EndDoc, one StartPage / EndPage per label/copy. Frozen visible-order→shipment→PDF-page sequence; no fiscal order header over labels. Journal SubmissionUnknown **before** StartDoc; positive job ID means Submitted, not printed paper. Post-boundary failure never becomes a safe automatic retry.
