using System.Runtime.InteropServices;
using System.Text;

namespace HeadphoneGuard.Bluetooth;

/// <summary>
/// Чтение свойств дерева устройств Windows через SetupAPI.
///
/// Именно так Windows хранит заряд классических Bluetooth-гарнитур:
/// DEVPKEY_Bluetooth_BatteryLevel публикуется на узле BTHENUM-сервиса
/// (Hands-Free), а не на самом устройстве — поэтому обходятся все узлы,
/// как это делает fastfetch.
/// </summary>
public static class DeviceTree
{
    private const uint DigcfAll = 0x00000001;   // DIGCF_ALL — все узлы дерева
    private const uint DigcfPresent = 0x00000002; // DIGCF_PRESENT
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpDeviceDesc = 0x00000002;
    private const uint DevpropTypeByte = 0x00000002;
    private const uint DevpropTypeUInt32 = 0x00000004;
    private const uint DevpropTypeString = 0x00000012;
    private const uint DevpropTypeAny = 0x0000001F;

    private static readonly Guid DevpkeyBatteryLevel = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private static readonly Guid DevpkeyBluetoothDeviceAddress = new("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A");
    private static readonly Guid DevpkeyDeviceFriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly Guid DevpkeyDeviceContainerId = new("8C7ED206-3F8A-4827-B3AB-FB166A9AAFAA");
    private static readonly Guid DevpkeyBluetoothConnected = new("83DA6326-97A6-4088-9453-A1923F573B29");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        IntPtr propertyBuffer, uint propertyBufferSize, out uint requiredSize, IntPtr propertyType);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref PROPERTYKEY propertyKey,
        uint propertyType, out uint propertyBufferSize, IntPtr propertyBuffer);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    public sealed record NodeInfo(
        string DeviceId,
        string FriendlyName,
        string Address,
        string ContainerId,
        int? BatteryPercent,
        bool? Connected);

    public sealed record NamedNode(string InstanceId, string FriendlyName);

    /// <summary>Все узлы дерева с их именами — для сопоставления по VID/PID.</summary>
    public static IReadOnlyList<NamedNode> EnumerateNamedNodes()
    {
        var result = new List<NamedNode>();

        foreach (var (info, instanceId) in EnumerateDevices())
        {
            string name = ReadFriendlyName(info) ?? ReadStringProperty(info, DevpkeyDeviceFriendlyName, 14) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(new NamedNode(instanceId, name));
        }

        return result;
    }

    /// <summary>Все узлы, у которых есть Bluetooth-заряд.</summary>
    public static IReadOnlyList<NodeInfo> ReadBluetoothBatteryNodes()
    {
        var result = new List<NodeInfo>();

        foreach (var (info, instanceId) in EnumerateDevices())
        {
            int? battery = ReadBatteryLevel(info);
            if (battery is null)
                continue;

            string name = ReadFriendlyName(info) ?? ReadStringProperty(info, DevpkeyDeviceFriendlyName, 14) ?? instanceId;
            string address = ReadStringProperty(info, DevpkeyBluetoothDeviceAddress, 1) ?? string.Empty;
            string container = ReadStringProperty(info, DevpkeyDeviceContainerId, 2) ?? string.Empty;
            bool? connected = ReadUInt32(info, DevpkeyBluetoothConnected, 15);

            result.Add(new NodeInfo(instanceId, name, address, container, battery, connected));
        }

        return result;
    }

    private static IEnumerable<(SP_DEVINFO_DATA Info, string InstanceId)> EnumerateDevices()
    {
        Guid empty = Guid.Empty;
        IntPtr set = SetupDiGetClassDevs(ref empty, IntPtr.Zero, IntPtr.Zero, DigcfAll);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            Diagnostics.Dev($"SetupDiGetClassDevs не удалась: {Marshal.GetLastWin32Error()} (handle={set})");
            yield break;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                    yield break;

                var sb = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref info, sb, (uint)sb.Capacity, out _))
                    continue;

                yield return (info, sb.ToString());
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? ReadFriendlyName(SP_DEVINFO_DATA info)
    {
        uint size = 0;
        SetupDiGetDeviceRegistryPropertyW(IntPtr.Zero, ref info, SpdrpFriendlyName, IntPtr.Zero, 0, out size, IntPtr.Zero);
        return ReadRegistryString(info, SpdrpFriendlyName, size);
    }

    private static string? ReadRegistryString(SP_DEVINFO_DATA info, uint property, uint size)
    {
        if (size == 0)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!SetupDiGetDeviceRegistryPropertyW(IntPtr.Zero, ref info, property, buffer, size, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? ReadBatteryLevel(SP_DEVINFO_DATA info)
    {
        var key = new PROPERTYKEY { FormatId = DevpkeyBatteryLevel, PropertyId = 2 };

        foreach (uint type in new[] { DevpropTypeByte, DevpropTypeUInt32, DevpropTypeAny })
        {
            if (!SetupDiGetDevicePropertyW(IntPtr.Zero, ref info, ref key, type, out uint size, IntPtr.Zero))
                continue;

            if (size is 0 or > 64)
                continue;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!SetupDiGetDevicePropertyW(IntPtr.Zero, ref info, ref key, type, out size, buffer))
                    continue;

                if (size == 1)
                {
                    int value = Marshal.ReadByte(buffer);
                    if (value is >= 0 and <= 100)
                        return value;
                }
                else if (size == 4)
                {
                    uint raw = (uint)Marshal.ReadInt32(buffer);
                    int value = (int)(raw & 0xFFFF);
                    if (value is >= 0 and <= 100)
                        return value;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static bool? ReadUInt32(SP_DEVINFO_DATA info, Guid formatId, uint pid)
    {
        var key = new PROPERTYKEY { FormatId = formatId, PropertyId = pid };

        foreach (uint type in new[] { DevpropTypeUInt32, DevpropTypeByte, DevpropTypeAny })
        {
            if (!SetupDiGetDevicePropertyW(IntPtr.Zero, ref info, ref key, type, out uint size, IntPtr.Zero))
                continue;

            if (size is 0 or > 16)
                continue;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!SetupDiGetDevicePropertyW(IntPtr.Zero, ref info, ref key, type, out size, buffer))
                    continue;

                if (size == 4)
                    return Marshal.ReadInt32(buffer) != 0;
                if (size == 1)
                    return Marshal.ReadByte(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static string? ReadStringProperty(SP_DEVINFO_DATA info, Guid formatId, uint pid)
    {
        var key = new PROPERTYKEY { FormatId = formatId, PropertyId = pid };

        foreach (uint type in new[] { DevpropTypeString, DevpropTypeAny })
        {
            if (!SetupDiGetDevicePropertyW(IntPtr.Zero, ref info, ref key, type, out uint size, IntPtr.Zero))
                continue;

            if (size == 0 || size > 4096)
                continue;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!SetupDiGetDevicePropertyW(IntPtr.Zero, ref info, ref key, type, out size, buffer))
                    continue;

                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }
}
