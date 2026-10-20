using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>Whether ReTweak can show the hotspot, and if not, what the user can do about it.</summary>
public enum HotspotAvailability
{
    Available,
    /// <summary>Not an RTX 50 card; older cards' drivers handle temperatures without help.</summary>
    NotApplicable,
    /// <summary>Optional PawnIO driver isn't installed.</summary>
    PawnIoMissing,
    /// <summary>PawnIO is installed but ReTweak isn't running as administrator.</summary>
    NeedsAdmin,
    /// <summary>The register isn't there or isn't reporting on this GPU.</summary>
    Unsupported,
    Failed,
}

/// <summary>
/// GPU hotspot on Blackwell (RTX 50), which NVIDIA's driver APIs no longer report.
///
/// Read from six GPU registers at BAR0 + 0xAD0A90 via PawnIO's signed NVIDIA module (found by
/// disassembling HWMonitor 1.68; see docs/power-detector.md, "Hotspot"). Bit 30 marks a valid
/// sample; the low 16 bits are 1/256 °C. Channels #1-#4 are individual sensors and #5
/// (0xAD0AA0) is exactly their maximum in every one of 2,889 reads: that's the hotspot.
/// HWMonitor shows #1 as "Hot Spot", which reads low whenever another sensor is hotter.
/// Read-only.
/// </summary>
public sealed class NvidiaHotspot : IDisposable
{
    private const string ModuleResource = "ReTweak.Core.PawnIo.Nvidia.bin";
    private const string ReadFunction = "ioctl_read_thermal_registers";
    public const int ChannelCount = 6;
    /// <summary>Index of the channel that is the maximum of the four sensors (#5).</summary>
    public const int HotspotChannel = 4;
    private const int SensorChannels = 4;
    private const long ValidBit = 1L << 30;

    private readonly PawnIo _pawnIo;
    private readonly long[] _pciAddress;

    private NvidiaHotspot(PawnIo pawnIo, uint bus, uint device, uint function)
    {
        _pawnIo = pawnIo;
        _pciAddress = [bus, device, function];
    }

    /// <summary>Null when unavailable; <paramref name="reason"/> then says why, in words for the user.</summary>
    internal static NvidiaHotspot? TryCreate(uint bus, uint device, uint function, out HotspotAvailability availability, out string reason)
    {
        byte[] module;
        using (var s = typeof(NvidiaHotspot).Assembly.GetManifestResourceStream(ModuleResource)
                       ?? throw new InvalidOperationException("PawnIO module missing from the build."))
        {
            module = new byte[s.Length];
            s.ReadExactly(module);
        }

        var pawnIo = PawnIo.TryOpen(module, out availability, out string? error);
        if (pawnIo is null) { reason = error!; return null; }

        var hotspot = new NvidiaHotspot(pawnIo, bus, device, function);
        if (hotspot.ReadChannels() is not { } channels)
        {
            hotspot.Dispose();
            availability = HotspotAvailability.Unsupported;
            reason = "this GPU doesn't expose the register (GB202-GB207 only)";
            return null;
        }
        // The register answered. A sample without the valid bit (just after boot or a power-state change)
        // isn't a reason to give up for the session; the sampler skips invalid samples.
        _ = channels;
        reason = "";
        return hotspot;
    }

    /// <summary>All six channels in °C (see <see cref="Hotspot"/>); null entries weren't valid. Null if the read failed.</summary>
    public double?[]? Read() => ReadChannels() is { } c ? Array.ConvertAll(c, Decode) : null;

    /// <summary>All six raw thermal channels, or null if the read failed.</summary>
    public long[]? ReadChannels() =>
        _pawnIo.Execute(ReadFunction, _pciAddress, ChannelCount) is { Length: ChannelCount } c ? c : null;

    /// <summary>
    /// The hotspot from one read: channel #5, or the hottest of sensors #1-#4 if #5 wasn't valid.
    /// </summary>
    public static double? Hotspot(IReadOnlyList<double?> channels) =>
        channels.Count > HotspotChannel && channels[HotspotChannel] is double h ? h
        : channels.Take(SensorChannels).Max();

    /// <summary>Raw channel to °C; null when the valid bit is clear or the value is implausible.</summary>
    public static double? Decode(long raw)
    {
        if ((raw & ValidBit) == 0) return null;
        double c = (raw & 0xFFFF) / 256.0;
        return c is > 0 and < 150 ? c : null;
    }

    public void Dispose() => _pawnIo.Dispose();
}
