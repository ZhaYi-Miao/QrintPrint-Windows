using System;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

class ProbeEndpoints
{
    const uint DIGCF_PRESENT = 0x00000002;
    const uint DIGCF_ALLCLASSES = 0x00000004;
    const uint SPDRP_HARDWAREID = 0x00000001;
    const uint SPDRP_DEVICEDESC = 0x00000000;
    const uint SPDRP_CLASS = 0x00000007;
    const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property, out uint PropertyRegDataType, IntPtr PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        IntPtr InterfaceClassGuid, uint MemberIndex, ref SP_DEV_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet,
        ref SP_DEV_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEV_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    static void Main(string[] args)
    {
        Console.WriteLine("=== Beeprt BY-288 USB Interface Probe ===\n");

        // 枚举所有 USB 设备
        IntPtr devInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devInfoSet == IntPtr.Zero || devInfoSet.ToInt64() == -1)
        {
            Console.WriteLine("SetupDiGetClassDevs failed");
            return;
        }

        uint index = 0;
        while (true)
        {
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfo))
                break;
            index++;

            string hwId = GetRegProperty(devInfoSet, devInfo, SPDRP_HARDWAREID);
            if (!hwId.Contains("VID_09C6", StringComparison.OrdinalIgnoreCase) ||
                !hwId.Contains("PID_0288", StringComparison.OrdinalIgnoreCase))
                continue;

            string desc = GetRegProperty(devInfoSet, devInfo, SPDRP_DEVICEDESC);
            string cls = GetRegProperty(devInfoSet, devInfo, SPDRP_CLASS);
            string friendly = GetRegProperty(devInfoSet, devInfo, SPDRP_FRIENDLYNAME);

            Console.WriteLine($"Device: {desc}");
            Console.WriteLine($"  FriendlyName: {friendly}");
            Console.WriteLine($"  HardwareID: {hwId}");
            Console.WriteLine($"  Class: {cls}");
            Console.WriteLine($"  ClassGuid: {devInfo.ClassGuid}");
            Console.WriteLine($"  DevInst: {devInfo.DevInst}");

            // 枚举该设备的所有接口
            Console.WriteLine("\n  Interfaces:");
            uint ifaceIdx = 0;
            while (true)
            {
                var ifaceData = new SP_DEV_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEV_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, IntPtr.Zero, ifaceIdx, ref ifaceData))
                    break;
                ifaceIdx++;

                SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize == 0) continue;

                var detailPtr = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailPtr, 4);
                    if (SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, detailPtr, reqSize, out _, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detailPtr, 4)) ?? "";
                        Console.WriteLine($"    Interface {ifaceIdx}:");
                        Console.WriteLine($"      Path: {path}");
                        Console.WriteLine($"      ClassGuid: {ifaceData.InterfaceClassGuid}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailPtr);
                }
            }

            Console.WriteLine();
        }
        SetupDiDestroyDeviceInfoList(devInfoSet);

        // 通过注册表获取 USB 描述符
        Console.WriteLine("=== Registry USB Info ===\n");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_09C6&PID_0288");
            if (key != null)
            {
                foreach (string subKeyName in key.GetSubKeyNames())
                {
                    Console.WriteLine($"  SubKey: {subKeyName}");
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey != null)
                    {
                        foreach (string valName in subKey.GetValueNames())
                            Console.WriteLine($"    {valName} = {subKey.GetValue(valName)}");
                    }
                }
            }
            else
            {
                Console.WriteLine("  Registry key not found");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  Registry error: {ex.Message}"); }

        // 检查所有 USB 接口类
        Console.WriteLine("\n=== All USB Interface Classes ===\n");
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_09C6&PID_0288%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                Console.WriteLine($"DeviceID: {mo["DeviceID"]}");
                Console.WriteLine($"Caption: {mo["Caption"]}");
                Console.WriteLine($"Service: {mo["Service"]}");
                Console.WriteLine($"Status: {mo["Status"]}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"WMI error: {ex.Message}"); }

        Console.WriteLine("\n=== Done ===");
    }

    static string GetRegProperty(IntPtr devInfoSet, SP_DEVINFO_DATA devInfo, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devInfo, property,
            out _, IntPtr.Zero, 0, out uint reqSize);
        if (reqSize == 0) return "";

        var buf = Marshal.AllocHGlobal((int)reqSize);
        try
        {
            if (SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devInfo, property,
                out _, buf, reqSize, out _))
            {
                return Marshal.PtrToStringUni(buf) ?? "";
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return "";
    }
}
