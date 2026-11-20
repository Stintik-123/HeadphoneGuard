using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HeadphoneGuard.Audio;
using HeadphoneGuard.Core;
using Hardcodet.Wpf.TaskbarNotification;

namespace HeadphoneGuard.App;

/// <summary>Иконка в трее и меню управления.</summary>
public sealed class TrayApp
{
    private readonly Settings _settings;
    private readonly GuardEngine _engine;
    private TaskbarIcon? _tray;

    public TrayApp(Settings settings, GuardEngine engine)
    {
        _settings = settings;
        _engine = engine;
    }

    public void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        _tray = new TaskbarIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "HeadphoneGuard — следит за зарядом наушников",
        };

        _tray.ContextMenu = BuildMenu();
        _engine.StatusChanged += text =>
        {
            _tray.Dispatcher.Invoke(() =>
            {
                SetStatus(text);
                _tray.ToolTipText = $"HeadphoneGuard — {text}";
                _tray.ContextMenu = BuildMenu();
            });
        };

        _tray.TrayMouseDoubleClick += (_, _) => _ = _engine.PollAsync();

        app.Run();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(path))
        {
            try
            {
                return new System.Drawing.Icon(path);
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Не удалось загрузить иконку: {ex.Message}");
            }
        }

        return System.Drawing.SystemIcons.Application;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var header = new MenuItem { Header = "HeadphoneGuard", IsEnabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        var status = new MenuItem { Header = "Статус: " + (_lastStatus ?? "ожидание…"), IsEnabled = false };
        menu.Items.Add(status);

        var quiet = new MenuItem
        {
            Header = _engine.QuietModeActive ? "Тихий режим: включён" : "Тихий режим: выключен",
            IsEnabled = false,
        };
        menu.Items.Add(quiet);

        var devices = new MenuItem { Header = "Устройства" };
        if (_engine.Devices.Count == 0)
            devices.Items.Add(new MenuItem { Header = "заряд пока неизвестен", IsEnabled = false });
        foreach (var (name, percent) in _engine.Devices.OrderBy(d => d.Value))
        {
            devices.Items.Add(new MenuItem
            {
                Header = $"{name}: {percent}%",
                IsEnabled = false,
            });
        }
        menu.Items.Add(devices);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Проверить заряд сейчас", Command = new RelayCommand(async () => await _engine.PollAsync()) });

        var sounds = new MenuItem { Header = "Последние источники звука" };
        foreach (var evt in _engine.Tracker.RecentEvents.Take(8))
        {
            var item = new MenuItem
            {
                Header = $"{evt.ProcessName} — {evt.Duration.TotalMilliseconds:F0} мс" + (evt.IsOneShot ? "  (оповещение)" : string.Empty),
            };

            if (evt.IsOneShot && !_settings.MutedProcesses.Contains(evt.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                item.Click += (_, _) =>
                {
                    _engine.AlwaysMute(evt.ProcessName);
                    if (_tray is not null)
                        _tray.ContextMenu = BuildMenu();
                };
            }
            else
            {
                item.IsEnabled = false;
            }

            sounds.Items.Add(item);
        }
        if (_engine.Tracker.RecentEvents.Count == 0)
            sounds.Items.Add(new MenuItem { Header = "пока ничего не зафиксировано", IsEnabled = false });
        menu.Items.Add(sounds);

        menu.Items.Add(new MenuItem { Header = "Снять всё глушение", Command = new RelayCommand(() => _engine.UnmuteAll()) });

        var threshold = new MenuItem { Header = $"Порог заряда: {_settings.BatteryThreshold}%" };
        foreach (int value in new[] { 5, 10, 15, 20, 30 })
        {
            int captured = value;
            threshold.Items.Add(new MenuItem
            {
                Header = $"{captured}%",
                IsCheckable = true,
                IsChecked = _settings.BatteryThreshold == captured,
                Command = new RelayCommand(() =>
                {
                    _settings.BatteryThreshold = captured;
                    _settings.Save();
                    if (_tray is not null)
                        _tray.ContextMenu = BuildMenu();
                }),
            });
        }
        menu.Items.Add(threshold);

        var notify = new MenuItem { Header = "Показывать уведомления", IsCheckable = true, IsChecked = _settings.ShowNotification };
        notify.Click += (_, _) =>
        {
            _settings.ShowNotification = notify.IsChecked;
            _settings.Save();
        };
        menu.Items.Add(notify);

        var autostart = new MenuItem { Header = "Запускать вместе с Windows", IsCheckable = true, IsChecked = Core.Autostart.IsEnabled };
        autostart.Click += (_, _) =>
        {
            Core.Autostart.SetEnabled(autostart.IsChecked);
            _settings.StartWithWindows = autostart.IsChecked;
            _settings.Save();
        };
        menu.Items.Add(autostart);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Открыть настройки", Command = new RelayCommand(() =>
        {
            _settings.Save();
            try
            {
                Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(_settings.FilePath)}\"");
            }
            catch
            {
                // ignore
            }
        }) });

        menu.Items.Add(new MenuItem { Header = "Выход", Command = new RelayCommand(() => Application.Current.Shutdown()) });

        return menu;
    }

    private string? _lastStatus;

    public void SetStatus(string text)
    {
        _lastStatus = text;
    }
}

/// <summary>Минимальная реализация ICommand для пунктов меню.</summary>
public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute) => _execute = execute;

    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter);
}
