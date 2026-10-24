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
/// Read from six GPU registers at BAR0 + 0xAD0A90 via PawnIO's signed NVIDIA module on Windows, or a
/// read-only mapping of BAR0 from sysfs on Linux (<see cref="LinuxBar0"/>) (found by
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
    private const string MemoryFunction = "ioctl_read_memory_temperatures";
    /// <summary>The module reports up to 48 memory-chip sensors; unavailable ones are INT32_MIN.</summary>
    public const int MemorySensorCount = 48;
    public const int ChannelCount = 6;
    /// <summary>Index of the channel that is the maximum of the four sensors (#5).</summary>
    public const int HotspotChannel = 4;
    private const int SensorChannels = 4;
    private const long ValidBit = 1L << 30;

    /// <summary>Where the six channels sit in BAR0; the PawnIO module reads the same registers.</summary>
    private const long ChannelOffset = 0xAD0A90;
    private const string NvidiaVendor = "0x10de";

    // Exactly one backend: PawnIO on Windows; on Linux the retweak_hotspot module's file, or a read-only BAR0 mapping.
    private readonly PawnIo? _pawnIo;
    private readonly LinuxBar0? _bar0;
    private readonly string? _moduleFile;
    private readonly long[] _pciAddress = [];

    private NvidiaHotspot(PawnIo pawnIo, uint bus, uint device, uint function)
    {
        _pawnIo = pawnIo;
        _pciAddress = [bus, device, function];
    }

    private NvidiaHotspot(LinuxBar0 bar0) => _bar0 = bar0;
    private NvidiaHotspot(string moduleFile) => _moduleFile = moduleFile;

    /// <summary>How the registers are read, for the note under the value.</summary>
    public string Source => _moduleFile is not null ? "retweak_hotspot module" : _bar0 is not null ? "BAR0, read-only" : "PawnIO";

    /// <summary>
    /// Linux: reads <paramref name="moduleFile"/> from the retweak_hotspot kernel module (linux/retweak-hotspot) if it's
    /// loaded; any user can read it. Otherwise, as root, maps the registers read-only from the GPU's sysfs folder
    /// (/sys/bus/pci/devices/0000:01:00.0). Null when unavailable; <paramref name="reason"/> then says why, in words for the user.
    /// </summary>
    public static NvidiaHotspot? TryCreateLinux(string pciDeviceFolder, string moduleFile, out HotspotAvailability availability, out string reason)
    {
        // The module only makes the file for a supported GPU. A failed read just means it's asleep right now (laptops);
        // the sampler skips those.
        if (File.Exists(moduleFile))
        {
            (availability, reason) = (HotspotAvailability.Available, "");
            return new NvidiaHotspot(moduleFile);
        }
        string vendor;
        try { vendor = File.ReadAllText(Path.Combine(pciDeviceFolder, "vendor")).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (availability, reason) = (HotspotAvailability.Unsupported, $"can't find the GPU in sysfs ({pciDeviceFolder})");
            return null;
        }
        // Never map a device that isn't NVIDIA's: the offsets mean nothing elsewhere.
        if (vendor != NvidiaVendor)
        {
            (availability, reason) = (HotspotAvailability.Unsupported, $"{pciDeviceFolder} isn't an NVIDIA device");
            return null;
        }
        var bar0 = LinuxBar0.TryOpen(Path.Combine(pciDeviceFolder, "resource0"), ChannelOffset, ChannelCount * 4, out availability, out string? error);
        if (bar0 is null) { reason = $"{error}; or install the retweak-hotspot module"; return null; }
        reason = "";
        return new NvidiaHotspot(bar0);
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
    public long[]? ReadChannels()
    {
        if (_moduleFile is not null)
        {
            // Six hex registers, e.g. "40002980 40002c40 ...". The module refuses the read while the GPU is powered down.
            try
            {
                var parts = File.ReadAllText(_moduleFile).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var c = new long[ChannelCount];
                if (parts.Length != ChannelCount) return null;
                for (int i = 0; i < ChannelCount; i++)
                    if (!long.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out c[i])) return null;
                return c;
            }
            catch (IOException) { return null; }
        }
        if (_bar0 is not null)
        {
            var c = new long[ChannelCount];
            for (int i = 0; i < ChannelCount; i++) c[i] = _bar0.Read32(ChannelOffset + 4 * i);
            return c;
        }
        return _pawnIo!.Execute(ReadFunction, _pciAddress, ChannelCount) is { Length: ChannelCount } r ? r : null;
    }

    /// <summary>
    /// Memory-chip temperatures (°C) from PawnIO's NVIDIA module (BAR0 + 0x900000 block; Ampere, Ada and
    /// Blackwell). Only the sensors that report are returned; null if the call failed or none report.
    /// </summary>
    public int[]? ReadMemoryChips()
    {
        // Linux: the retweak_hotspot module's "memory" file next to "channels" (same read as PawnIO's). Not through the
        // BAR0 fallback, which only maps the hotspot page.
        if (_moduleFile is not null)
        {
            try
            {
                var chips = File.ReadAllText(Path.Combine(Path.GetDirectoryName(_moduleFile)!, "memory"))
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => int.TryParse(t, out int c) ? c : 0).Where(t => t is > 0 and < 150).ToArray();
                return chips.Length > 0 ? chips : null;
            }
            catch (IOException) { return null; } // an older module without the file, or the GPU asleep
        }
        if (_pawnIo is null) return null;
        if (_pawnIo.Execute(MemoryFunction, _pciAddress, MemorySensorCount + 2) is not { Length: MemorySensorCount + 2 } r
            || r[1] != MemorySensorCount) return null;
        var temps = r.Skip(2).Select(v => unchecked((int)v)).Where(t => t != int.MinValue && t is > 0 and < 150).ToArray();
        return temps.Length > 0 ? temps : null;
    }

    /// <summary>
    /// The hotspot from one read: channel #5, or the hottest of sensors #1-#4 if #5 wasn't valid.
    /// Null for a read that contradicts itself: a maximum below the coolest sensor can't be right.
    /// </summary>
    public static double? Hotspot(IReadOnlyList<double?> channels)
    {
        double? coolest = channels.Take(SensorChannels).Min();
        if (channels.Count > HotspotChannel && channels[HotspotChannel] is double h)
            return h < coolest ? null : h;
        return channels.Take(SensorChannels).Max();
    }

    /// <summary>
    /// Die spread from one read: the hotspot minus the coolest of the four sensors. Needs all four.
    /// On a healthy RTX 5080 it grows with power: about 4 °C at idle, 25-32 °C at 300-380 W. A spread
    /// well beyond that means part of the die isn't being cooled: poor cooler contact or paste pump-out.
    /// </summary>
    public static double? Spread(IReadOnlyList<double?> channels)
    {
        var sensors = channels.Take(SensorChannels).ToList();
        if (sensors.Count < SensorChannels || sensors.Any(s => s is null) || Hotspot(channels) is not double h) return null;
        return h - sensors.Min()!.Value;
    }

    /// <summary>
    /// Channel #6 (0xAD0AA4): NVIDIA's own die average. It isn't the plain mean of #1-#4 (it runs
    /// ~5 °C above it at load), so the hardware presumably weights more sensors.
    /// </summary>
    public const int AverageChannel = 5;

    /// <summary>Raw channel to °C; null when the valid bit is clear or the value is implausible.</summary>
    public static double? Decode(long raw)
    {
        if ((raw & ValidBit) == 0) return null;
        double c = (raw & 0xFFFF) / 256.0;
        return c is > 0 and < 150 ? c : null;
    }

    public void Dispose()
    {
        _pawnIo?.Dispose();
        _bar0?.Dispose();
    }
}
