# Контракти Checkbox / Prom / Rozetka

Дата перевірки: **2026-09-23**. База змін: поточний `main` від `b08e5ec82eb674496c8e0471e61423df521989e5`.
Джерела прочитано без реальних облікових даних. Нижче розділено підтверджений API-контракт і його обмеження.
Реальна read-only перевірка конкретного магазину потребує локального введення секретів у програмі.

## Джерела

### Prom

- [Офіційна документація](https://public-api.docs.prom.ua/), [OpenAPI index](https://public-api.docs.prom.ua/documentation/index.yaml).
- [GET orders/list](https://public-api.docs.prom.ua/documentation/Orders/paths/GetOrderList.yaml), [GET orders/{id}](https://public-api.docs.prom.ua/documentation/Orders/paths/GetOrder.yaml).
- [Order](https://public-api.docs.prom.ua/documentation/Orders/schemas/Order.yaml), [Product](https://public-api.docs.prom.ua/documentation/Orders/schemas/Product.yaml), [DeliveryProvider](https://public-api.docs.prom.ua/documentation/Orders/schemas/DeliveryProvider.yaml), [Payment](https://public-api.docs.prom.ua/documentation/Orders/schemas/Payment.yaml).
- Детальний маппінг і непідтверджені моменти: [PROM_CONTRACT_NOTES.md](PROM_CONTRACT_NOTES.md).

### Rozetka

- [Офіційний Seller API](https://api-seller.rozetka.com.ua/apidoc/), [його api_data.js](https://api-seller.rozetka.com.ua/apidoc/api_data.js).
- [Авторизація](https://api-seller.rozetka.com.ua/apidoc/#api-Authorization-PostSites), [список](https://api-seller.rozetka.com.ua/apidoc/#api-Orders-GetOrderSearch), [деталі](https://api-seller.rozetka.com.ua/apidoc/#api-Orders-GetOrderDetails), [існуючий фіскальний документ](https://api-seller.rozetka.com.ua/apidoc/#api-PrroModule-GetPrroPrintReceipt).
- Детальний маппінг і непідтверджені моменти: [ROZETKA_CONTRACT_NOTES.md](ROZETKA_CONTRACT_NOTES.md).

### Checkbox

- [Офіційна сторінка API-інтеграції](https://checkbox.ua/api-integration/).
- [Swagger](https://api.checkbox.ua/api/docs), [OpenAPI JSON](https://api.checkbox.ua/api/openapi.json), перевірена версія `2.107.0+7e00a4c6`.
- [Офіційна PDF-специфікація](https://wiki.checkbox.ua/specification/%D0%BA%D0%BE%D0%BF%D1%96%D1%8F_api_specification_%28eng%29.pdf) доповнює опис одиниць цін/кількості, які не пояснено в сучасній DTO-схемі.

## Авторизація і точні дозволені операції

| Сервіс | Авторизація | Список | Деталі |
|---|---|---|---|
| Prom | `Authorization: Bearer <token>`; `X-LANGUAGE: uk` | `GET https://my.prom.ua/api/v1/orders/list` | `GET /api/v1/orders/{id}` |
| Rozetka | `POST https://api-seller.rozetka.com.ua/sites`, JSON `username` і Base64 UTF-8 `password`; потім Bearer `content.access_token` | `GET /orders/search` | `GET /orders/{id}` |
| Checkbox | Наявний `POST /api/v1/cashier/signin` із `login/password`, потім Bearer | `GET /api/v1/receipts/search` | `GET /api/v1/receipts/{receipt_id}` |

Rozetka також має документований `GET /prro/receipt/{order_id}?type=link`: отримує лише посилання на вже наявний документ, за дозволу `prro_access`. Сам документ за цим URL не завантажується. Відмова цього додаткового запиту не губить замовлення.

Для маркетплейсів використано явний host/path/method allowlist. Інші POST, усі PUT/DELETE і довільні URL відповідей не використовуються. Наявне читання офіційного PNG Checkbox збережено; новий модуль не накладає на PNG дані покупця/замовлення і не запускає друк.

Rozetka може повідомити помилку через `success:false` при HTTP 200 — envelope перевіряється окремо. Токен переотримується максимум один раз на операцію після відмови авторизації. Prom API-токен, Rozetka login/password і Checkbox пароль зберігаються окремо; локальний ConnectionId не залежить від секретів.

## Фільтри, пагінація й діапазони

| Сервіс | Фільтри | Пагінація | Дати |
|---|---|---|---|
| Prom | `date_from/to`, `last_modified_from/to`, `status`, `sort_dir` | `limit`, `last_id`; відповідь `orders[]`; у поточній реалізації `sort_dir=desc` | UTC у запитах; ISO-8601 `date_created`, offset зберігається |
| Rozetka | `created_from/to`, `changed_from/to`, `status_updated_from/to`, `id`, `types`, статуси, ТТН, оплата/доставка | `page` від 1; `content.orders[]`; `content._meta.currentPage/pageCount/perPage/totalCount` | Фільтри `YYYY-MM-DD`; `created/changed` у прикладах без offset |
| Checkbox | `from_date`, `to_date`, `self_receipts`, `shift_id[]`, `branch_id[]`, `cash_register_id[]`, `desc` | `limit` 1..100, `offset >= 0`; наявна реалізація | ISO date-time: нижня межа включна, верхня виключна |

Prom не отримує механічно скопійовані `offset/page`, Rozetka — `limit/offset`. Prom-контракт не прояснює включення граничного `last_id`; клієнт не віднімає 1, перекриття дедуплікує. Лише коротка сторінка не завершує скан. Порожня сторінка або єдиний повторений граничний запис завершує його; неочікувані повтори чи непоступальний курсор позначають неповний результат.

Rozetka використовує `types=1` (всі групи). Старий `type=1` обмежує список замовленнями в обробці. Перебираються всі документовані `pageCount` сторінки. Відсутня/суперечлива пагінація, повтори або safety limit не стають успішною повною синхронізацією.

Обидва клієнти повертають уже отримані дані з ознакою неповноти при помилці посеред завантаження. Скасування передається викликачу. Стан останнього успішного оновлення не просувається неповною спробою. Відомі вручну/точно пов’язані ID читаються окремо поза поточним діапазоном, щоб освіжити старі замовлення.

**Rozetka timezone не підтверджений документацією.** Оригінальний рядок `RawCreatedAt` зберігається; для нормалізації застосовано явне припущення Europe/Kyiv, яке повідомляється в результаті синхронізації. Неоднозначна/неіснуюча DST-година не вгадується. Точний зв’язок за датою заборонений незалежно від цього припущення. Український календар застосунку будує межі Europe/Kyiv явно, без мовчазної залежності від системного часового поясу ПК. Prom конвертує їх в UTC, Checkbox передає відповідний offset.

## Нормалізовані дані

Ключ: `Marketplace + ConnectionId + OrderId`; однакові числові ID різних сервісів/магазинів не перетинаються.

| Значення | Prom | Rozetka | Checkbox |
|---|---|---|---|
| Внутрішній ID | `id` | `id` | receipt `id` UUID |
| Статус | `status/status_name` | `status/status_data` | `status/type` |
| Статус оплати | `payment_data.status` | `payment.payment_status` / `status_payment` | payments — факт способів/сум чека, не статус marketplace order |
| Покупець | `client_*_name`, `phone` | `user_title`, `user.contact_fio`, `user_phone` | не використовується як точний ключ |
| Отримувач | окремий підтверджений об’єкт у GET-схемі відсутній | `delivery.recipient_title/phone` | — |
| Сума | `price` без доставки | `cost_with_discount` із доставкою/знижками | `total_sum` у копійках |
| Товар | `products[].name/sku/quantity/price/total_price` | `purchases[].item_name/item.article/quantity/price_with_discount/cost_with_discount` | `goods[].good.name/code/price`, `goods[].quantity/sum` |
| Доставка/ТТН | `delivery_option`, `delivery_provider_data.declaration_number` | `delivery`, `ttn`, `carrier.carrier_track_num` | — |
| Фіскальні дані замовлення | GET-схема не документує | `prro.prro_receipt_fiscal_code`, опціональний PRRO URL | `id`, `fiscal_code`, `related_receipt_id` |

Prom і Rozetka грошові значення читаються як `decimal` у звичайних одиницях, без ділення на 100. Невідомий формат суми зберігається сирим текстом, не підміняється нулем. Окрема валюта у перевірених order-схемах не знайдена — залишається невідомою. Checkbox integer-ціни/суми діляться на 100, integer quantity — на 1000. Prom quantity — звичайна кількість одиниць із можливою дробовою частиною, Rozetka — ціла кількість товарів.

Суми замовлення та чека зберігаються й показуються окремо: доставка, знижка, повернення, часткова оплата і зміна замовлення не виправляються автоматично. Покупець і отримувач не об’єднуються. Доступні транспортні номери зберігаються всі; два номери можуть позначати одну фізичну доставку в різних перевізників.

## Чому 100% автоматичної прив’язки немає

Досліджено обидва напрямки:

1. Prom GET Order не документує receipt UUID/URL. Існування write endpoint `attach_receipt` цього не змінює; він не викликається.
2. Rozetka `prro_receipt_fiscal_code` — номер фіскального чека, не Checkbox UUID; сервіс фіскалізації може бути іншим або `manual`. PRRO URL не має гарантованого в документації Checkbox UUID-формату.
3. Checkbox `ReceiptOperativeDTO.order_id` описано як UUID. Немає документованого правила, яке ототожнює його з numeric Prom/Rozetka order ID або визначає для цього поля зовнішній магазин.
4. Checkbox `context` — непрозорий об’єкт додаткових даних; імена ключів маркетплейсу/магазину/замовлення не описано. UUID чи номер з довільного тексту, context або примітки не вважається точним ключем.
5. `related_receipt_id` стосується спорідненого чека, зокрема повернення; це не самостійне підтвердження marketplace order.

Алгоритм підтримує точну прив’язку лише за явно перевіреним receipt UUID із довіреного адаптера в тому самому контексті, але поточні live-контракти Prom/Rozetka не дають такого гарантованого поля, тому ці адаптери повертають `ReceiptIds=[]`. Суми/дати формують тільки кандидатів; товари показуються для ручного порівняння. Користувач підтверджує або відхиляє їх локально. Дані кандидата не показуються як уже підтверджене замовлення.

Фіскальні номери й опціональні URL Rozetka залишаються довідковими полями. Формат seller-order URL не підтверджений офіційним контрактом обох сервісів: посилання кабінету не вигадуються.

## Область Checkbox і межі перевірки

OpenAPI описує `self_receipts` як показ лише власних чеків і має default `true`. Поточне явне `self_receipts=true` збережено. У цьому режимі не можна обіцяти чеки інших касирів навіть за наявності їхніх замовлень у маркетплейсі. Доступ залежить від локального Checkbox-акаунта та прав API; відсутність у доступному списку не доводить, що чек не створений. Автоматичного розширення області немає.

Автоматичні тести використовують синтетичні відповіді API й перевіряють парсинг, пагінацію, неповні результати, дати, read-only запити та локальні правила зв’язування. Реальні продавецькі токени, поля конкретного магазину, інтеграційний context Checkbox, Rozetka timezone та деталі Prom cursor потребують локальної read-only перевірки.

Система друку не переписується. У поточному `main b08e5ec` є наявна історія `printed-receipts.json`; її збереження продовжується. Цей модуль не переносить і не заявляє реалізованими гарантії окремих старіших reliability-гілок, яких немає у взятому за основу `main`. Успіх фізичного друку не випливає з API-тестів.
