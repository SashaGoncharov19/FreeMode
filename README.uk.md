# GTA Network (відроджений)

[![Build](https://github.com/SashaGoncharov19/FreeMode/actions/workflows/build.yml/badge.svg)](https://github.com/SashaGoncharov19/FreeMode/actions/workflows/build.yml)

GTA Network (GTA:N) — мультиплеєрна модифікація для Grand Theft Auto V: виділений сервер зі скриптовим
API (C#, VB, JavaScript на клієнті) та внутрішньоігровий клієнт, який синхронізує гравців, транспорт, зброю
і світ між усіма підключеними до сервера.

Це відроджена версія коду. Порівняно з оригіналом (Visual Studio 2017, .NET Framework, тільки Windows) тут є:

* **один `dotnet build` для всього** — SDK-style проєкти, NuGet-пакети, одна `GTANetwork.sln`;
* **сервер працює нативно на Linux** (а також Windows/macOS) на .NET 8 — скрипти компілюються Roslyn'ом,
  Unix-сигнали зупиняють його коректно, HTTP-файлсервер більше не потребує Nancy/OWIN;
* **кросплатформний лаунчер** (`GTANetwork.Launcher`), який запускає гру через Steam/Proton на Linux
  стандартним ASI-лоадером ScriptHookV замість DLL-інжекції;
* **GitHub Actions**, що збирають, тестують і пакують усе (Linux-джоб + Windows-джоб для C++/CLI-частини);
* внутрішньоігровий клієнт *компілюється* на будь-якій ОС завдяки керованій заглушці ScriptHookVDotNet.

> Чесний статус: система збірки, сервер (Linux) і логіка лаунчера перевірені тестами. Внутрішньоігрова
> частина (хуки ScriptHookV / ScriptHookVDotNet, патерни пам'яті, нативи) не змінювалась з 2019–2020 років
> і тут її не було з чим протестувати. Див. [Відомі обмеження](#відомі-обмеження).

Повна англійська документація: [README.md](README.md).

---

## Структура репозиторію

| Шлях | Проєкт | Ціль | Що це |
| --- | --- | --- | --- |
| `Shared/` | `GTANetworkShared` | net48 + netstandard2.0 | Пакети, властивості сутностей, математика, налаштування, protobuf-контракти — спільне для клієнта й сервера. |
| `Server/` | `GTANetworkServer` | **net8.0** | Виділений сервер: Lidgren UDP, стрімер, ресурси, скриптовий API (`API.cs`), HTTP-файлсервер. Працює на Linux. |
| `Launcher/` | `GTANetwork.Launcher` | **net8.0** | Кросплатформний лаунчер (Steam / Proton / Windows). |
| `Client/` | `GTANetwork` (клієнт) | net48 | Внутрішньоігровий клієнт, який ScriptHookVDotNet завантажує всередину `GTA5.exe`. Синхронізація, стрімінг, CEF-інтерфейс, JS-движок, DirectX-хук. |
| `NativeUI/` | `NativeUI` | net48 | Бібліотека меню в стилі Rockstar (GPL-3.0). |
| `Shv.NET/` | `ScriptHookVDotNet` | C++/CLI, net48 | Форк ScriptHookVDotNet v3 від crosire. **Тільки Windows + MSVC**, потрібен ScriptHookV SDK. |
| `Shv.NET/ref/` | `ScriptHookVDotNet.Ref` | net48 | Керована *заглушка* з тим самим публічним API, щоб клієнт компілювався на Linux. Ніколи не постачається. |
| `Subprocess/` | `GTANLauncher`, `GTANSubprocess`, лаунчерний `GTANetwork.dll` | net48 | Класичний триступеневий Windows-лаунчер (реєстр, оновлення, інжекція DLL). Збирається і працює на Windows як і раніше. |
| `Map2Resource/` | `Map2Resource` | net8.0 | Конвертує XML з Map Editor у серверні map-ресурси. |
| `Tools/GTANetwork.Bot/` | `GTANetwork.Bot` | **net8.0** | Headless-клієнт, що говорить справжнім протоколом: підключається до сервера, завантажує карту і скрипти, чатиться, виконує команди. Використовується інтеграційним тестом у CI. |
| `libs/` | — | — | Бінарні залежності без NuGet-аналога: кастомний форк Lidgren, CEF 85 + CefGlue, набір SharpDX, NAudio, нативні V8/EasyHook. |
| `eng/`, `.github/workflows/` | — | — | Скрипти версіонування, smoke-тест сервера, пакування клієнта; CI. |

## Як це працює

### Клієнт (що відбувається після натискання «Грати»)

1. **ScriptHookV** (Alexander Blade, закритий код, розповсюджувати не можна) дає нативний хук; його
   `dinput8.dll` завантажує всі `*.asi` з папки гри.
2. **ScriptHookVDotNet** (`Shv.NET/`, C++/CLI) піднімає .NET Framework усередині `GTA5.exe`, надає API `GTA.*`
   і завантажує з `ScriptsLocation` (`ScriptHookVDotNet.ini`) тільки `GTANetwork.dll` та `NativeUI.dll`.
3. **`GTANetwork.dll`** (`Client/`) — це `GTA.Script`. `Main.cs` створює меню (браузер серверів, швидке
   підключення), підключається через Lidgren; папки `Sync/`, `Streamer/`, `Javascript/`, `GUI/` реалізують
   синхронізацію сутностей, стрімінг, JS-движок (ClearScript V8) і CEF-оверлей, що малюється через хук
   DirectX 11 swap-chain.
4. Клієнт знаходить свою папку встановлення (`bin/`, `cef/`, `images/`, `settings.xml`, `logs/`) через
   значення реєстру, яке пише класичний лаунчер, або — нове — змінну середовища `GTAN_INSTALL_DIR`, або
   піднімаючись від власного розташування (`<install>/bin/scripts/GTANetwork.dll`). Останні два варіанти і
   роблять можливою роботу під Proton.

Класичний Windows-ланцюжок: `GTANLauncher.exe` → (реєстр, самооновлення) → `launcher/GTANSubprocess.exe` →
завантажує `launcher/GTANetwork.dll` (`MainBehaviour`) → перевіряє залежності, патчить налаштування гри,
запускає GTAVLauncher/Steam, чекає `GTA5.exe` і через `CreateRemoteThread` інжектить `bin/ScriptHookV.dll`,
`bin/ScriptHookVDotNet.dll` та нативні DLL.

Новий кросплатформний ланцюжок: `GTANetwork.Launcher` копіює `dinput8.dll`, `ScriptHookV.dll`,
`ScriptHookVDotNet.asi` + `.ini` у папку гри, запускає гру через Steam або Proton, чекає завершення
`GTA5.exe` і повертає все назад.

### Сервер

* `Program.cs` читає `settings.xml`, створює `GameServer` і «тікає» його 60 разів на секунду.
* `GameServer` (`GameServer.cs`, `ProcessMessages.cs`, `Packets.cs`) володіє Lidgren `NetServer`: підтвердження
  підключень, перевірка версій, sync-пакети, стрімер сутностей, пікапи, колшейпи.
* `Resources.cs` запускає ресурси зі `settings.xml`. `meta.xml` ресурсу описує серверні скрипти
  (`lang="csharp|vbasic|compiled"`, компілюються **під час запуску Roslyn'ом**, `Managers/ScriptCompiler.cs`),
  клієнтські JS-скрипти (хешуються і передаються гравцям по UDP або через HTTP-файлсервер), файли, карти,
  залежності та експорти. Кожен публічний клас-нащадок `GTANetworkServer.Script` отримує екземпляр `API`.
* `Server/resources/example` — мінімальний C#-геймод, який стартує з дефолтним `settings.xml`.

## Збірка

* [.NET 8 SDK](https://dotnet.microsoft.com/download) на будь-якій ОС — збирає **все кероване**, включно з
  .NET Framework-клієнтом (через `Microsoft.NETFramework.ReferenceAssemblies`).
* Windows + Visual Studio 2022 з *C++/CLI support* і *Windows SDK* — тільки для справжнього
  `ScriptHookVDotNet.dll`. [ScriptHookV SDK](http://www.dev-c.com/gtav/scripthookv/) (`inc/`, `lib/`) кладеться в
  `Shv.NET/sdk/`, або запустіть `Shv.NET/sdk-compat/install-compat-sdk.ps1` з Developer PowerShell: він
  ставить еквівалентні декларації і генерує import-бібліотеку (dev-c.com блокує автоматичні завантаження,
  тож саме це використовує CI, якщо змінна репозиторію `SHV_SDK_URL` не вказує на копію SDK).

```bash
# все (на Linux/macOS клієнт компілюється проти заглушки)
dotnet build GTANetwork.sln -c Release

# сервер для Linux
dotnet publish Server/GTANetworkServer.csproj -c Release -r linux-x64 --self-contained false -o out/server

# лаунчер одним файлом
dotnet publish Launcher/GTANetwork.Launcher.csproj -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true -o out/launcher

# тільки Windows: C++/CLI-хук (після цього перезбирання solution підхопить його автоматично)
msbuild Shv.NET/ScriptHookVDotNet.sln /p:Configuration=Release /p:Platform=x64
```

Якщо існує `Shv.NET/bin/ScriptHookVDotNet.dll`, `Client` і `NativeUI` лінкуються з ним; інакше — із
`Shv.NET/ref` (властивість `UseRealShvdn`). Постачати можна лише бінарники, зібрані зі справжньою DLL.

Схема версій залишилась оригінальною: `0.1.<днів з 2016-01-01>.<хвилин UTC / 2>` (протокол їх порівнює);
рахується в `Directory.Build.props`, у CI — з дати коміту (`eng/version.sh`).

## Сервер на Linux

```bash
dotnet publish Server/GTANetworkServer.csproj -c Release -r linux-x64 --self-contained false -o ~/gtan-server
cp vehicleData.json ~/gtan-server/
cd ~/gtan-server && ./GTANetworkServer
```

Редагуйте `settings.xml` (назва, `serverport`, `maxplayers`, `password`, `<resource src="..."/>`). Відкрийте
**UDP 4499** (і **TCP 4499**, якщо `<httpserver>true</httpserver>`). `Ctrl+C`, `SIGTERM` (systemd, Docker) і
`SIGHUP` зупиняють сервер коректно. Публічного мастер-сервера (`master.gtanet.work`) більше не існує, тому
`<announce>` вимкнено, гравці підключаються за IP.

`eng/smoke-test-server.sh <dir>` запускає опублікований сервер, перевіряє компіляцію прикладу, `/manifest.json`
і зупинку по `SIGTERM`; CI робить це на кожен push.

### Спробувати сервер без гри: headless-бот

`GTANetwork.Bot` — консольний клієнт, що реалізує клієнтську сторону протоколу (Lidgren UDP, protobuf-пакети з
`GTANetworkShared`): discovery, handshake з `ConnectionRequest`, завантаження карти і клієнтських скриптів,
`ConnectionConfirmed`, чат і команди, синхронізація позиції; усе, що сервер надсилає (створення сутностей,
виклики нативів, події), він друкує у читабельному вигляді.

```bash
dotnet run --project Tools/GTANetwork.Bot -- --host 127.0.0.1 --port 4499 --name Tester --discover \
  --say "/help" --say "/veh adder" --say "/pos" --say "hello" --duration 5
```

З `--interactive` бот читає рядки чату і `/команди` зі stdin до `/quit`, тож сервером можна керувати прямо з
термінала.

`eng/integration-test.sh <server dir> <bot>` піднімає сервер із вбудованим геймодом `freeroam`, заходить на нього
ботом, виконує кілька команд і перевіряє відповіді, а потім підключає двох ботів одночасно і перевіряє, що чат,
створення авто та синхронізація позицій ретранслюються між ними; CI робить це на кожен push. Бот також зручний,
щоб бачити, що саме робить геймод «по дротах», коли розробляєш серверні скрипти на Linux.

## Гра на Linux (Proton)

Підтримується лише **GTA V Legacy** (Steam app 271590); Enhanced має інший виконуваний файл.

### Швидкий старт (один скрипт)

```bash
# 0. інструменти (protontricks скрипт ставить сам, коли треба)
sudo apt install curl unzip python3
# 1. запустіть GTA V один раз через Steam (створює Proton-префікс)
# 2. скачайте zip ScriptHookV з http://www.dev-c.com/gtav/scripthookv/ браузером (сайт блокує скрипти)
# 3. поставте все в ~/GTANetwork з останнього GitHub-релізу:
curl -fsSL https://raw.githubusercontent.com/SashaGoncharov19/FreeMode/master/eng/setup-linux.sh | bash -s -- --name ВашНік
```

`eng/setup-linux.sh` скачує клієнтський пакет, self-contained Linux-лаунчер, сервер і бота (ставити .NET не
треба), витягує `ScriptHookV.dll` + `dinput8.dll` з найновішого `~/Downloads/ScriptHookV*.zip` (або `--shv <zip>`),
пише `settings.xml` (метод запуску `proton`, ваш нік, `127.0.0.1:4499` у фаворитах), ставить `protontricks`,
якщо його немає (у Debian він у `contrib`: скрипт вмикає цей компонент для офіційних репозиторіїв, бекапи
лишаються; інакше python venv + winetricks з GitHub або Flatpak), ставить .NET Framework 4.8 + VC++ у префікс
гри, створює `play.sh`, `server/start.sh`, `bot.sh`, `update.sh` і ярлик у меню, і зберігає копію себе та ваші
опції в `~/GTANetwork`. `--build` збирає лаунчер/сервер/бота з git-чекауту замість скачування
(клієнтський пакет усе одно з релізу, бо ScriptHookVDotNet потребує MSVC), `--release <tag>` фіксує версію,
`--game-path` допомагає, якщо Steam не знайдено автоматично, `--method steam`, якщо хочете через параметри запуску Steam.

Налаштування клієнта лежать у `~/GTANetwork/settings.xml`: `MasterServerAddress` за замовчуванням порожній (оригінального
master-сервера немає; фаворити, недавні сервери і LAN працюють без нього), `EnableMpVehiclesGlobal` вимкнений, бо індекс
скриптового глобала там із білдів 2016 року.

Далі: `~/GTANetwork/server/start.sh` в одному терміналі, `~/GTANetwork/play.sh` в іншому, і в меню гри у Favorites
вибрати `127.0.0.1:4499`. `~/GTANetwork/bot.sh` заходить на сервер без гри.

**Оновлення.** `play.sh`, `server/start.sh` і `bot.sh` спершу запускають `update.sh --quiet`: він питає GitHub про
найновіший реліз і ставить його, якщо той відрізняється від встановленого (`~/GTANetwork/release.txt`); сам скрипт
теж оновлюється з релізу. Ваш `settings.xml`, ScriptHookV і `server/settings.xml` зберігаються, а поки з цієї папки
працює сервер, бот чи лаунчер, нічого не чіпається. `update.sh --check` лише повідомляє, `update.sh --auto-update off`
вимикає автоматичну перевірку (`GTAN_NO_UPDATE=1` пропускає її один раз), `update.sh --release <tag>` фіксує
версію, `update.sh --shv <zip>` ставить новий ScriptHookV.

### Вручну

1. Встановіть гру і запустіть її один раз через Steam, щоб Proton створив префікс.
2. Поставте .NET Framework у цей префікс (потрібен ScriptHookVDotNet):
   `protontricks 271590 dotnet48` (або `WINEPREFIX=~/.steam/steam/steamapps/compatdata/271590/pfx winetricks dotnet48`).
   Інсталятор .NET 4.0 падає з `Failed to extract cabinet: netfx_core.mzz` на дуже нових збірках wine (Proton
   Experimental): поставте у Steam Proton 8.0 і запустіть `PROTON_VERSION="Proton 8.0" protontricks 271590 dotnet48`;
   сама гра може лишатися на будь-якому Proton. `setup-linux.sh` робить це сам, а якщо стабільного Proton нема,
   скачує GE-Proton8-32 у `compatibilitytools.d` Steam. Після цього `protontricks 271590 win10`: winetricks лишає
   префікс на Windows 7, а Rockstar Launcher хоче Windows 10 (скрипт це теж робить сам).
   Оригінальний клієнт також вимагав Visual C++ 2013/2015 (`vcrun2013`, `vcrun2015`).
3. Розпакуйте клієнтський пакет (`gtanetwork-client-win64-*.zip` з артефактів Actions / релізу), наприклад у
   `~/GTANetwork`, і покладіть туди Linux-лаунчер (`gtanetwork-launcher-linux-x64-*`).
4. Завантажте ScriptHookV з <http://www.dev-c.com/gtav/scripthookv/> і скопіюйте `ScriptHookV.dll` та
   `dinput8.dll` у `~/GTANetwork/bin/`.
5. У Steam задайте параметри запуску GTA V: `WINEDLLOVERRIDES="dinput8=n,b" %command%` (щоб Wine брав
   `dinput8.dll` від ScriptHookV, а не свій вбудований).
6. `~/GTANetwork/GTANetwork.Launcher doctor` покаже, що знайдено (Steam, бібліотека, папка гри, префікс,
   Proton) і чого бракує. Виправте попередження.
7. `~/GTANetwork/GTANetwork.Launcher` розгортає мод у папку гри, запускає гру через Steam, чекає завершення
   `GTA5.exe` і відновлює папку (інші `*.asi` на час сесії переносяться в `Disabled/`). `--method proton`
   запускає Proton напряму; `--no-wait`, `--keep-asi`, `--game-path`, `--prefix`, `--proton`, `--save` описані
   у `--help`.

Усе, чого торкається лаунчер, записується у `gtanetwork-deploy.json` у папці гри і відкочується командою
`GTANetwork.Launcher restore` (а також автоматично при наступному запуску після падіння).

## Гра на Windows

* Класично: `GTANSetup-<версія>.exe` (NSIS-інсталятор) або розпакувати zip і запустити `GTANLauncher.exe`
  від адміністратора.
* Новий варіант: `GTANetwork.Launcher.exe` з тієї ж папки (ASI-лоадер; `--method direct` запускає `PlayGTAV.exe`).

Обом потрібні `ScriptHookV.dll` + `dinput8.dll` у `bin\` і .NET Framework 4.8.

## CI

`.github/workflows/build.yml` запускається на кожен push, pull request і тег:

| Джоб | Раннер | Робить |
| --- | --- | --- |
| **linux** | ubuntu-latest | `dotnet build GTANetwork.sln` (перевірка компіляції клієнта проти заглушки), publish сервера (linux-x64, win-x64), лаунчера і бота (linux-x64, win-x64, один файл) і Map2Resource, smoke-тест сервера та інтеграційний тест із ботом, артефакти. |
| **windows** | windows-2022 | Встановлює ScriptHookV SDK (офіційний зі змінної репозиторію `SHV_SDK_URL`, якщо задана, інакше сумісні декларації з `Shv.NET/sdk-compat`), збирає `ScriptHookVDotNet.dll` через MSVC (v143, .NET Framework 4.8), збирає solution проти нього, пакує клієнт (`eng/package-client.ps1`), збирає NSIS-інсталятор, артефакти. |
| **release** | теги `v*` | Прикріплює всі артефакти до GitHub-релізу. |

## Відомі обмеження

* **Розходження версій гри.** ScriptHookV має відповідати встановленому білду GTA V. Патерни пам'яті в
  `Shv.NET/source/core` містять класичні сигнатури (2019–2020) плюс запасні варіанти для білдів від 1.0.3788
  (пул сутностей, пул камер, хук тексту гри). `ScriptHookVDotNet-*.log` перелічує кожен патерн, що не
  збігся, і який запасний варіант спрацював; на 1.0.3889 досі відсутні функції euphoria (GTA Network їх не
  використовує) і патч "force offline". GTA V Enhanced не підтримується.
* **Тестування в грі — ручне** — у CI немає GTA V. Збірка, сервер, лаунчер і бот покриті тестами; ігровий
  клієнт перевірено вручну на GTA V Legacy 1.0.3889 під Proton (підключення, синхронізація, чат, транспорт,
  клієнтські скрипти).
* **ScriptHookV не можна розповсюджувати** — кожен користувач завантажує його сам.
* **Мастер-сервера немає**: без публічного списку серверів і оновлень через мастер (Linux-інсталятор
  оновлюється сам з GitHub Releases), `announce` вимкнено.
* **CEF 85 / CefGlue** і мікс SharpDX 2.6/4.0 залишені як бінарники, під які налаштований DirectX-хук.

## Ліцензія

Код GTA Network — MIT (`LICENSE`). Сторонні компоненти зберігають свої ліцензії: ScriptHookVDotNet (zlib),
NativeUI (GPL-3.0), MinHook (BSD-2), Lidgren (MIT), бінарники в `libs/`. ScriptHookV — пропрієтарний і не є
частиною репозиторію.
