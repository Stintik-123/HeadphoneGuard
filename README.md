# HeadphoneGuard

**Заглушает именно тот звук, который раздражает — и только его.**

Когда Bluetooth-гарнитура садится, Windows (или сервис производителя) проигрывает голосовое
оповещение «устройство разряжается». Оно идёт через тот же аудио-выход, что и игра или музыка,
поэтому обычное «выключить звук» бьёт по игре.

HeadphoneGuard делает три вещи:

1. **Следит за зарядом** Bluetooth-устройств.
2. **Определяет, кто именно издал звук** — по аудиосессиям Windows (процесс + ресурс DLL),
   а не гасит весь выход целиком.
3. **Глушит только этот источник** и показывает своё уведомление — один раз за цикл разряда.
   Когда заряд восстановился, глушение снимается само.

Никаких прав администратора, ничего не перехватывает, экран не трогает.

> 🇬🇧 [English summary](#english-summary) — в конце файла.

---

## Как это работает

```
  ┌────────────────────┐        ┌──────────────────────────┐        ┌───────────────────┐
  │ Заряд BT-устройства│───────▶│ Порог (по умолчанию 10%) │───────▶│ Уведомление + mute│
  │  (PnP / GATT 0x180F)│        └──────────────────────────┘        └───────────────────┘
  └────────────────────┘                                                   │
                                                                              ▼
  ┌────────────────────┐        ┌──────────────────────────┐        ┌───────────────────┐
  │ Все render-выходы  │───────▶│ Пиковые счётчики сессий  │───────▶│ Mute конкретной   │
  │ (IAudioSessionMgr2)│        │ «всплески» ≤ 4 с        │        │ аудиосессии       │
  └────────────────────┘        └──────────────────────────┘        └───────────────────┘
```

1. **Заряд.** Для классических Bluetooth-гарнитур (BR/EDR) Windows хранит заряд в свойстве
   `DEVPKEY_Bluetooth_BatteryLevel` — ровно то значение, что показывает «Параметры → Bluetooth».
   Мы обходим дерево устройств (`cfgmgr32`) и читаем это свойство. Если гарнитура — Low Energy
   и PnP-заряда нет, используется GATT Battery Service (`0x180F` / `0x2A19`).
2. **Источник звука.** Фоновый поток перечисляет аудиосессии каждого выхода, снимает пиковый
   уровень и делит звук на «всплески». Короткий всплеск — это системный сигнал или реплика
   приложения; длинный — игра или музыка. Такой же подход использует open-source проект
   [windows_sound_tracker](https://github.com/IronAdamant/windows_sound_tracker).
3. **Глушение.** Когда заряд пересёк порог, программа смотрит, какой короткий звук прозвучал
   в этот момент, и ставит `Mute` именно этой сессии (`ISimpleAudioVolume`). Звук игры не
   трогается: он живёт в другой сессии и остаётся слышным.
4. **Уведомление.** Показывается свой тост Windows (AppUserModelID регистрируется в
   `HKCU\Software\Classes\AppUserModelId` — без прав администратора).

## Что именно он умеет находить

| Источник | Как определяется |
|---|---|
| Голосовое оповещение производителя гарнитуры | процесс-владелец сессии (например, `Soundcore`, `AnkerSound`) |
| Системный звук Windows | `Системные звуки Windows (@%SystemRoot%\System32\AudioSrv.Dll,-202)` — ресурс указывается прямо в имени сессии |
| Любое другое короткое приложение | имя процесса + длительность всплеска + пиковый уровень |

Честное ограничение: **Windows не отдаёт путь к самому `.wav`-файлу** через публичный API.
Источник определяется на уровне приложения/ресурса DLL — этого достаточно, чтобы заглушить
именно его, не трогая остальной звук.

## Возможности

- Тихий режим: уведомление один раз за цикл разряда, никакого повторного писка.
- Глушение строго по источнику: игровой звук продолжает играть.
- Автоснятие глушения, когда заряд восстановился.
- Ручной список «нарушителей»: процессы, которые глушатся всегда, когда бы они ни пискнули.
- Защищённые процессы: например, облачный клиент игры можно внести в «не трогать».
- Порог заряда настраивается из трея (5 / 10 / 15 / 20 / 30 %).
- Трей-иконка с меню: текущий статус, последние источники звука, снять глушение, автозапуск.
- Диагностический режим `--diagnose` — показывает заряды, выходы и живые аудиосессии.

## Установка

Требуется Windows 10 2004+ / Windows 11 и .NET 8 Desktop Runtime.

```powershell
git clone https://github.com/<ваш-логин>/HeadphoneGuard.git
cd HeadphoneGuard
dotnet publish src/HeadphoneGuard/HeadphoneGuard.csproj -c Release -r win-x64 --self-contained false
```

Запустите `HeadphoneGuard.exe` — появится иконка в трее. Правый клик по иконке:
«Запускать вместе с Windows», порог заряда, список последних источников звука.

Ничего не устанавливается в систему: настройки лежат в
`%APPDATA%\HeadphoneGuard\settings.json`, AppUserModelID — в ветке реестра текущего пользователя.

## Использование из командной строки

```text
HeadphoneGuard                запустить в фоне (иконка в трее)
HeadphoneGuard --diagnose     показать заряды, выходы и активные сессии
HeadphoneGuard --diagnose --seconds 10 --beep
                              10 секунд следить за звуками, в начале проиграть
                              системный звук — чтобы проверить детектор
HeadphoneGuard --verbose      подробный лог
```

Пример вывода `--diagnose`:

```text
=== Bluetooth-устройства с зарядом (PnP-дерево) ===
  WH-1000XM4 [00:1A:7D:DA:71:1B] — 8% (подключено: True)

=== Аудио-выходы (render) ===
  Наушники (WH-1000XM4 Stereo)
      id: {0.0.0.00000000}.{7a3c...}

=== Живые аудиосессии (кто сейчас играет) ===
  21:04:12 Системные звуки Windows (@%SystemRoot%\System32\AudioSrv.Dll,-202) — 1197 мс
```

## Настройки

`%APPDATA%\HeadphoneGuard\settings.json`:

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `batteryThreshold` | `10` | Процент, ниже которого считается «скоро разрядится» |
| `batteryPollSeconds` | `30` | Как часто опрашивать заряд |
| `shortSoundMaxMs` | `4000` | Всё длиннее — считается игрой/музыкой и не глушится |
| `protectedProcesses` | `[]` | Процессы, которые никогда не заглушать (например, клиент облачной игры) |
| `mutedProcesses` | `[]` | Процессы, которые глушатся всегда |
| `endpointOverrides` | `{}` | Ручная привязка Bluetooth-устройства к аудио-выходу |
| `showNotification` | `true` | Показывать тост Windows |

## Ограничения

- Гарнитура должна **сама отдавать заряд** в Windows. Некоторые дешёвые TWS этого не делают —
  тогда порог не сработает, но заглушить источник вручную (или добавить процесс в
  `mutedProcesses`) можно всегда.
- Заряд в PnP публикуется на узле `BTHENUM` сервиса (Hands-Free), а не на самом устройстве —
  поэтому обходятся все узлы дерева, а не только класс Bluetooth.
- Если оповещение проигрывает сам Windows, а не сервис производителя, источником будет
  «Системные звуки Windows» — их глушение отключает все системные звуки до восстановления заряда.
- Имя Bluetooth-устройства сопоставляется с аудио-выходом по совпадению значимых токенов;
  при неоднозначности задайте привязку вручную в `endpointOverrides`.

## Архитектура

```text
src/HeadphoneGuard/
├── Bluetooth/
│   ├── DeviceTree.cs             чтение PnP-свойств (cfgmgr32): заряд, адрес, имя
│   └── BluetoothBatteryReader.cs  fallback: GATT Battery Service через WinRT
├── Audio/
│   ├── AudioCatalog.cs            список render-выходов и их свойства
│   ├── SoundSourceTracker.cs     слежение за сессиями, «всплески», mute/unmute
│   └── EndpointMatcher.cs         сопоставление BT-устройства и выхода
├── Core/
│   ├── GuardEngine.cs             сценарий: заряд → звук → mute → уведомление
│   ├── ToastService.cs            тосты Windows + регистрация AUMID
│   ├── Settings.cs                настройки в JSON
│   └── Autostart.cs               автозапуск (HKCU\...\Run)
├── App/TrayApp.cs                 иконка в трее и меню
└── Program.cs                     CLI: --diagnose / --verbose
```

Сборка иконок: `tools/make-icon.ps1` (классический BMP-ICO, без внешних зависимостей).

## Благодарности

Проект опирается на проверенные open-source подходы, а не изобретает их заново:

- [windows_sound_tracker](https://github.com/IronAdamant/windows_sound_tracker) — определение
  источника звука через аудиосессии и пиковые счётчики.
- [taskbar-battery](https://github.com/renanleonel/windows-taskbar-battery) и
  [fastfetch](https://github.com/fastfetch-cli/fastfetch) — чтение заряда Bluetooth через
  `DEVPKEY_Bluetooth_BatteryLevel` в дереве устройств.
- [BluetoothBatteryMonitor](https://github.com/Kaandonmez/BluetoothBatteryMonitor) — обзор
  протоколов заряда (BLE GATT, HFP/PnP, HID).
- [audio-auto-mute-Python](https://github.com/farmanullah1/audio-auto-mute-Python) — идея
  автоматического приглушения выхода при разряде наушников.

## Лицензия

MIT — см. [LICENSE](LICENSE).

---

## English summary

HeadphoneGuard silences the annoying "your device is low on battery" voice that Windows or a
headset vendor plays when your Bluetooth headphones run out of charge — without touching the
game's audio.

- Reads the headset battery from the Windows device tree (`DEVPKEY_Bluetooth_BatteryLevel`),
  with a Bluetooth LE GATT Battery Service (`0x180F`) fallback.
- Tracks every audio session of every output device (IAudioSessionManager2 + peak meters) to
  identify **which application** produced the sound, then mutes only that session.
- Shows a single Windows toast per discharge cycle and restores audio when the battery recovers.
- No admin rights, no screen capture, no global mute.

Limitations: Windows does not expose the actual `.wav` path, so the source is identified at the
application/DLL-resource level. Headsets that never report their battery to Windows cannot
trigger the threshold automatically.
