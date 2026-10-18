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
    ConnectorReading? Connector);
