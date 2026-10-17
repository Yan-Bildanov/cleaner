# Cleaner — заметки для разработки

Архитектура, принятые решения и подводные камни. Описание для пользователя — в [README.md](README.md).
Личные заметки владельца (не в репозитории) — `NOTES.local.md`.

## Сборка и тесты
- .NET 10 SDK (x64). Если в PATH раньше стоит старый 32-битный `C:\Program Files (x86)\dotnet\` без SDK,
  вызывайте `"C:\Program Files\dotnet\dotnet.exe"` по полному пути.
- Сборка: `dotnet build Cleaner.slnx`
- Тесты: `dotnet test Cleaner.slnx --filter "Category!=RealSystem&Category!=Demo&Category!=Diag"`
  - `Category=RealSystem` — прогон сканера и проверок здоровья на реальной системе, **только чтение**;
  - `Category=Demo` — создаёт демо-партию в настоящем карантине из файлов `demo-sandbox/`;
  - `Category=Diag` — диагностика чтения настоящего карантина.
- Установка/обновление: `tools\install.cmd` (журнал `tools\install.log`) — Release single-file self-contained
  в `%USERPROFILE%\.cleaner\app\Cleaner.exe`, ярлыки на рабочем столе и в «Пуске», перенаправление
  задачи планировщика. Иконка генерируется `tools\make-icon.ps1`.

## Структура
- `src/Cleaner.App` — WPF (WPF-UI 4.x, стиль Windows 11, тёмная тема), собирается в `Cleaner.exe`.
- `src/Cleaner.Core` — вся логика, без UI:
  - `Rules/BuiltInRules.cs` — **белый список** того, что программа вообще может трогать. Пути с `%ПЕРЕМЕННЫМИ%`
    и `*` в сегментах; ссылки/junction не обходятся.
  - `Scanning` — анализ (только чтение), возрастной фильтр, признак «приложение открыто».
  - `Cleaning` — карантин: партия = `batch.json` + `manifest.jsonl` (исходный путь → `files\000001`) + `files\`.
    Хранение 7 дней. Возврат не перезаписывает файл, если приложение уже создало новый.
  - `Health` — проверки здоровья: `HealthRules` — чистая логика (покрыта тестами), `HealthCollector` —
    WMI / реестр / `powercfg` / `nvidia-smi`, `HardwareCollector` — информация о железе.
  - `Auto` — автоочистка и её настройки; `Platform/AutoCleanTask` — задача планировщика через `schtasks /XML`.
  - `AppPaths` — где лежат данные программы.
  - `Programs` — вкладка «Программы» (бесплатная замена IObit Uninstaller Pro):
    - `ProgramInventory` — список из трёх веток `...\CurrentVersion\Uninstall` (HKLM 64, HKLM WOW6432Node, HKCU),
      только чтение. Отбрасываются: без DisplayName, `SystemComponent=1`, обновления (`ParentKeyName`, `ReleaseType`),
      записи без способа удаления. Дубли 32/64 сливаются. Нет `InstallDate` — берётся дата изменения ключа;
      нет `EstimatedSize` — размер досчитывается по папке, но только если `IsProgramOwnFolder` (не корень, не Program Files,
      не Windows, не профиль).
    - `UninstallCommand` — разбор UninstallString (кавычки, пути с пробелами без кавычек, rundll32).
      MSI всегда удаляется как `msiexec /x {код}`: у многих UninstallString содержит `/I` (это «изменить»).
    - `Uninstaller` — запускает деинсталлятор (`UseShellExecute`, UAC он просит сам) и ждёт. NSIS/Inno копируют себя
      во временную папку и сразу выходят, поэтому после выхода ждём, пока исчезнет ключ реестра или пропадут новые
      процессы с именами деинсталляторов (`LooksLikeUninstaller`); кнопка «Хватит ждать» прерывает ожидание.
      Сам ничего не удаляет.
    - `LeftoverScanner` — хвосты после удаления (только чтение): папка установки и пустая папка над ней; папки с ТОЧНЫМ
      (нормализованным) именем программы/издателя в Program Files, ProgramData, AppData, Programs, «Пуске»; ярлыки;
      ключи `HKCU\Software\…`, `HKLM\SOFTWARE\…` (64 и 32). Имена, общие с оставшимися программами, и слова из `Generic`
      не используются; имя-префикс другой программы (python → Python Launcher) — только Medium. Папки, где лежит
      другая установленная программа, и общие корни не предлагаются никогда. High отмечено по умолчанию, Medium — нет.
      Прогон по всем программам системы: тест `Leftover_scan_for_every_program_read_only` (RealSystem).
    - `LeftoverRemover` — в карантин: файлы переносом, папки запоминаются (`Kind=dir`) и удаляются, когда опустеют,
      ключи — `Cleaning/RegistryBackup`: экспорт `reg.exe export` в .reg партии, затем удаление; возврат — `reg import`.
      Перед действием ещё раз: ключ только `HKxx\Software\Имя[\Имя]` (`LeftoverPlan.IsSoftwareSubkey`), папка только «своя».
    - Пункты с правами администратора (Program Files, ProgramData, HKLM) окно не трогает, а запускает через UAC
      `Cleaner.exe --remove-leftovers plan.json` (`LeftoverPlan`); помощник перепроверяет план и пишет `plan.json.result`.
  - Решение по реестру (октябрь 2026): общей «чистки реестра» по-прежнему нет; удаляются только ключи удалённой программы
    по точному имени и только через карантин с .reg-копией.
    - Этап 3 (октябрь 2026):
      - TFM — `net10.0-windows10.0.19041.0` во всех проектах (нужен WinRT `PackageManager` для Store).
      - `StoreApps` — приложения Store текущего пользователя (`ProgramScope.Store`). Скрыты: frameworks, resource,
        подпись System и пакеты без записи в «Пуске» (`GetAppListEntries().Count == 0`). Защищены (`NoRemove` +
        `NoRemoveReason`): Microsoft Store, DesktopAppInstaller (winget), StorePurchaseApp, XboxIdentityProvider, Claude.
        Удаление — `RemovePackageAsync`; хвосты для Store не ищем (данные в Packages убирает Windows).
      - Тихий режим `UninstallCommand.Quiet`: MSI `/x /qb /norestart`, QuietUninstallString, Inno (`uninsNNN.exe` →
        `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`), NSIS (строка «Nullsoft» в exe → `/S`). Иначе — обычный запуск.
      - Пакетное удаление — по очереди, затем общий поиск хвостов (слияние по Kind+Path+View).
      - `UninstallCommand.IsBroken` — путь к деинсталлятору есть, файла нет → кнопка «Принудительно».
      - `ForcedUninstall` — деинсталлятор не запускается: план = хвосты + запись в Uninstall (единственное разрешённое
        место под `Software\Microsoft`, см. `LeftoverPlan.IsUninstallEntry`); перед файлами закрываются процессы из папок.
      - Хвосты теперь включают службы (`SystemServices`) и задачи Планировщика (`ScheduledTasks`, COM Schedule.Service),
        запускающие файлы из уверенных папок программы. Карантин: служба — `.reg` ключа + `sc delete` (возврат —
        импорт, работает после перезагрузки), задача — XML + удаление (возврат — `schtasks /create /xml`).
      - Защита: `SystemServices.IsSystemLocation` — файл в папке Windows или в любой папке `Microsoft`/`Windows…`
        (Защитник живёт в ProgramData\Microsoft\Windows Defender!) — такие службы, задачи и процессы не трогаются никогда;
        службы со Start ≤ 1 и драйверы — тоже. Задачи из `\Microsoft\` — никогда.
    - Этап 4 — монитор установок (`InstallMonitor`, `TraceStore`):
      - Снимок «до» и «после» (без драйверов и перехватов): папки и файлы на 2 уровня в Program Files (x86), ProgramData,
        AppData (Roaming, Local, Local\Programs, LocalLow), «Пуске» и на рабочих столах; ключи `Software` на 2 уровня
        (HKCU, HKLM 64/32, включая `Classes\*`); службы; задачи; значения Run/RunOnce; записи Uninstall.
        Шум не снимается: `NoiseNames` (Temp, Microsoft, Packages, кэши…) и ветки `Microsoft`/`WOW6432Node`/`Policies`.
        Снимок ~0,8 с; два снимка подряд без установки — 0 изменений (тест `Monitor_snapshot_noise_without_install_read_only`).
      - Разница → `InstallTrace` (вложенное в новую папку/ключ схлопывается), пользователь снимает лишние галочки и сохраняет.
        Хранение: `%USERPROFILE%\.cleaner\traces\*.json`; незаконченный снимок — `traces\pending.json` (переживает закрытие окна, 3 дня).
      - При удалении программы `InstallMonitor.LeftoversFor` = записанное (`FromTrace`, всегда High) + найденное по именам.
        Те же защиты: чужие папки, системные службы/задачи не предлагаются.
      - Новые виды хвостов: `File`, `RegistryValue` (только в Run/RunOnce, `LeftoverPlan.IsAutostartKey`; карантин `regval` —
        JSON с типом и данными). Ключи `Software\Classes\Имя` (сопоставления файлов) — ТОЛЬКО из записи
        (`IsTracedClassesKey`, без CLSID/Directory/.exe и т. п.), по именам их не ищем.
      - Запись удаляется, когда программы больше нет и с карточкой хвостов разобрались.
      - Порядковые значения `LeftoverKind` хранятся на диске — новые добавлять только в конец.
  - Этапы вкладки «Программы»: 1) список + штатное удаление — готово; 2) поиск и зачистка хвостов через карантин — готово;
    3) пакетное и принудительное удаление, приложения Microsoft Store — готово; 4) монитор установок — готово;
    5) расширения браузеров — готово (отдельная вкладка «Расширения»).
  - `Browsers/BrowserExtensions` — расширения браузеров на Chromium (Chrome, Edge, Brave, Vivaldi, Яндекс):
    профили из `User Data\*\Preferences` + `Secure Preferences` (слияние `extensions.settings`), имена профилей из
    `Local State`, манифест из `Extensions\id\версия`, имена `__MSG_…__` из `_locales`. Источник по `location`:
    1 — магазин/вручную, 2/3/6 — программа (если есть ключ `Software\<браузер>\Extensions\id`) или «вместе с браузером»
    (ключа нет — Docs Offline и т. п.), 4 — разработчик, 7/9 — политика. Предупреждения: подмена поиска/домашней/новой вкладки,
    поставлено программой. Права — справочно.
  - Решение по удалению расширений: файлы профиля НЕ меняем (Chrome подписывает Secure Preferences и при вмешательстве
    сбрасывает настройки, а расширения из магазина возвращает синхронизация). Поставленные программой — ключ(и)
    `HKxx\SOFTWARE\<браузер>\Extensions\<id>` в карантин (`LeftoverPlan.IsBrowserExtensionKey`, бывает и в 64-, и в 32-битной
    ветке — убираем все), браузер сам удаляет «осиротевшее» внешнее расширение при запуске. Остальные — кнопка открывает
    `chrome://extensions/?id=…` в нужном профиле (`--profile-directory`), адрес копируется в буфер.
  - `App/Elevation` — общий запуск помощника через UAC (`RemoveAsync`: что можно — сразу, остальное — с правами администратора).
- `tests/Cleaner.Core.Tests` — xUnit, всё удаление — только в песочнице во временной папке.

## Принятые решения
- **C#/.NET**, а не Python: родной WinAPI, быстрый запуск, один .exe, меньше ложных срабатываний антивирусов.
- **Только белый список путей.** Никакого «найди весь мусор на диске».
- **Чистки реестра нет** — бесполезна и рискованна. Обновления программ и драйверов — не делаем.
- **Всё удалённое — в карантин на 7 дней** (перенос на том же диске = мгновенное переименование).
  Корзина — единственное безвозвратное действие, только с отдельным подтверждением.
- **Открытые файлы не трогаем.** Chrome и Проводник держат кэш с `FILE_SHARE_DELETE`: Windows разрешает
  такой файл переместить, но программа продолжает им пользоваться уже в карантине. Поэтому перед переносом —
  пробное открытие с `FileShare.None`.
- **Автоочистка строже ручной:** без корзины и админ-пунктов; если приложение открыто — его пункт пропускается целиком.
- Права администратора — только по кнопке (runas), а не при каждом запуске.

## Подводные камни Windows
- **Данные не в `%LOCALAPPDATA%`, а в `%USERPROFILE%\.cleaner`.** Если программу запускает приложение,
  упакованное в MSIX (например, ИИ-ассистент во время разработки), Windows перенаправляет записи в AppData в папку
  этого пакета (`...\Packages\<pkg>\LocalCache`) — карантин одного запуска не виден в другом.
  Миграция со старого места — `AppPaths.MigrateLegacyQuarantine` (по файлам: папку целиком оттуда не перенести).
- По той же причине `tools\install.cmd` нужно запускать «снаружи» (двойным кликом / `explorer.exe tools\install.cmd`),
  иначе ярлык в «Пуске» (это тоже AppData) окажется в виртуализованной папке.
- Окно, запущенное от администратора, нельзя закрыть из неадминистративного процесса (UIPI).
  Для пересборки — служебный файл `%USERPROFILE%\.cleaner\close.signal`: программа закрывается сама.
- Файлы из `C:\Windows\Temp` переносятся и удаляются только с правами администратора.
- Температура процессора через ACPI на многих ноутбуках с Ryzen недоступна даже администратору;
  драйверы уровня ядра (как у HWMonitor) сознательно не используем.

## Данные программы (`%USERPROFILE%\.cleaner`, скрытая)
- `Quarantine\` — партии карантина; `settings.json` — автоочистка; `last-auto-run.json` — последний результат;
- `logs\errors.log` — ошибки (смотреть первым при «программа упала»), `logs\auto.log` — журнал автоочистки;
- `app\Cleaner.exe` — установленная версия; `close.signal` — служебный сигнал на закрытие.
