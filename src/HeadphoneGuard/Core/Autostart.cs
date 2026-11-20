using Microsoft.Win32;

namespace HeadphoneGuard.Core;

/// <summary>Автозапуск вместе с Windows (запись в ветку реестра текущего пользователя).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HeadphoneGuard";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "HeadphoneGuard.exe";

            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
                return;

            if (enabled)
                key.SetValue(ValueName, $"\"{exe}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось изменить автозапуск: {ex.Message}");
        }
    }
}
