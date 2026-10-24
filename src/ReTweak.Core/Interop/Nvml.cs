using System.Runtime.InteropServices;
using System.Text;

namespace ReTweak.Core.Interop;

/// <summary>
/// Minimal NVML bindings. nvml.dll ships with every NVIDIA driver (System32).
/// Only documented calls are used here; all are read-only except the fan and power-limit setters.
/// </summary>
internal static partial class Nvml
{
    private const string Lib = "nvml.dll";

    // Linux ships the same library as libnvidia-ml.so.1; map the Windows name to it so the bindings work unchanged.
    // Runs before the first NVML call (and only once, as SetDllImportResolver requires).
    static Nvml() =>
        NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, (name, assembly, path) =>
            name == Lib && OperatingSystem.IsLinux() ? NativeLibrary.Load("libnvidia-ml.so.1") : 0);

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

    /// <summary>Video decode engine load (%); a video playing shows here, not in GPU load.</summary>
    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetDecoderUtilization")]
    public static partial int DeviceGetDecoderUtilization(nint device, out uint percent, out uint samplingPeriodUs);

    /// <summary>Video encode engine load (%): streaming, recording, transcoding.</summary>
    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetEncoderUtilization")]
    public static partial int DeviceGetEncoderUtilization(nint device, out uint percent, out uint samplingPeriodUs);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static partial int DeviceGetMemoryInfo(nint device, out Memory mem);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static partial int DeviceGetPowerUsage(nint device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    public static partial int DeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
    public static partial int DeviceGetPowerManagementLimitConstraints(nint device, out uint minMilliwatts, out uint maxMilliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")]
    public static partial int DeviceGetPowerManagementDefaultLimit(nint device, out uint milliwatts);

    /// <summary>Needs administrator rights. Lasts until the driver reloads (a reboot).</summary>
    [LibraryImport(Lib, EntryPoint = "nvmlDeviceSetPowerManagementLimit")]
    public static partial int DeviceSetPowerManagementLimit(nint device, uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetNumFans")]
    public static partial int DeviceGetNumFans(nint device, out uint count);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeed_v2")]
    public static partial int DeviceGetFanSpeed(nint device, uint fan, out uint percent);

    // Fan control: the only calls that write to the GPU. Setting a speed needs administrator rights.
    public const int NoPermission = 4;

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetMinMaxFanSpeed")]
    public static partial int DeviceGetMinMaxFanSpeed(nint device, out uint min, out uint max);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetTargetFanSpeed")]
    public static partial int DeviceGetTargetFanSpeed(nint device, uint fan, out uint percent);

    /// <summary>Takes the fan off the driver's automatic control and holds it at <paramref name="percent"/>.</summary>
    [LibraryImport(Lib, EntryPoint = "nvmlDeviceSetFanSpeed_v2")]
    public static partial int DeviceSetFanSpeed(nint device, uint fan, uint percent);

    /// <summary>Hands the fan back to the driver's (and the card's VBIOS) automatic curve.</summary>
    [LibraryImport(Lib, EntryPoint = "nvmlDeviceSetDefaultFanSpeed_v2")]
    public static partial int DeviceSetDefaultFanSpeed(nint device, uint fan);

    public static string ErrorString(int rc) => rc switch
    {
        Success => "ok",
        NotSupported => "not supported on this card",
        // FanController looks for "administrator" in this text to tell a permission problem.
        NoPermission => OperatingSystem.IsWindows() ? "needs administrator rights" : "needs root (administrator rights)",
        _ => $"NVML error {rc}",
    };

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
