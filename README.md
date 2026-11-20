# HeadphoneGuard

**Заглушает именно тот звук, который раздражает — и только его.**

Когда беспроводная гарнитура садится, Windows (или сервис производителя, или сам донгл)
проигрывает голосовое оповещение «устройство разряжается». Оно идёт через тот же
аудио-выход, что и игра или музыка, поэтому обычное «выключить звук» бьёт по игре.

HeadphoneGuard:

1. **Читает заряд** беспроводных устройств из трёх независимых источников.
2. **Определяет, кто именно издал звук** — по аудиосессиям Windows (процесс + ресурс DLL).
3. **Глушит только этот источник** — игровой звук не страдает.
4. **Показывает своё уведомление** — один раз за цикл разряда, и снимает глушение,
   когда заряд восстановился.

Никаких прав администратора, ничего не перехватывает, экран не трогает.

> 🇬🇧 [English summary](#english-summary) — в конце файла.

---

## Как это работает

```
  ┌─────────────────────────────┐      ┌──────────────────────────────┐
  │ Источники заряда:           │      │ Порог (по умолчанию 10%)     │
  │ • PnP-реестр (BTHENUM)      │─────▶│ тихий режим на 30 минут      │
  │ • vendor-HID (MCHOSE и др.) │      └──────────────┬───────────────┘
  │ • GATT Battery Service      │                     │
  └─────────────────────────────┘                     ▼
                                        ┌──────────────────────────────┐
  ┌─────────────────────────────┐      │ 1) своё уведомление Windows  │
  │ Все render-выходы, все     │─────▶│ 2) mute конкретной сессии   │
  │ аудиосессии + пиковые счётчики│      └──────────────────────────────┘
  └─────────────────────────────┘
```

### 1. Где берётся заряд

| Источник | Что покрывает | Как читается |
|---|---|---|
| **Реестр Windows** (`SYSTEM\CurrentControlSet\Enum\BTHENUM`) | классические Bluetooth-гарнитуры (BR/EDR) — именно то, что видно в «Параметры → Bluetooth» | `DEVPKEY_Bluetooth_BatteryLevel` |
| **vendor-HID** | гарнитуры и мыши с собственным протоколом: MCHOSE, Logitech, Razer, HyperX, Corsair… | кадр `55 65 <значение> 02` на вендорной usage page |
| **GATT Battery Service** | Low Energy-устройства | `0x180F` / `0x2A19` через WinRT |

Windows показывает заряд далеко не для всех устройств. Например, у MCHOSE V9 PRO
(донгл `VID_291D&PID_385D`) Windows **не знает** про заряд — зато донгл сам присылает
кадр по usage page `0xFF90`:

```
55 65 0A 02 00 00 ...     →  значение 0x0A = 10 %, поле 0x02 = заряд
```

HeadphoneGuard читает такие кадры напрямую через HID. Проверено на живом устройстве:
`MCHOSE V9 PRO — 10 %`.

### 2. Как отличить оповещение от игры

Звук считается **оповещением (one-shot)**, если выполняется хотя бы одно условие:

- аудиосессия прожила недолго целиком (это не игровая сессия);
- процесс молчал дольше `quietGapSeconds` (30 с) — то есть звук пришёл «откуда-то»;
- сессия активировалась после простоя (типичный признак пришедшего уведомления).

И при этом процесс **не является доминирующим** на этом выходе — то есть непрерывно
играющим (игра, музыка, видео). Так игровой звук не трогается, а одиночный сигнал
производителя гасится.

### 3. Как глушится

Глушится **аудиосессия**, а не выход: `ISimpleAudioVolume::SetMute` на конкретной
сессии. Игра живёт в другой сессии и остаётся слышной.

---

## Что именно он умеет находить

| Источник | Как определяется |
|---|---|
| Голос производителя гарнитуры | процесс-владелец сессии (например, `MCHOSE HUB`) |
| Системный звук Windows | `Системные звуки Windows (@%SystemRoot%\System32\AudioSrv.Dll,-202)` — ресурс указывается прямо в имени сессии |
| Любое другое короткое приложение | имя процесса + длительность всплеска + пиковый уровень |

Честное ограничение: **Windows не отдаёт путь к самому `.wav`-файлу** через публичный
API. Источник определяется на уровне приложения/ресурса DLL — этого достаточно, чтобы
заглушить именно его, не трогая остальной звук.

## Возможности

- Тихий режим: уведомление один раз за цикл разряда, повторные оповещения глушатся.
- Глушение строго по источнику: игровой звук продолжает играть.
- Автоснятие глушения, когда заряд восстановился (+10 % сверх порога).
- Список «нарушителей»: процессы, которые глушатся всегда — в том числе по клику
  в трее по последнему зафиксированному источнику.
- Список «не трогать»: клиент облачной игры, Discord и прочее.
- Трей-меню: статус, список устройств с зарядом, последние источники звука, порог,
  автозапуск, журнал.
- Диагностические режимы `--diagnose`, `--hid`, `--sessions`.
- Журнал: `%APPDATA%\HeadphoneGuard\headphoneguard.log`.

## Установка

Требуется Windows 10 2004+ / Windows 11 и .NET 8 Desktop Runtime.

```powershell
git clone https://github.com/Stintik-123/HeadphoneGuard.git
cd HeadphoneGuard
dotnet publish src/HeadphoneGuard/HeadphoneGuard.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Запустите `HeadphoneGuard.exe` — появится иконка в трее. Правый клик по иконке:
«Запускать вместе с Windows», порог заряда, список последних источников звука.

Ничего не устанавливается в систему: настройки лежат в
`%APPDATA%\HeadphoneGuard\settings.json`, AppUserModelID — в ветке реестра текущего
пользователя.

## Использование из командной строки

```text
HeadphoneGuard                запуск в фоне (иконка в трее)
HeadphoneGuard --diagnose     заряды, выходы и живые аудиосессии
HeadphoneGuard --diagnose --seconds 10 --beep
                              10 секунд следить за звуками, в начале проиграть
                              системный звук — проверка детектора
HeadphoneGuard --hid          HID-коллекции, вендорные кадры и попытки опроса заряда
HeadphoneGuard --sessions     состояния сессий и пиковые уровни (отладка)
HeadphoneGuard --verbose      подробный лог
```

Пример вывода `--diagnose`:

```text
=== Заряд беспроводных устройств (универсальный источник) ===
  MCHOSE V9 PRO — 10% (источник: HID)

=== Аудио-выходы (render) ===
  Динамики (MCHOSE V9 PRO)
      id: {0.0.0.00000000}.{72c363a0-e701-4132-b939-c2299f9ff7fb}
```

## Настройки

`%APPDATA%\HeadphoneGuard\settings.json`:

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `batteryThreshold` | `10` | Процент, ниже которого считается «скоро разрядится» |
| `batteryPollSeconds` | `30` | Как часто опрашивать заряд |
| `quietModeMinutes` | `30` | Сколько после падения заряда действует тихий режим |
| `shortSoundMaxMs` | `4000` | Всё длиннее — считается игрой/музыкой и не глушится |
| `quietGapSeconds` | `30` | Пауза без звуков от процесса, после которой его звук считается оповещением |
| `lookbackSeconds` | `180` | Как далеко назад искать оповещение, прозвучавшее при падении заряда |
| `protectedProcesses` | `[]` | Процессы, которые никогда не заглушать |
| `mutedProcesses` | `[]` | Процессы, которые глушатся всегда |
| `endpointOverrides` | `{}` | Ручная привязка устройства к аудио-выходу |
| `showNotification` | `true` | Показывать тост Windows |

## Поддерживаемые устройства

| Семейство | Как читается заряд | Статус |
|---|---|---|
| **MCHOSE V9 PRO** (донгл `VID_291D&PID_385D`) | vendor-HID, usage page `0xFF90`, кадр `55 65 <заряд> 02` | ✅ проверено на живом устройстве |
| MCHOSE X9 / V9 Pro 2 | тот же вендорный протокол (`0xFF90`, `0xFF07:0222`) | ✅ по открытым протоколам |
| Bluetooth-гарнитуры (BR/EDR) | PnP-реестр, `DEVPKEY_Bluetooth_BatteryLevel` | ✅ |
| Bluetooth LE (наушники, мыши) | GATT Battery Service `0x180F` | ✅ |
| Logitech / Razer / HyperX / Corsair и другие 2.4 ГГц | vendor-HID (формат `55 65 …`) | ⚠️ протоколы разные, читается не у всех |

## Ограничения

- Устройство должно **само отдавать заряд**. Если нет — порог не сработает, но процесс
  можно добавить в `mutedProcesses`, и он будет глушиться всегда.
- Если оповещение проигрывает сам Windows, источником будет «Системные звуки Windows» —
  их глушение отключает все системные звуки до восстановления заряда.
- Имя устройства сопоставляется с аудио-выходом по совпадению значимых токенов;
  при неоднозначности задайте привязку вручную в `endpointOverrides`.
- Донглы с vendor-HID отвечают на запрос кадра не сразу: значение заряда обновляется
  событийно, поэтому в трее может на секунды отставать.

## Архитектура

```text
src/HeadphoneGuard/
├── Bluetooth/
│   ├── RegistryBatteryReader.cs  заряд BT-гарнитур из реестра (BTHENUM)
│   ├── BluetoothBatteryReader.cs  fallback: GATT Battery Service через WinRT
│   ├── DeviceNames.cs             VID/PID -> человеческое имя устройства
│   └── DeviceTree.cs              свойства дерева устройств (SetupAPI)
├── Hid/
│   ├── HidInspector.cs            перечисление HID-коллекций, обмен по отчётам
│   ├── HidListener.cs             фоновое чтение input-отчётов
│   ├── HidBatteryMonitor.cs       заряд из вендорных HID-протоколов
│   └── MchoseBatteryProtocol.cs   форматы кадров заряда
├── Audio/
│   ├── AudioCatalog.cs            список render-выходов и их свойства
│   ├── SoundSourceTracker.cs      слежение за сессиями, «всплески», mute/unmute
│   └── EndpointMatcher.cs         сопоставление устройства и выхода
├── Core/
│   ├── GuardEngine.cs             сценарий: заряд → звук → mute → уведомление
│   ├── BatteryService.cs          единый источник заряда
│   ├── ToastService.cs            тосты Windows + регистрация AUMID
│   ├── Settings.cs                настройки в JSON
│   └── Autostart.cs               автозапуск (HKCU\...\Run)
├── App/TrayApp.cs                 иконка в трее и меню
└── Program.cs                     CLI: --diagnose / --hid / --sessions
```

Сборка иконок: `tools/make-icon.ps1` (классический BMP-ICO, без внешних зависимостей).

## Благодарности

Проект опирается на проверенные open-source подходы, а не изобретает их заново:

- [windows_sound_tracker](https://github.com/IronAdamant/windows_sound_tracker) — определение
  источника звука через аудиосессии и пиковые счётчики.
- [Wireless-Device-Battery-Monitor](https://github.com/krisgfx9/wireless-device-battery-monitor)
  и [taskbar-battery](https://github.com/renanleonel/windows-taskbar-battery) — чтение заряда
  Bluetooth и устройств с vendor-HID.
- [HeadsetControl PR #563](https://github.com/Sapd/HeadsetControl/pull/563) — протокол MCHOSE
  X9 (`usage page 0xFF90`, кадр `55 65 <значение> <поле>`).
- [BluetoothBatteryMonitor](https://github.com/Kaandonmez/BluetoothBatteryMonitor) — обзор
  протоколов заряда (BLE GATT, HFP/PnP, HID).
- [audio-auto-mute-Python](https://github.com/farmanullah1/audio-auto-mute-Python) — идея
  автоматического приглушения при разряде наушников.

## Лицензия

MIT — см. [LICENSE](LICENSE).

---

## English summary

HeadphoneGuard silences the annoying "your device is low on battery" voice that Windows, a
vendor hub app or the dongle itself plays when your wireless headphones run out of charge —
without touching the game's audio.

- Reads battery level from three independent sources: the Windows registry (BTHENUM /
  `DEVPKEY_Bluetooth_BatteryLevel`), vendor HID protocols (MCHOSE, Logitech, Razer…) and
  Bluetooth LE GATT Battery Service (`0x180F`). Verified live on a MCHOSE V9 PRO dongle.
- Tracks every audio session of every output device (IAudioSessionManager2 + peak meters) to
  identify **which application** produced the sound, then mutes only that session.
- One Windows toast per discharge cycle; audio is restored when the battery recovers.
- No admin rights, no screen capture, no global mute.

Limitations: Windows does not expose the actual `.wav` path, so the source is identified at the
application/DLL-resource level. Devices that never report their battery cannot trigger the
threshold automatically.
