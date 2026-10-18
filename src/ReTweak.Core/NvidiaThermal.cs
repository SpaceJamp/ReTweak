using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>
/// Extra temperatures from NVAPI's undocumented thermal-sensor call.
///
/// The slot layout differs by architecture. Only Blackwell (RTX 50) is enabled, where it was
/// verified on an RTX 5080 under load (docs/power-detector.md, "Temperature sensors"):
///   slot 1 = GPU core at 1/256 °C resolution (matches NVML's integer reading)
///   slot 2 = memory (GDDR7): whole degrees, lags the core, stays warm after load stops
/// No hotspot is exposed on Blackwell. Older architectures use other slots (reportedly
/// 1 = hotspot, 9 = memory junction) and are left off until verified.
/// </summary>
internal sealed class NvidiaThermal
{
    private const int CoreSlot = 1, MemorySlot = 2;

    private readonly NvApi _nvapi;
    private readonly nint _gpu;
    private readonly int _count;
    private readonly bool _hasCore, _hasMemory;

    private NvidiaThermal(NvApi nvapi, nint gpu, int count, bool hasCore, bool hasMemory)
    {
        _nvapi = nvapi;
        _gpu = gpu;
        _count = count;
        _hasCore = hasCore;
        _hasMemory = hasMemory;
    }

    /// <summary>
    /// Probes the sensor layout once. <paramref name="nvmlCoreC"/> is a sanity check: the
    /// precise core slot must agree with NVML, or we don't trust the layout on this card.
    /// </summary>
    public static NvidiaThermal? TryCreate(NvApi nvapi, nint gpu, uint architecture, uint? nvmlCoreC)
    {
        if (architecture != Nvml.ArchBlackwell) return null;
        int count = nvapi.ProbeThermalSensorCount(gpu);
        if (count <= MemorySlot) return null;

        Span<int> t = stackalloc int[32];
        if (!nvapi.ReadThermalSensors(gpu, count, t)) return null;

        bool hasCore = Plausible(t[CoreSlot]) && nvmlCoreC is uint n && Math.Abs(t[CoreSlot] / 256.0 - n) <= 3;
        bool hasMemory = Plausible(t[MemorySlot]);
        return hasCore || hasMemory ? new NvidiaThermal(nvapi, gpu, count, hasCore, hasMemory) : null;
    }

    public bool HasMemory => _hasMemory;

    /// <summary>Precise core and memory temperatures; null where unavailable or implausible.</summary>
    public (double? Core, double? Memory) Read()
    {
        Span<int> t = stackalloc int[32];
        if (!_nvapi.ReadThermalSensors(_gpu, _count, t)) return (null, null);
        double? core = _hasCore && Plausible(t[CoreSlot]) ? t[CoreSlot] / 256.0 : null;
        double? mem = _hasMemory && Plausible(t[MemorySlot]) ? t[MemorySlot] / 256.0 : null;
        return (core, mem);
    }

    private static bool Plausible(int raw) => raw != NvApi.NoThermalSensor && raw > 0 && raw / 256.0 < 130;
}
