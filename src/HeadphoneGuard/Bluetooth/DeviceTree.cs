using System.Runtime.InteropServices;

namespace HeadphoneGuard.Bluetooth;

/// <summary>
/// Чтение свойств дерева устройств Windows (cfgmgr32).
///
/// Именно так Windows хранит заряд классических Bluetooth-гарнитур:
/// DEVPKEY_Bluetooth_BatteryLevel публикуется на узле BTHENUM-сервиса
/// (Hands-Free), а не на самом устройстве. Поэтому здесь обходятся все
/// присутствующие узлы дерева — так же, как это делает fastfetch.
/// </summary>
public static class DeviceTree
{
    private const uint CmGetIdListPresent = 0x00000001;
    private const uint CmLocateDevNodeNormal = 0x00000000;
    private const uint CmGetPropertyFlagNone = 0x00000000;

    private const uint DevpropTypeByte = 0x00000002;
    private const uint DevpropTypeUInt32 = 0x0000000D;
    private const uint DevpropTypeString = 0x00000012;

    private static readonly Guid DevpkeyBluetoothBatteryLevel = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private static readonly Guid DevpkeyBluetoothDeviceAddress = new("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A");
    private static readonly Guid DevpkeyDeviceFriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly Guid DevpkeyDeviceContainerId = new("8C7ED206-3F8A-4827-B3AB-FB166A9AAFAA");
    private static readonly Guid DevpkeyBluetoothConnected = new("83DA6326-97A6-4088-9453-A1923F573B29");

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_U(ushort filter, [Out] char[] buffer, uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_Size(out uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode_W(ref uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Property_W(
        uint devInst,
        ref PROPERTYKEY key,
        out uint propType,
        [Out] byte[] buffer,
        ref uint bufferLen,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Property_Size(
        ref uint devInst,
        ref PROPERTYKEY key,
        out uint propType,
        out uint bufferLen,
        uint flags);

    public sealed record NodeInfo(
        string DeviceId,
        string FriendlyName,
        string Address,
        string ContainerId,
        int? BatteryPercent,
        bool? Connected);

    /// <summary>Все узлы дерева, у которых есть Bluetooth-заряд.</summary>
    public static IReadOnlyList<NodeInfo> ReadBluetoothBatteryNodes()
    {
        var result = new List<NodeInfo>();

        foreach (string deviceId in EnumerateDeviceIds())
        {
            uint devInst = 0;
            if (CM_Locate_DevNode_W(ref devInst, deviceId, CmLocateDevNodeNormal) != 0)
                continue;

            int? battery = ReadByte(devInst, DevpkeyBluetoothBatteryLevel, 2);
            if (battery is null)
                continue;

            string address = ReadString(devInst, DevpkeyBluetoothDeviceAddress, 1) ?? string.Empty;
            string friendly = ReadString(devInst, DevpkeyDeviceFriendlyName, 14) ?? deviceId;
            string container = ReadString(devInst, DevpkeyDeviceContainerId, 2) ?? string.Empty;
            bool? connected = ReadUInt32(devInst, DevpkeyBluetoothConnected, 15);

            result.Add(new NodeInfo(deviceId, friendly, address, container, battery, connected));
        }

        return result;
    }

    /// <summary>Список ID всех присутствующих устройств в дереве.</summary>
    public static IReadOnlyList<string> EnumerateDeviceIds()
    {
        var ids = new List<string>();

        if (CM_Get_Device_ID_Size(out uint len, CmGetIdListPresent) != 0)
            return ids;

        var buffer = new char[len + 2];
        if (CM_Get_Device_ID_List_U(0, buffer, len + 2, CmGetIdListPresent) != 0)
            return ids;

        string joined = new string(buffer);
        foreach (string id in joined.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);

        return ids;
    }

    private static int? ReadByte(uint devInst, Guid fmtid, uint pid)
    {
        var key = new PROPERTYKEY { FormatId = fmtid, PropertyId = pid };
        uint size = 0;
        if (CM_Get_DevNode_Property_Size(ref devInst, ref key, out uint type, out size, CmGetPropertyFlagNone) != 0)
            return null;
        if (type != DevpropTypeByte || size != 1)
            return null;

        var buffer = new byte[1];
        if (CM_Get_DevNode_Property_W(devInst, ref key, out _, buffer, ref size, CmGetPropertyFlagNone) != 0)
            return null;

        return buffer[0];
    }

    private static bool? ReadUInt32(uint devInst, Guid fmtid, uint pid)
    {
        var key = new PROPERTYKEY { FormatId = fmtid, PropertyId = pid };
        uint size = 0;
        if (CM_Get_DevNode_Property_Size(ref devInst, ref key, out uint type, out size, CmGetPropertyFlagNone) != 0)
            return null;
        if (type is not (DevpropTypeUInt32 or DevpropTypeByte) || size is not (4 or 1))
            return null;

        var buffer = new byte[4];
        if (CM_Get_DevNode_Property_W(devInst, ref key, out _, buffer, ref size, CmGetPropertyFlagNone) != 0)
            return null;

        return size switch
        {
            4 => BitConverter.ToUInt32(buffer, 0) != 0,
            _ => buffer[0] != 0,
        };
    }

    private static string? ReadString(uint devInst, Guid fmtid, uint pid)
    {
        var key = new PROPERTYKEY { FormatId = fmtid, PropertyId = pid };
        uint size = 0;
        if (CM_Get_DevNode_Property_Size(ref devInst, ref key, out uint type, out size, CmGetPropertyFlagNone) != 0)
            return null;
        if (type != DevpropTypeString || size == 0)
            return null;

        var buffer = new byte[size];
        if (CM_Get_DevNode_Property_W(devInst, ref key, out _, buffer, ref size, CmGetPropertyFlagNone) != 0)
            return null;

        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }
}
