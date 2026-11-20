using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Win32;

namespace HeadphoneGuard.Core;

/// <summary>
/// Тост-уведомления Windows. Для unpackaged-приложения Windows требует
/// зарегистрированный AppUserModelID — регистрируем его в HKCU
/// (без прав администратора) и выставляем процессу тот же AUMID.
/// </summary>
public sealed class ToastService
{
    public const string AppId = "HeadphoneGuard.BatteryGuard";
    private const string DisplayName = "HeadphoneGuard";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    public bool Initialize()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "HeadphoneGuard.exe";

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            if (key is not null)
            {
                key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
                key.SetValue("IconUri", exe, RegistryValueKind.String);
            }

            SetCurrentProcessExplicitAppUserModelID(AppId);
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось зарегистрировать AUMID: {ex.Message}");
            return false;
        }
    }

    public void Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось показать уведомление: {ex.Message}");
        }
    }
}
