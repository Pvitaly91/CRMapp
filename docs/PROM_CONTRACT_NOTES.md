# Prom Orders read-only contract

Verified from the live official OpenAPI on 2026-09-23. These notes describe the published contract, not a successful call using a real seller token. No real seller API, customer data, fiscal operation or printer was used for verification.

## Sources

- [Official documentation UI](https://public-api.docs.prom.ua/)
- [Root OpenAPI / authorization / UTC dates / base URL](https://public-api.docs.prom.ua/documentation/index.yaml)
- [GET /orders/list, filters and cursor](https://public-api.docs.prom.ua/documentation/Orders/paths/GetOrderList.yaml)
- [GET /orders/{id}](https://public-api.docs.prom.ua/documentation/Orders/paths/GetOrder.yaml)
- [Order model](https://public-api.docs.prom.ua/documentation/Orders/schemas/Order.yaml)
- [Order product model](https://public-api.docs.prom.ua/documentation/Orders/schemas/Product.yaml)
- [Delivery provider / tracking declaration](https://public-api.docs.prom.ua/documentation/Orders/schemas/DeliveryProvider.yaml)
- [Delivery option](https://public-api.docs.prom.ua/documentation/Orders/schemas/DeliveryOption.yaml)
- [Payment option](https://public-api.docs.prom.ua/documentation/Orders/schemas/PaymentOption.yaml)
- [Payment status](https://public-api.docs.prom.ua/documentation/Orders/schemas/Payment.yaml)
- [Response language](https://public-api.docs.prom.ua/documentation/common/schemas/Language.yaml)
- [Error response](https://public-api.docs.prom.ua/documentation/common/responses/Error.yaml)
- [Fiscal attachment mutation, explicitly excluded](https://public-api.docs.prom.ua/documentation/Orders/paths/PostOrdersAttachReceipt.yaml)
- [Official EVO Python example](https://github.com/evo-company/company-api-example/blob/master/python/api_python3-5_example.py)

## Requests implemented

Base URL is `https://my.prom.ua/api/v1`; each request uses `Authorization: Bearer <token>`, `Accept: application/json`, `X-LANGUAGE: uk`. The client holds the supplied token only during calls and does not persist or log it.

Only these operations are implemented:

| Operation | Response envelope | Purpose |
| --- | --- | --- |
| `GET /orders/list?limit=1&sort_dir=desc` | `orders` array | Connection test without mutations |
| `GET /orders/list` | `orders` array | Full range scan |
| `GET /orders/{id}` | `order` object | Detail refresh, positive numeric ID only |

List supports `status`, `date_from`, `date_to`, `last_modified_from`, `last_modified_to`, `limit`, `sort_dir`, `last_id`. The current range scan uses creation dates, UTC conversion, `sort_dir=desc`, page size 100 and the last-ID cursor. No documented numeric maximum for `limit`, rate limit, offset, page number, next-page token or total count was found. The page size and 1,000-page safety ceiling are implementation safeguards, not asserted API limits.

The API describes `last_id` as restricting results to IDs no higher than the supplied value, without clearly distinguishing an inclusive/exclusive boundary. The implementation sends the smallest received ID without decrementing it, deduplicates the overlap, accepts an empty page or a single repeated boundary record as terminal, and marks other repetition/nonprogress/inconsistent-cursor conditions incomplete. It does not treat a merely short page as complete. This accommodates both boundary conventions without skipping an order ID. Real-account acceptance testing must confirm the seller API's cursor behavior.

Date inputs are UTC. Date-created response examples use ISO-8601 with an explicit offset. The client retains response offsets, filters the normalized `[from, toExclusive)` range locally, and keeps invalid raw dates while marking the range incomplete. UTC conversion is applied to the supplied offset range; the caller is responsible for constructing the intended Kyiv calendar-day boundaries, including DST.

Failures retain successfully read orders and explicitly report an incomplete result. User cancellation propagates as cancellation. HTTP errors are localized by the common transport; response bodies, credentials and customer values are not included in error messages. Malformed response envelopes/IDs fail incomplete instead of being interpreted as an empty successful scan.

## Normalization and documented limitations

| Normalized field | Official source | Handling |
| --- | --- | --- |
| Account/store key | Local connection ID + Prom + `id` | IDs cannot collide across local shop connections |
| Order number | `id` | No separate order-number field is documented |
| Created date | `date_created` | `DateTimeOffset?`; raw text preserved |
| Source/display status | `status` / `status_name` | Raw custom statuses preserved |
| Buyer | `client_last_name`, `client_first_name`, `client_second_name`, `phone` | Nullable, no invented recipient |
| Amount | `price` | String amount **excluding delivery**, raw text preserved |
| Currency | Not present in the published Order schema | Empty/unknown, never assumed UAH |
| Item | `products[].name`, `.sku`, `.quantity`, `.price`, `.total_price` | Nullable decimal values |
| Payment | `payment_option.name`, `payment_data.status` | Unknown/custom text preserved |
| Delivery | `delivery_option.name`, `delivery_cost` | Nullable |
| Tracking number | `delivery_provider_data.declaration_number` | Optional shipment with provider and delivery address |
| Recipient | Not separately documented | `null` |
| Discount / updated order date | Not documented in Order | Unknown |
| Fiscal receipt linkage | Not documented in GET Order | Empty reference lists |
| Seller deep link | Not documented | No fabricated URL |

`price` and product monetary values are documented as strings; quantity is an ordinary float count of units. They are **not** Checkbox's integer kopecks / thousandths of quantity. The parser accepts invariant decimal text and exact JSON numeric decimals; a currency suffix, comma, grouping separator or unrecognized syntax remains unknown. It does not parse an unknown amount as zero, scale it by 100, assume a currency, or infer a discount from totals. `full_price` is documented as optionally including delivery; it is not silently substituted for merchandise `price`.

The Order schema has no `required` list. Optional objects may be missing/null. `delivery_provider_data` is explicitly nullable for unsupported providers. Documented providers are `nova_poshta`, `justin`, `delivery_auto`, `ukrposhta`; unknown strings are retained rather than rejected. `recipient_warehouse_id` identifies a delivery branch, not a person.

Standard order statuses include pending, received, delivered, draft, paid and custom IDs. The English schema spells cancelled with two Ls, while Ukrainian/Russian descriptions use canceled; both must be treated as source values, not exhaustive enums. Payment data documents evopay and paid/unpaid/refunded/paid_out; it does not establish that another payment name implies a paid order.

## No guaranteed Prom-to-Checkbox UUID

GET Order does not document receipt UUID, fiscal number, `receipt_url`, `receipts[]` or a Checkbox receipt list. The published `POST /orders/{id}/attach_receipt` changes the order and returns a receipt URL. Its existence does **not** prove that GET returns that field. This application never invokes it. The client deliberately ignores undocumented receipt-like response fields; they cannot silently create an exact match. A missing Prom fiscal reference is a normal data limitation requiring candidate/manual matching, not proof that a receipt or order is absent.

## Verification

`PromOrdersTests` uses synthetic HTTP handlers against the real client/transport pipeline: inclusive and exclusive cursor responses; deduplication; short-page continuation; UTC query boundaries; decimal quantities and money; null objects; raw unknown money/date retention; account-scoped keys; detail loading; unsupported fiscal fields; API errors retaining partial results; repeated/page-limit failure; cancellation; malformed envelopes and invalid ID rejection. No actual token, buyer information or external writes are required.

Update 2026-09-24: authenticated read access was tested through the existing local DPAPI store: one latest-orders request (5 rows) and three detail requests all returned HTTP 200. Their full structures contained no fiscal-document key. The inspected orders are not yet confirmed by the user as already fiscalized; this is not a universal claim that Prom has no such read capability. Additional real fields included `delivery_recipient` and `date_modified`, not fiscal references; their semantics/normalization are outside the current fiscal-link change. See [FISCAL_LINK_REPORT.md](FISCAL_LINK_REPORT.md) for safe evidence, limitations, and an unsent support question. Physical printing is not part of this verification.
