# Відновлення CRMapp із GitHub

Основний локальний репозиторій — `D:\DEV\CPP\checkbox`. Актуальний код знаходиться в гілці [`codex/checkbox-marketplace-orders`](https://github.com/Pvitaly91/CRMapp/tree/codex/checkbox-marketplace-orders), а не в старій `main`. Версія програми — 1.0.60. Ревізія та контрольні суми поточного пакета вказані в його `build-info.json` і `SHA256SUMS.txt`.

## Чиста копія та збірка

Потрібні Windows x64, Git, .NET SDK із `global.json` і доступ до NuGet під час першого відновлення залежностей. Старі збірки, журнали, локальні пробні скрипти й файли з папки Portable Soft для збірки не потрібні.

```powershell
git clone --branch codex/checkbox-marketplace-orders --single-branch https://github.com/Pvitaly91/CRMapp.git CRMapp
cd CRMapp
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Production.ps1
```

Скрипт запускає тести обох каналів, пакує однофайловий Production EXE, перевіряє запуск його окремої копії та створює `build-info.json`, `SHA256SUMS.txt` і ZIP. CRMapp на цьому ПК потрібно закрити через трей → «Вийти» перед перевіркою пакування. Готовий пакет — у `artifacts\production\<version>-<перші 12 символів SHA>`. Для запуску EXE потрібен .NET Desktop Runtime 8 x64; у пакеті є `Ensure-DesktopRuntime.ps1`.

Для конкретного набору джерел після клонування можна виконати `git checkout <commit-із-build-info.json>` і той самий скрипт пакування.

## Оновлення встановленої програми

Використовуйте скрипт із основного репозиторію:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\DEV\CPP\checkbox\scripts\Update-Production.ps1"
```

Після нового коміту для збірки й оновлення однією командою доступний параметр `-Build`. Стандартний шлях EXE — `D:\Portable Soft\CheckboxBatchPrinter.exe`. Скрипт перевіряє контрольні суми, відмовляє для відкритої програми та робить захищену резервну копію профілю перед заміною EXE.

## Що зберігається

У GitHub відстежуються вихідний код, XAML, іконки, повідомлення про ліцензії, проєкти, тести, закріплені версії SDK/пакетів і всі скрипти збірки, перевірки та оновлення. Локальні `bin`, `obj`, тестові результати, старі production-пакети та проміжні збірки відновлюються з цього коду й можуть бути видалені. Після прибирання залишається один поточний production-пакет для встановленого EXE.

Особисті налаштування, API-ключі, кеш замовлень і історія друку розташовані у `%LOCALAPPDATA%\CheckboxBatchPrinter-Production`; резервні копії — поруч зі встановленою програмою. Вони не публікуються на GitHub і не входять у прибирання робочого репозиторію. Для іншого ПК ключі потрібно встановити заново або відновити окремий сумісний резервний профіль; DPAPI-секрети прив’язані до користувача Windows.

Стара вкладена копія `artifacts\short-feed-auth-source` та її `production-worktree` більше не використовуються. Усі подальші зміни, збірки та оновлення виконуються з основного репозиторію.
