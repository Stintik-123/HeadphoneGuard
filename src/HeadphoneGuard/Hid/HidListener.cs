using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeadphoneGuard.Hid;

/// <summary>
/// Слушает HID-отчёты устройства: читает входящие input-отчёты в фоне.
/// Нужен для вендорных протоколов, где устройство само присылает телеметрию
/// (MCHOSE-гарнитуры шлют кадры с зарядом в usage page 0xFF90).
/// </summary>
public sealed class HidListener : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    private readonly string _path;
    private readonly int _length;
    private readonly CancellationTokenSource _cts = new();
    private SafeFileHandle? _handle;
    private Thread? _thread;

    public event Action<byte[]>? FrameReceived;

    public HidListener(string path, int length = 64)
    {
        _path = path;
        _length = length;
    }

    public void Start()
    {
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "HID listener " + _path
        };
        _thread.Start();
    }

    private void ReadLoop()
    {
        try
        {
            _handle = CreateFileW(
                _path,
                0x80000000,          // GENERIC_READ
                0x00000003,          // FILE_SHARE_READ | FILE_SHARE_WRITE
                IntPtr.Zero,
                3,                  // OPEN_EXISTING
                0,
                IntPtr.Zero);

            if (_handle.IsInvalid)
                return;

            var buffer = new byte[_length];

            while (!_cts.IsCancellationRequested)
            {
                uint read;
                bool ok = ReadFile(_handle, buffer, (uint)_length, out read, IntPtr.Zero);

                if (!ok || read == 0)
                {
                    if (Marshal.GetLastWin32Error() == 995) // ERROR_OPERATION_ABORTED
                        break;

                    Thread.Sleep(50);
                    continue;
                }

                var frame = new byte[read];
                Array.Copy(buffer, frame, (int)read);
                FrameReceived?.Invoke(frame);
            }
        }
        catch
        {
            // Устройство отвалилось — слушатель просто завершается.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    public void Dispose()
    {
        _cts.Cancel();
        _handle?.Dispose();
        _thread?.Interrupt();
    }
}
