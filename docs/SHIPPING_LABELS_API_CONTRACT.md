# Official shipping-label contracts

Checked 2026-09-27. Existing documents only; no shipment, registry, order or receipt mutations. POST /sites is authentication.

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

Direct NP API/key support is **not implemented** pending a current official read/100×100-print contract. Do not advertise this alternative as access to all Prom shipments.

### Direct NP follow-up: contract still unverified (2026-09-27)

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

The generic JSON entry point is **not** added to the transport allowlist. All direct NP requests currently fail closed: there is no verified modelName/calledMethod/parameter allowlist. A route guessed from a third-party example or a synthetic PDF would not satisfy this contract. No placeholder connection test reports success and no unchecked redirect can receive a key.

Current source dispatch remains the previously verified Seller alternative / Rozetka Delivery implementation. Separate direct NP credentials, per-store NP connection selection and the direct adapter are **pending**, not represented as implemented. In particular, Seller cannot prove access to a Prom-origin TTN owned by another NP context. It must never be treated as proof that the TTN does not exist.

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
