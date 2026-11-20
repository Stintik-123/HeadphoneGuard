using NAudio.CoreAudioApi;

namespace HeadphoneGuard.Audio;

/// <summary>Каталог аудио-выходов Windows (render endpoints).</summary>
public sealed class AudioCatalog
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<AudioEndpointInfo> ListActiveRenderEndpoints()
    {
        var result = new List<AudioEndpointInfo>();

        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new AudioEndpointInfo(
                device.ID,
                device.FriendlyName,
                device.DeviceFriendlyName,
                device.State == DeviceState.Active));

            device.Dispose();
        }

        return result;
    }

    /// <summary>Открывает выход по ID (для управления mute).</summary>
    public MMDevice? Open(string endpointId)
    {
        try
        {
            return _enumerator.GetDevice(endpointId);
        }
        catch (Exception ex)
        {
            Diagnostics.Dev($"Не удалось открыть аудио-выход {endpointId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Все строковые значения свойств выхода. Через них ищется Bluetooth-адрес
    /// устройства — так выход однозначно связывается с конкретной гарнитурой.
    /// </summary>
    public static IReadOnlyList<string> ReadPropertyStrings(MMDevice device)
    {
        var values = new List<string>();

        try
        {
            var store = device.Properties;
            for (int i = 0; i < store.Count; i++)
            {
                object? value = store[i].Value;
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    values.Add(s);
                else if (value is Guid g)
                    values.Add(g.ToString());
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Dev($"Не удалось прочитать свойства выхода: {ex.Message}");
        }

        return values;
    }
}
