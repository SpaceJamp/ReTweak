using System.Runtime.InteropServices;
using System.Text;

namespace ReTweak.Core.Interop;

/// <summary>
/// Minimal NVML bindings. nvml.dll ships with every NVIDIA driver (System32).
/// Only documented, read-only queries are used here.
/// </summary>
internal static partial class Nvml
{
    private const string Lib = "nvml.dll";

    public const int Success = 0;
    public const int NotSupported = 3;

    public enum ClockType : uint { Graphics = 0, Sm = 1, Memory = 2, Video = 3 }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;     // (device << 16) | vendor
        public uint PciSubSystemId;  // (subsystem << 16) | subvendor
        public fixed byte BusId[32];
    }

    [LibraryImport(Lib, EntryPoint = "nvmlInit_v2")]
    public static partial int Init();

    [LibraryImport(Lib, EntryPoint = "nvmlShutdown")]
    public static partial int Shutdown();

    [LibraryImport(Lib, EntryPoint = "nvmlSystemGetDriverVersion")]
    private static unsafe partial int SystemGetDriverVersion(byte* buf, uint len);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static partial int DeviceGetCount(out uint count);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static partial int DeviceGetHandleByIndex(uint index, out nint device);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetName")]
    private static unsafe partial int DeviceGetName(nint device, byte* buf, uint len);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetPciInfo_v3")]
    public static partial int DeviceGetPciInfo(nint device, out PciInfo pci);

    /// <summary>nvmlDeviceArchitecture_t values used here.</summary>
    public const uint ArchBlackwell = 10;

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetArchitecture")]
    public static partial int DeviceGetArchitecture(nint device, out uint arch);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")]
    public static partial int DeviceGetTemperature(nint device, uint sensor, out uint celsius);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static partial int DeviceGetClockInfo(nint device, ClockType type, out uint mhz);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static partial int DeviceGetUtilizationRates(nint device, out Utilization util);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static partial int DeviceGetMemoryInfo(nint device, out Memory mem);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static partial int DeviceGetPowerUsage(nint device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    public static partial int DeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetNumFans")]
    public static partial int DeviceGetNumFans(nint device, out uint count);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeed_v2")]
    public static partial int DeviceGetFanSpeed(nint device, uint fan, out uint percent);

    public static unsafe string GetDriverVersion()
    {
        byte* buf = stackalloc byte[80];
        return SystemGetDriverVersion(buf, 80) == Success ? CString(buf, 80) : "?";
    }

    public static unsafe string GetName(nint device)
    {
        byte* buf = stackalloc byte[96];
        return DeviceGetName(device, buf, 96) == Success ? CString(buf, 96) : "NVIDIA GPU";
    }

    private static unsafe string CString(byte* buf, int max)
    {
        int n = 0;
        while (n < max && buf[n] != 0) n++;
        return Encoding.ASCII.GetString(buf, n);
    }
}
