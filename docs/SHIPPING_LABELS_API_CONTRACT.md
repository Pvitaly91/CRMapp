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
