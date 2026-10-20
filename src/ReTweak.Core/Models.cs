namespace ReTweak.Core;

/// <summary>One 12V pin of the 12V-2x6 / 12VHPWR connector.</summary>
public readonly record struct PinReading(int Pin, double Volts, double Amps, bool Valid)
{
    public double Watts => Valid ? Volts * Amps : 0;
}

public sealed record ConnectorReading(IReadOnlyList<PinReading> Pins)
{
    /// <summary>Spec limit for a 12V-2x6 power pin.</summary>
    public const double PinRatedAmps = 9.5;

    public double TotalAmps => Pins.Where(p => p.Valid).Sum(p => p.Amps);
    public double TotalWatts => Pins.Sum(p => p.Watts);
    public double MinVolts => Pins.Where(p => p.Valid).Select(p => p.Volts).DefaultIfEmpty().Min();
    public double MaxAmps => Pins.Where(p => p.Valid).Select(p => p.Amps).DefaultIfEmpty().Max();

    /// <summary>Highest pin current relative to the average (1.0 = perfectly balanced).</summary>
    public double Imbalance
    {
        get
        {
            var valid = Pins.Where(p => p.Valid).ToList();
            double avg = valid.Count > 0 ? valid.Average(p => p.Amps) : 0;
            return avg > 0.5 ? MaxAmps / avg : 1.0; // ignore noise at idle
        }
    }
}

/// <summary>One ASUS board temperature sensor; <see cref="Celsius"/> is null when absent or implausible.</summary>
public readonly record struct BoardTemp(string Name, double? Celsius);

public sealed record GpuInfo(int Index, string Name, uint PciBus, ushort DeviceId, ushort SubVendorId, ushort SubDeviceId)
{
    public bool IsAsus => SubVendorId == 0x1043;
}

public sealed record GpuSnapshot(
    DateTime Time,
    GpuInfo Gpu,
    uint? TemperatureC,
    uint? CoreClockMhz,
    uint? MemoryClockMhz,
    uint? GpuUtilization,
    uint? MemoryUtilization,
    double? PowerWatts,
    double? PowerLimitWatts,
    IReadOnlyList<uint> FanPercents,
    ulong? VramUsedBytes,
    ulong? VramTotalBytes,
    ConnectorReading? Connector,
    double? CoreTempPreciseC = null,
    double? MemoryTempC = null,
    IReadOnlyList<BoardTemp>? BoardTemps = null,
    string? MemoryTempSource = null,
    double? HotspotC = null,                      // RTX 50 hotspot, averaged over the last second (10 Hz)
    IReadOnlyList<double?>? ThermalChannels = null, // each GPU thermal channel, averaged the same way
    double? HotspotMaxC = null,                   // highest hotspot reading in the last second
    uint? DecoderUtilization = null,              // video decode engine load (%)
    uint? EncoderUtilization = null,              // video encode engine load (%)
    IReadOnlyList<int>? MemoryChipsC = null,       // each memory chip's temperature (PawnIO), when available
    double? HotspotSpreadC = null)                // hotspot minus the coolest die sensor, averaged over the last second
{
    /// <summary>NVIDIA's die average (thermal channel #6), or null.</summary>
    public double? DieAverageC => ThermalChannels is { } c && c.Count > NvidiaHotspot.AverageChannel ? c[NvidiaHotspot.AverageChannel] : null;

    /// <summary>Best available core temperature: the precise sensor if present, else NVML's integer value.</summary>
    public double? CoreTempC => CoreTempPreciseC ?? TemperatureC;

    /// <summary>Hottest memory chip (PawnIO), or null.</summary>
    public int? MemoryChipMaxC => MemoryChipsC is { Count: > 0 } c ? c.Max() : null;

    /// <summary>Hottest VRM board sensor (ASUS), or null.</summary>
    public double? VrmMaxC => BoardTemps?.Where(b => b.Name.StartsWith("VRM")).Select(b => b.Celsius).Max();
}
