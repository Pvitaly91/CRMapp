# Rozetka Seller API: перевірений read-only контракт

Перевірено 2026-09-23 за офіційною документацією
[Rozetka Seller API](https://api-seller.rozetka.com.ua/apidoc/), зокрема її відкритим
[машиночитаним описом api_data.js](https://api-seller.rozetka.com.ua/apidoc/api_data.js).
Це перевірка публічної документації, не реальний вхід у магазин: облікові дані не використовувались.

## Авторизація і дозволені запити

- `POST https://api-seller.rozetka.com.ua/sites`: JSON `username`, `password`; пароль — Base64 байтів UTF-8, не хеш. Повертає `success`, `content.access_token`, `content.id` (менеджер), `content.market.id` (магазин), `content.permissions`.
- Наступні запити: `Authorization: Bearer <access_token>`, `Content-Language: uk`. Документація вказує подовження токена при використанні, 24 години без використання. Клієнт отримує сесію на початку операції, повторно авторизується максимум один раз за операцію після HTTP 401 або документованого `errors.code` 1018/1020.
- `GET /orders/search?page=N&types=1&sort=-id&created_from=YYYY-MM-DD&created_to=YYYY-MM-DD&expand=delivery,user,purchases,status_data,prro,carrier`.
- `GET /orders/{id}?expand=delivery,user,purchases,item_details,status_data` для деталей / відомого ID незалежно від діапазону. `item_details` документований разом із `purchases`.
- `GET /prro/receipt/{order_id}?type=link`: тільки за вже фіскалізованого чека та `prro_access` у дозволах сесії. Повертає `content.url`. Відмова у цьому додатковому запиті не видаляє замовлення. Сам URL не завантажується, UUID із нього не виводиться. Довільні URL не відкриваються клієнтом.

Офіційні розділи: [авторизація](https://api-seller.rozetka.com.ua/apidoc/#api-Authorization-PostSites),
[список](https://api-seller.rozetka.com.ua/apidoc/#api-Orders-GetOrderSearch),
[деталі](https://api-seller.rozetka.com.ua/apidoc/#api-Orders-GetOrderDetails),
[посилання на чек](https://api-seller.rozetka.com.ua/apidoc/#api-PrroModule-GetPrroPrintReceipt).

Жодні `PUT`, `DELETE`, створення замовлень, створення ТТН, зміни ПРРО або статусів не потрібні.
Помилки Rozetka можуть повертатися HTTP 200 із `success:false`: перевіряється envelope, а не лише HTTP-код.

## Пагінація та дати

Список знаходиться в `content.orders`, метадані — `content._meta.currentPage`, `pageCount`, `perPage`, `totalCount`.
`page` починається з 1. Перебираються всі `pageCount` сторінки, `limit/offset` не використовуються.
`types=1` означає всі замовлення. Старий `type=1` означає лише ті, що в обробці, тому його не використовуємо.
Документовані додаткові фільтри: `id`, `status`, `user_phone`, `userName`, `ttn`, `changed_from/to`, `status_updated_from/to`, `payment_methods`, `deliveries`, `prro_receipt_status`.

Сторінки upsert-яться за ID всередині конкретного підключення. Помилка посеред завантаження, некоректні / відсутні метадані, повторення сторінки без нових ID або межа 1000 сторінок дають `Complete=false`, зі збереженням уже отриманої частини. Неповні деталі також позначають синхронізацію неповною.

Поточна версія документації описує фільтри дат як `YYYY-MM-DD`; поля `created`/`changed` у прикладах мають `yyyy-MM-dd HH:mm:ss` без offset. **Часовий пояс API не підтверджений документацією.** Оригінальний рядок зберігається в `RawCreatedAt`; застосовано явне припущення Europe/Kyiv через `FLE Standard Time`, яке відображається в повідомленні синхронізації. Не використовується локальний часовий пояс ПК. Неоднозначні/неіснуючі години при переході DST залишають нормалізовану дату порожньою. Формат із явно переданим offset обробляється без такого припущення.

Для календарних фільтрів початок / кінець переводяться у Kyiv; виключна верхня межа перетворюється на останню включну дату. Точна inclusive/exclusive семантика API і часовий пояс потребують локальної read-only перевірки; автоматичний точний зв’язок на основі дат не встановлюється.

## Маппінг

| Дані | Документовані поля |
|---|---|
| ID / номер | `id`, у межах `Rozetka + ConnectionId + id` |
| Створення / оновлення | `created`, `changed` |
| Статус | `status`, `status_data.title`, `status_data.name_uk`, `status_data.name`; оригінальний код зберігається в `SourceStatus` |
| Оплата | `payment.payment_method_name`, `payment.payment_status.title/name`; fallback `payment_type_name`, `payment_type_title`, `status_payment.title/name`, `payment_status` |
| Покупець | `user_title.full_name`, `user.contact_fio`, `user_phone` |
| Отримувач | `delivery.recipient_title`, `delivery.recipient_phone` |
| Фінальна сума | `cost_with_discount` (із доставкою/знижками), fallback `cost`, `amount`; оригінал `RawTotal` |
| Знижка товарів | різниця наданих `amount` та `amount_with_discount`; не обчислюється, якщо одне з полів відсутнє |
| Доставка | `delivery.delivery_service_name`, `delivery.cost`, `delivery.city.city_name/title`, `place_street/house/number` |
| ТТН | `ttn`; додаткова накладна перевізника `carrier.carrier_track_num`, його `carrier_inner_id` |
| Товари | `purchases[].item_name`, `quantity`, `price_with_discount/price`, `cost_with_discount/cost`, `item.article` |
| Фіскальний номер | `prro.prro_receipt_fiscal_code`, display-only `FiscalReceiptNumbers` |
| Посилання на фіскальний документ | `content.url` окремого read-only PRRO endpoint, display-only `FiscalReceiptUrls` |

Грошові суми документовані як decimal (`123.45`) та наведені як рядки у JSON; клієнт читає їх через `decimal`, без ділення на 100. Окреме поле валюти в перевіреному order-контракті не знайдене, тому `Currency` залишається порожнім. Кількість документована як ціле число товарів, без тисячних часток.

Моделі: [Delivery](https://api-seller.rozetka.com.ua/apidoc/#api-Models-DeliveryDetails),
[Purchase](https://api-seller.rozetka.com.ua/apidoc/#api-Models-PurchaseDetails),
[User](https://api-seller.rozetka.com.ua/apidoc/#api-Models-UserDetails),
[OrderPaymentData](https://api-seller.rozetka.com.ua/apidoc/#api-Models-OrderPaymentData).

`delivery.another_recipient` не доводить відмінність від покупця: документація пояснює, що це ознака редагування при оформленні. Покупець та отримувач завжди читаються окремо.
`purchases[].ttn` позначено як невикористовуване — з нього не створюються відправлення. Верхній `ttn` і `carrier_track_num` можуть бути двома номерами тієї ж фізичної доставки, а не двома посилками; відображаються як доступні транспортні посилання без припущення про кількість посилок.

## Точний зв’язок із Checkbox: обмеження

`prro_receipt_fiscal_code` — фіскальний номер, **не Checkbox receipt UUID**. `prro_receipt_service_name` у прикладі дорівнює `manual`, тому навіть існування цього поля не означає Checkbox. Документація PRRO повертає URL без гарантованого формату Checkbox UUID. Підтвердженого спільного ID замовлення/Checkbox чека не знайдено: `ReceiptIds` для Rozetka залишається порожнім.

UUID-подібний рядок у фіскальному номері, коментарі або URL не стає точним ключем. Дані зберігаються для перегляду / ручного зв’язку. Формат URL сторінки замовлення в кабінеті продавця офіційним контрактом не підтверджений; `SellerUrl` не вигадується.

Схема документації має окремі неточності типів (`prro` названо Boolean за наявності вкладених полів, `payment_method_id` у старому order-полі Boolean, а в моделі оплати Integer), деякі JSON-приклади містять синтаксичні описки. Парсер спирається на назви полів та фактичну JSON-структуру, приймає null/відсутні значення і не підміняє їх вигаданими даними.

## Перевірки

Тести `RozetkaOrdersTests` працюють із підставним `HttpMessageHandler`: Base64 UTF-8 auth body, Bearer, повна пагінація й upsert, окремі покупець/отримувач, decimal гроші, null-поля, деталі, display-only PRRO URL, часткові відмови, bounded reauth для HTTP/JSON, некоректні метадані/повторна сторінка та cancellation. Усі приклади синтетичні; реальні замовлення / чеки не створюються.
