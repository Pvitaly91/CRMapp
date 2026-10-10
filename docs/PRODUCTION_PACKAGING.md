# Production framework-dependent packaging

## Джерело та межі

Робочий репозиторій: `D:\DEV\CPP\checkbox`, гілка `codex/checkbox-marketplace-orders`.
`origin`: `https://github.com/Pvitaly91/CRMapp.git`. Свіжу копію можна клонувати в будь-яку папку; скрипти визначають корінь відносно свого розташування й не потребують колишніх локальних копій або worktree. Повний порядок відновлення: [RECOVERY_AND_CLEANUP.md](RECOVERY_AND_CLEANUP.md).

TargetFramework збережено: `net8.0-windows`, WPF. Наявний `win-x64.pubxml` і `build.ps1` не змінено.
Новий `Production-FDD.pubxml` вимикає self-contained/trimming/AOT/R2R, вмикає single-file/apphost і embedded PDB. Усі ці параметри обмежені новим профілем. `AppChannel` явно записується в assembly metadata; default — Development, production-профіль/скрипт — Production. `AppEnvironment` є єдиним місцем вибору кореня даних. Наявні сховища отримують дочірні шляхи від цього кореня: settings, credential, Logs, Cache, printed-receipts і всі Marketplaces-файли. Окремого журналу спроб у цьому HEAD немає; пакування не додає нової логіки друку.

## Наступний реліз

Після перевірки та коміту змін у робочому репозиторії:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Production.ps1
```

Опційно `-ExpectedCommit <повний SHA>` фіксує очікувану ревізію. Скрипт відмовляє для dirty-дерева й існуючого релізу. Нові результати: `artifacts\production\<version>-<sha>\app\CheckboxBatchPrinter.exe` та сусідній ZIP. Власні проміжні каталоги: `artifacts\production-build\<version>-<sha>-<guid>`, з окремими підкаталогами проєктів/каналів через SDK `--artifacts-path`. Старі artifacts і development bin/obj не перезаписуються.

Тести: скрипти Runtime/міграції на штучних даних; усі консольні тести для Development і Production; single-file EXE копіюється окремо в TEMP поза репозиторієм й двічі запускається через apphost з іншої CWD. `--verify-package` — явна offline-діагностика пакування: лише TEMP із маркером, синтетичні налаштування, створення/растеризація WPF-вікон, без production bootstrap, реальних секретів, HTTP-клієнтів або принтера. Звичайний запуск не приймає довільний data-root. Діагностичні дані/рендери не входять до ZIP.

Повний комплект: `app/CheckboxBatchPrinter.exe`, `Ensure-DesktopRuntime.ps1`, `RuntimeSupport.psm1`, `Copy-DevelopmentProfile.ps1`, `ProfileMigration.psm1`, `Update-Production.ps1`, `README.md`, `build-info.json`, `SHA256SUMS.txt`. Жодних секретів/кешів/бекапів/тестових binaries/SDK/runtime DLL. Скрипт перевіряє однофайловий publish, а не видаляє залежності після нього. `Test-Packaging.ps1` також перевіряє updater на синтетичних EXE/профілях: `-WhatIf`, відкритий процес, неправильні контрольні суми/канал, пониження версії, вибір останнього Production, запуск із розпакованого пакета, захищений backup, приховані файли й незмінність історії.

## Runtime та обмеження перевірки

На цьому ПК уже виявлено x64 host у `C:\Program Files\dotnet`, `Microsoft.WindowsDesktop.App 8.0.31` та `Microsoft.NETCore.App 8.0.31`. Перевстановлення не потрібне. Перевірка Runtime читає PE-архітектуру хоста й обидва shared frameworks; сам факт наявності dotnet або x86/іншої major-версії недостатній. Скрипт інсталяції використовує тільки офіційний стабільний пакет WinGet, з підтвердженням, без обходу підпису/хешу/перезавантаження.

Окреме середовище Windows без SDK наразі недоступне; SDK не видаляється. Копіювання тільки EXE та запуск через installed shared runtime перевіряють відсутність залежності від development DLL, але не замінюють тест на чистому ПК без SDK. Немає автоматичної перевірки реальних API/друку. Перенесення справжнього профілю виконується лише користувачем після підтвердження; у ході пакування не запускається.

Параметри узгоджено з документацією Microsoft: [single-file / framework-dependent](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview), [ізольовані artifacts SDK](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output).
