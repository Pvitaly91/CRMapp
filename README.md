# Checkbox Batch Printer

Локальна WPF-програма для Windows 10/11, яка завантажує **вже існуючі** фіскальні чеки через офіційний Checkbox API й пакетно друкує їх офіційні PNG-копії.

Програма не створює, не фіскалізує і не змінює чеки або зміни. У коді відсутні виклики write-endpoint-ів Checkbox, окрім `cashier/signin`, потрібного для отримання JWT.

## API

- Production host: `https://api.checkbox.ua`
- Авторизація: `POST /api/v1/cashier/signin`
- Пошук: `GET /api/v1/receipts/search`
- Зображення: `GET /api/v1/receipts/{receipt_id}/png`
- Офіційні джерела: [інтеграція](https://checkbox.ua/api-integration/), [Swagger](https://api.checkbox.ua/api/docs), [ReDoc](https://api.checkbox.ua/api/redoc)

## Розробка

```powershell
dotnet build CheckboxBatchPrinter.sln -c Release
dotnet run --project tests/CheckboxBatchPrinter.Tests -c Release
dotnet publish src/CheckboxBatchPrinter/CheckboxBatchPrinter.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

Пароль зберігається у `%LOCALAPPDATA%\CheckboxBatchPrinter\credential.bin` через Windows DPAPI. Налаштування, кеш і логи також залишаються лише в `%LOCALAPPDATA%\CheckboxBatchPrinter`.

## Фізична перевірка

Для end-to-end перевірки потрібні реальні облікові дані Checkbox та встановлений Windows-драйвер принтера. У «Налаштування → Діагностика» доступні перевірка API і тестовий друк. Жоден automated test не виконує фіскальних операцій.

Статус «Передано Windows» означає лише створення конкретного print job з відомим ID. Навіть статус завершення або зникнення job зі spooler не є доказом фізичного друку, тому програма показує «Результат не підтверджено». Повтор такого чека завжди потребує явного підтвердження користувача.

Детальний сценарій фізичної перевірки, результати й обмеження: [docs/RELIABILITY_REPORT.md](docs/RELIABILITY_REPORT.md).
