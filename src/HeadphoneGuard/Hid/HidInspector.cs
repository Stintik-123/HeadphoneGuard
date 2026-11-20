using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeadphoneGuard.Hid;

/// <summary>
/// Доступ к HID-устройствам Windows: перечисление коллекций и чтение feature-отчётов.
/// Нужен для гарнитур, которые сообщают заряд через vendor-протокол (MCHOSE, Logitech, Razer и т.п.).
/// </summary>
public static class HidInspector
{
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public ulong Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public uint[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    public sealed record HidCollection(
        string Path,
        string Description,
        ushort UsagePage,
        ushort Usage,
        ushort VendorId,
        ushort ProductId,
        ushort FeatureReportLength,
        int InputReportLength,
        int OutputReportLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte reportId, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte reportId, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte reportId, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte reportId, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle hidDeviceObject, [Out] byte[] buffer, int bufferLength);

    /// <summary>Все HID-коллекции, присутствующие в системе.</summary>
    public static IReadOnlyList<HidCollection> Enumerate()
    {
        var result = new List<HidCollection>();

        Guid interfaceGuid = HidInterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return result;

        try
        {
            uint index = 0;
            while (true)
            {
                var iface = new SpDeviceInterfaceData { cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref iface))
                    break;

                index++;

                uint required = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required == 0)
                    continue;

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize структуры SP_DEVICE_INTERFACE_DETAIL_DATA: 6 на x86, 8 на x64.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);

                    var iface2 = new SpDeviceInterfaceData { cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                    SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index - 1, ref iface2);

                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface2, detail, required, out _, IntPtr.Zero))
                        continue;

                    string path = ReadDevicePath(detail);
                    if (path.Length == 0)
                        continue;

                    var collection = Describe(path);
                    if (collection is not null)
                        result.Add(collection);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string ReadDevicePath(IntPtr detail)
    {
        // Путь может начинаться со смещения 4 или 8 — зависит от сборки структуры.
        foreach (int offset in new[] { 4, 8 })
        {
            string? candidate = Marshal.PtrToStringUni(detail + offset);
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.StartsWith("\\\\"))
                return candidate;
        }

        return string.Empty;
    }

    private static HidCollection? Describe(string path)
    {
        using var handle = Open(path, GenericRead);
        if (handle.IsInvalid)
            return null;

        var attributes = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes))
            return null;

        ushort usagePage = 0, usage = 0, featureLen = 0, inputLen = 0, outputLen = 0;

        if (HidD_GetPreparsedData(handle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) == 0x00110000)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    featureLen = caps.FeatureReportByteLength;
                    inputLen = caps.InputReportByteLength;
                    outputLen = caps.OutputReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        return new HidCollection(
            path,
            ReadString(handle, HidD_GetProductString),
            usagePage,
            usage,
            attributes.VendorID,
            attributes.ProductID,
            featureLen,
            inputLen,
            outputLen);
    }

    private static string ReadString(SafeFileHandle handle, Func<SafeFileHandle, byte[], int, bool> getter)
    {
        try
        {
            var buffer = new byte[512];
            if (!getter(handle, buffer, buffer.Length))
                return string.Empty;

            int len = Array.IndexOf(buffer, (byte)0);
            if (len < 0) len = buffer.Length;
            return System.Text.Encoding.Unicode.GetString(buffer, 0, len);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        return CreateFile(path, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
    }

    /// <summary>Читает feature-отчёт (ответ устройства) без предварительного запроса.</summary>
    public static byte[]? ReadFeature(string path, int length = 64)
    {
        using var handle = Open(path, GenericRead | GenericWrite);
        if (handle.IsInvalid)
            return null;

        var buffer = new byte[length];
        if (!HidD_GetFeature(handle, 0, buffer, buffer.Length))
            return null;

        return buffer;
    }

    /// <summary>Шлёт запрос и читает ответ: сначала SetFeature, затем GetFeature.</summary>
    public static byte[]? Query(string path, byte[] request, int length = 64, byte reportId = 0)
    {
        using var handle = Open(path, GenericRead | GenericWrite);
        if (handle.IsInvalid)
            return null;

        var outBuffer = new byte[length];
        Array.Copy(request, outBuffer, Math.Min(request.Length, length));

        if (!HidD_SetFeature(handle, reportId, outBuffer, outBuffer.Length))
            return null;

        var reply = new byte[length];
        if (!HidD_GetFeature(handle, reportId, reply, reply.Length))
            return null;

        return reply;
    }

    /// <summary>
    /// Vendor-обмен через output/input-отчёты: отправляем кадр и ждём ответный
    /// input-отчёт (так работает протокол MCHOSE на usage page 0xFF90).
    /// </summary>
    public static byte[]? QueryOutput(string path, byte[] payload, int length = 64, int attempts = 6)
    {
        using var handle = Open(path, GenericRead | GenericWrite);
        if (handle.IsInvalid)
            return null;

        var frame = new byte[length];
        Array.Copy(payload, frame, Math.Min(payload.Length, length));

        if (!HidD_SetOutputReport(handle, 0x00, frame, frame.Length))
            return null;

        var reply = new byte[length];
        for (int i = 0; i < attempts; i++)
        {
            Thread.Sleep(60);
            if (!HidD_GetInputReport(handle, 0x00, reply, reply.Length))
                continue;

            // Ответ может прийти с задержкой — ждём осмысленного кадра.
            if (reply[0] == 0x55 || reply[0] == 0xA1)
                return reply;
        }

        return reply;
    }

    /// <summary>
    /// Полный vendor-обмен: пишем output-кадр через WriteFile и читаем ответный
    /// input-отчёт с таймаутом. Так работают протоколы MCHOSE (usage page 0xFF90).
    /// </summary>
    public static byte[]? Exchange(string path, byte[] payload, int length = 64, int attempts = 8)
    {
        using var handle = Open(path, GenericRead | GenericWrite);
        if (handle.IsInvalid)
            return null;

        var frame = new byte[length];
        Array.Copy(payload, frame, Math.Min(payload.Length, length));

        uint written;
        if (!WriteFile(handle, frame, (uint)frame.Length, out written, IntPtr.Zero))
            return null;

        var reply = new byte[length];
        for (int i = 0; i < attempts; i++)
        {
            Thread.Sleep(80);
            if (!HidD_GetInputReport(handle, 0x00, reply, reply.Length))
                continue;

            if (reply[0] == 0x55 || reply[0] == 0xA1)
                return reply;
        }

        return reply[0] == 0 ? null : reply;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint bytesToRead, out uint bytesRead, ref Overlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, ref Overlapped overlapped, out uint bytesTransferred, bool wait);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Отправляет output-кадр в устройство (без чтения ответа).</summary>
    public static bool Send(string path, byte[] payload, int length = 64)
    {
        using var handle = Open(path, GenericRead | GenericWrite);
        if (handle.IsInvalid)
            return false;

        var frame = new byte[length];
        Array.Copy(payload, frame, Math.Min(payload.Length, length));

        uint written;
        return WriteFile(handle, frame, (uint)frame.Length, out written, IntPtr.Zero);
    }

    /// <summary>Диагностический обмен: пишет кадр и печатает результат вместе с кодами ошибок.</summary>
    public static string DebugExchange(string path, byte[] payload, int length = 64)
    {
        var log = new System.Text.StringBuilder();

        using var handle = Open(path, GenericRead | GenericWrite);
        if (handle.IsInvalid)
            return $"open failed, error={Marshal.GetLastWin32Error()}";

        var frame = new byte[length];
        Array.Copy(payload, frame, Math.Min(payload.Length, length));

        uint written;
        bool ok = WriteFile(handle, frame, (uint)frame.Length, out written, IntPtr.Zero);
        log.AppendLine($"  WriteFile ok={ok} written={written} error={Marshal.GetLastWin32Error()}");

        var reply = new byte[length];
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(100);
            bool got = HidD_GetInputReport(handle, 0x00, reply, reply.Length);
            log.AppendLine($"  GetInputReport ok={got} error={Marshal.GetLastWin32Error()} bytes={string.Join(' ', reply.Take(10).Select(b => b.ToString("X2")))}");
            if (got && reply[0] != 0)
                break;
        }

        for (byte reportId = 0; reportId < 3; reportId++)
        {
            var feature = new byte[length];
            bool got = HidD_GetFeature(handle, reportId, feature, feature.Length);
            log.AppendLine($"  GetFeature id={reportId} ok={got} error={Marshal.GetLastWin32Error()} bytes={string.Join(' ', feature.Take(10).Select(b => b.ToString("X2")))}");
        }

        return log.ToString();
    }

    /// <summary>Возвращает список коллекций конкретного производителя.</summary>
    public static IReadOnlyList<HidCollection> ForVendor(ushort vendorId) =>
        Enumerate().Where(c => c.VendorId == vendorId).ToArray();
}

