# Contributing to HeadphoneGuard

Спасибо, что хотите помочь. Проект маленький и дружелюбный — правок всегда много.

## Как запустить

```powershell
dotnet run --project src/HeadphoneGuard -- --diagnose --verbose
```

## Правила

1. **Не ломайте чужие источники звука.** Любое изменение должно глушить конкретную
   аудиосессию, а не весь выход. Если кажется, что без общего mute не обойтись — сначала
   обсудите в issue.
2. **Не требуйте прав администратора.** Приложение работает в профиле пользователя.
3. **Никаких телеметрии и сети.** Только локальные API Windows.
4. Комментарии и сообщения коммитов — на русском или английском, как вам удобнее.

## Полезные куски

| Задача | Где смотреть |
|---|---|
| Чтение заряда | `Bluetooth/DeviceTree.cs` (PnP), `Bluetooth/BluetoothBatteryReader.cs` (GATT) |
| Определение источника звука | `Audio/SoundSourceTracker.cs` |
| Сопоставление устройства и выхода | `Audio/EndpointMatcher.cs` |
| Сценарий | `Core/GuardEngine.cs` |
| Иконка | `tools/make-icon.ps1` |

## Проверка перед PR

```powershell
dotnet build src/HeadphoneGuard/HeadphoneGuard.csproj -c Release
HeadphoneGuard.exe --diagnose --seconds 10 --beep   # детектор должен увидеть звук
```

Приложите вывод `--diagnose` к PR — он помогает быстро понять, что именно сломалось.
