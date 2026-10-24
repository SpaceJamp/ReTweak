using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>
/// Monitor for NVIDIA GPUs. General sensors come from NVML; ASUS cards
/// with a Power Detector chip additionally get per-pin connector readings.
/// Everything is read-only except the fan setters, which <see cref="FanController"/> drives.
/// <see cref="Sample"/> and <see cref="ReadConnector"/> may run on different threads,
/// but each should only be called from one thread at a time.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private sealed record Device(GpuInfo Info, nint Nvml, uint FanCount, AsusPowerDetector? Detector, NvidiaThermal? Thermal,
                                 HotspotSampler? Hotspot, HotspotAvailability HotspotState, string HotspotNote,
                                 PowerLimits? Limits);

    private readonly List<Device> _devices = [];
    private bool _disposed;

    public string DriverVersion { get; } = "?";
    public IReadOnlyList<GpuInfo> Gpus => _devices.Select(d => d.Info).ToList();

    public GpuMonitor()
    {
        try
        {
            if (Nvml.Init() != Nvml.Success) throw new InvalidOperationException("NVML failed to initialise.");
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException("nvml.dll not found. Is an NVIDIA driver installed?");
        }

        try { DriverVersion = Nvml.GetDriverVersion(); Enumerate(); }
        catch
        {
            foreach (var d in _devices) d.Hotspot?.Dispose(); // stop sampler threads already started
            Nvml.Shutdown(); // don't leak the NVML init if enumeration fails
            throw;
        }
    }

    private void Enumerate()
    {
        var nvapi = NvApi.TryLoad();
        var nvapiByBus = nvapi?.GpusByBus() ?? [];

        Nvml.DeviceGetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            if (Nvml.DeviceGetHandleByIndex(i, out var handle) != Nvml.Success) continue;
            if (Nvml.DeviceGetPciInfo(handle, out var pci) != Nvml.Success) pci = default;
            var info = new GpuInfo(
                (int)i,
                Nvml.GetName(handle),
                pci.Bus,
                (ushort)(pci.PciDeviceId >> 16),
                (ushort)(pci.PciSubSystemId & 0xFFFF),
                (ushort)(pci.PciSubSystemId >> 16));

            if (Nvml.DeviceGetNumFans(handle, out uint fans) != Nvml.Success) fans = 0;

            // Only probe the I2C bus on ASUS boards: address 0x56 means nothing on other vendors' cards.
            AsusPowerDetector? detector = null;
            NvidiaThermal? thermal = null;
            HotspotSampler? hotspot = null;
            // Older cards report it through NVAPI, which is Windows-only.
            string hotspotNote = OperatingSystem.IsWindows() ? "Not reported by this card" : "Not read on Linux for this card";
            var hotspotState = HotspotAvailability.NotApplicable;
            uint? arch = Nvml.DeviceGetArchitecture(handle, out uint a) == Nvml.Success ? a : null;
            if (nvapi is not null && nvapiByBus.TryGetValue(pci.Bus, out var nvHandle))
            {
                if (info.IsAsus) detector = AsusPowerDetector.TryCreate(nvapi, nvHandle);
                uint? nvmlCore = Nvml.DeviceGetTemperature(handle, 0, out uint c) == Nvml.Success ? c : null;
                if (arch is uint ar) thermal = NvidiaThermal.TryCreate(nvapi, nvHandle, ar, nvmlCore);
            }
            // RTX 50 dropped the hotspot from the driver APIs; read the GPU register instead: through PawnIO on
            // Windows, a read-only mapping of BAR0 from sysfs on Linux.
            if (arch == Nvml.ArchBlackwell && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                var reader = OperatingSystem.IsWindows()
                    ? NvidiaHotspot.TryCreate(pci.Bus, pci.Device, PciFunction(pci), out hotspotState, out string why)
                    : NvidiaHotspot.TryCreateLinux($"/sys/bus/pci/devices/{LinuxPciAddress(pci)}",
                                                   $"/sys/kernel/retweak_hotspot/{LinuxPciAddress(pci)}/channels", out hotspotState, out why);
                hotspot = reader is null ? null : new HotspotSampler(reader);
                hotspotNote = hotspot is null ? $"Unavailable: {why}"
                            : OperatingSystem.IsWindows() ? "from GPU register via PawnIO" : $"from GPU register ({reader!.Source})";
            }

            PowerLimits? limits = Nvml.DeviceGetPowerManagementLimitConstraints(handle, out uint minMw, out uint maxMw) == Nvml.Success
                                  && Nvml.DeviceGetPowerManagementDefaultLimit(handle, out uint defMw) == Nvml.Success && defMw > 0
                ? new PowerLimits(minMw / 1000.0, maxMw / 1000.0, defMw / 1000.0) : null;
            _devices.Add(new Device(info, handle, fans, detector, thermal, hotspot, hotspotState, hotspotNote, limits));
        }
    }

    /// <summary>The kernel's name for the GPU, e.g. "0000:01:00.0".</summary>
    private static string LinuxPciAddress(Nvml.PciInfo pci) => $"{pci.Domain:x4}:{pci.Bus:x2}:{pci.Device:x2}.{PciFunction(pci)}";

    /// <summary>"0000:01:00.0" -> 0. NVML's PCI struct has no separate function field.</summary>
    private static unsafe uint PciFunction(Nvml.PciInfo pci)
    {
        var id = System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(pci.BusId, 32)).TrimEnd('\0');
        int dot = id.LastIndexOf('.');
        return dot >= 0 && uint.TryParse(id.AsSpan(dot + 1), System.Globalization.NumberStyles.HexNumber, null, out uint f) ? f : 0;
    }

    public bool HasHotspot(int gpuIndex) => _devices[gpuIndex].Hotspot is not null;

    /// <summary>The hotspot is averaged over this window; a fresh monitor needs this long for a full one.</summary>
    public static TimeSpan HotspotWindow => HotspotSampler.WindowLength;

    public HotspotAvailability HotspotState(int gpuIndex) => _devices[gpuIndex].HotspotState;

    /// <summary>Where the hotspot comes from, or why there isn't one (shown under the value).</summary>
    public string HotspotNote(int gpuIndex) => _devices[gpuIndex].HotspotNote;

    public bool HasConnectorSensor(int gpuIndex) => _devices[gpuIndex].Detector is not null;

    /// <summary>True if a memory temperature is available (ASUS board sensors, or the chip sensor on Blackwell).</summary>
    public bool HasMemoryTemperature(int gpuIndex) =>
        _devices[gpuIndex].Detector is not null || _devices[gpuIndex].Thermal?.HasMemory == true;

    /// <summary>Connector-only read (fast path for high-rate sampling). Null if unsupported or the read failed.</summary>
    public ConnectorReading? ReadConnector(int gpuIndex)
    {
        if (_devices[gpuIndex].Detector is not { } detector) return null;
        using var timer = Diag.Time("asus.i2c.pins");
        var r = detector.Read();
        if (r is null) timer.Fail("read failed");
        return r;
    }

    /// <param name="includeConnector">False when a <see cref="ConnectorSampler"/> already polls the connector.</param>
    public GpuSnapshot Sample(int gpuIndex, bool includeConnector = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var d = _devices[gpuIndex];
        var h = d.Nvml;

        static T? Get<T>(int rc, T value) where T : struct => rc == Nvml.Success ? value : null;
        // Times one NVML call for the diagnostic log and records a failing return code.
        static int Timed(string call, Func<int> nvml)
        {
            using var timer = Diag.Time(call);
            int rc = nvml();
            if (rc != Nvml.Success) timer.Fail(Nvml.ErrorString(rc));
            return rc;
        }

        uint t = 0, c = 0, m = 0, mw = 0, limitMw = 0;
        Nvml.Utilization util = default;
        Nvml.Memory vram = default;
        uint? temp = Get(Timed("nvml.temperature", () => Nvml.DeviceGetTemperature(h, 0, out t)), t);
        uint? core = Get(Timed("nvml.clock.graphics", () => Nvml.DeviceGetClockInfo(h, Nvml.ClockType.Graphics, out c)), c);
        uint? mem = Get(Timed("nvml.clock.memory", () => Nvml.DeviceGetClockInfo(h, Nvml.ClockType.Memory, out m)), m);
        int utilRc = Timed("nvml.utilization", () => Nvml.DeviceGetUtilizationRates(h, out util));
        int powerRc = Timed("nvml.power", () => Nvml.DeviceGetPowerUsage(h, out mw));
        int limitRc = Timed("nvml.power_limit", () => Nvml.DeviceGetEnforcedPowerLimit(h, out limitMw));
        int vramRc = Timed("nvml.memory_info", () => Nvml.DeviceGetMemoryInfo(h, out vram));
        uint dec = 0, enc = 0;
        uint? decoder = Get(Timed("nvml.decoder", () => Nvml.DeviceGetDecoderUtilization(h, out dec, out _)), dec);
        uint? encoder = Get(Timed("nvml.encoder", () => Nvml.DeviceGetEncoderUtilization(h, out enc, out _)), enc);

        var fans = new List<uint>();
        for (uint f = 0; f < d.FanCount; f++)
        {
            uint pct = 0, fan = f;
            if (Timed("nvml.fan_speed", () => Nvml.DeviceGetFanSpeed(h, fan, out pct)) == Nvml.Success) fans.Add(pct);
        }

        (double? corePrecise, double? memChip) = (null, null);
        if (d.Thermal is { } thermal)
        {
            using var timer = Diag.Time("nvapi.thermal_sensors");
            (corePrecise, memChip) = thermal.Read();
            if (corePrecise is null && memChip is null) timer.Fail("no reading");
        }

        // Memory temperature: on ASUS boards use the board sensors by the memory (what GPU Tweak III
        // shows); otherwise fall back to the memory chips' own sensor (reads ~10 °C higher).
        IReadOnlyList<BoardTemp>? board = null;
        if (d.Detector is { } detector)
        {
            using var timer = Diag.Time("asus.i2c.board_temps");
            board = detector.ReadBoardTemps();
            if (board is null) timer.Fail("read failed");
        }
        var hotspot = d.Hotspot?.Summary(); // last second at 10 Hz; hotspot = channel #5, the max of sensors #1-#4
        double? memBoard = board is null ? null
            : new[] { board[AsusPowerDetector.MemorySensor1].Celsius, board[AsusPowerDetector.MemorySensor2].Celsius }.Max();
        double? memTemp = memBoard ?? memChip;
        string? memSource = memBoard is not null ? "ASUS board sensors" : memChip is not null ? "memory chip sensor" : null;

        var snapshot = new GpuSnapshot(
            DateTime.Now,
            d.Info,
            temp,
            core,
            mem,
            utilRc == Nvml.Success ? util.Gpu : null,
            utilRc == Nvml.Success ? util.Memory : null,
            powerRc == Nvml.Success ? mw / 1000.0 : null,
            limitRc == Nvml.Success ? limitMw / 1000.0 : null,
            fans,
            vramRc == Nvml.Success ? vram.Used : null,
            vramRc == Nvml.Success ? vram.Total : null,
            includeConnector ? d.Detector?.Read() : null,
            corePrecise,
            memTemp,
            board,
            memSource,
            hotspot?.AverageC,
            hotspot?.Channels,
            hotspot?.MaxC,
            decoder,
            encoder,
            d.Hotspot?.MemoryChips(),
            hotspot?.SpreadC,
            d.Limits?.DefaultW);
        if (Diag.IsOn(DiagLevel.Debug)) LogSnapshot(snapshot, hotspot?.Samples);
        return snapshot;
    }

    /// <summary>Every reading in one diagnostic line, so a log can be replayed against a problem.</summary>
    private static void LogSnapshot(GpuSnapshot s, int? hotspotSamples) => Diag.Debug(DiagCategory.Sensor, "snapshot", new
    {
        gpu = s.Gpu.Index,
        coreC = s.CoreTempC,
        coreNvmlC = s.TemperatureC,
        hotspotC = s.HotspotC,
        hotspotMaxC = s.HotspotMaxC,
        hotspotSamples,
        thermalChannelsC = s.ThermalChannels,
        hotspotSpreadC = s.HotspotSpreadC,
        dieAverageC = s.DieAverageC,
        memoryChipsC = s.MemoryChipsC,
        decoderPct = s.DecoderUtilization,
        encoderPct = s.EncoderUtilization,
        memoryC = s.MemoryTempC,
        memorySource = s.MemoryTempSource,
        board = s.BoardTemps?.Select(b => new { b.Name, b.Celsius }),
        coreMhz = s.CoreClockMhz,
        memMhz = s.MemoryClockMhz,
        loadPct = s.GpuUtilization,
        memLoadPct = s.MemoryUtilization,
        powerW = s.PowerWatts,
        powerLimitW = s.PowerLimitWatts,
        fansPct = s.FanPercents,
        vramUsedMiB = s.VramUsedBytes / 1048576,
    });

    public int FanCount(int gpuIndex) => (int)_devices[gpuIndex].FanCount;

    /// <summary>The power limits the driver allows, or null if the card doesn't report them.</summary>
    public PowerLimits? PowerLimits(int gpuIndex) => _devices[gpuIndex].Limits;

    /// <summary>
    /// Sets the power limit to <paramref name="percent"/> of the card's default (clamped to what the driver
    /// allows); 100 restores the default. Needs administrator rights. Null on success, else why it failed.
    /// </summary>
    public string? SetPowerLimitPercent(int gpuIndex, int percent, out double watts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var d = _devices[gpuIndex];
        watts = 0;
        if (d.Limits is not { } l) return "this card doesn't report its power limits";
        watts = Math.Clamp(l.DefaultW * percent / 100.0, l.MinW, l.MaxW);
        using var timer = Diag.Time("nvml.set_power_limit");
        int rc = Nvml.DeviceSetPowerManagementLimit(d.Nvml, (uint)Math.Round(watts * 1000));
        if (rc == Nvml.Success) return null;
        timer.Fail(Nvml.ErrorString(rc));
        return Nvml.ErrorString(rc);
    }

    /// <summary>Lowest and highest manual fan speed the driver accepts (%), or null if unknown.</summary>
    public (int Min, int Max)? FanSpeedRange(int gpuIndex) =>
        Nvml.DeviceGetMinMaxFanSpeed(_devices[gpuIndex].Nvml, out uint min, out uint max) == Nvml.Success && max > min
            ? ((int)min, (int)max) : null;

    /// <summary>Speed the driver is currently driving fan <paramref name="fan"/> towards (%), or null.</summary>
    public int? FanTarget(int gpuIndex, int fan) =>
        Nvml.DeviceGetTargetFanSpeed(_devices[gpuIndex].Nvml, (uint)fan, out uint pct) == Nvml.Success ? (int)pct : null;

    /// <summary>Holds every fan at <paramref name="percent"/>. Returns null on success, else why it failed.</summary>
    public string? SetFanSpeed(int gpuIndex, int percent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var d = _devices[gpuIndex];
        if (d.FanCount == 0) return "this card reports no fans";
        for (uint f = 0; f < d.FanCount; f++)
        {
            using var timer = Diag.Time("nvml.set_fan_speed");
            if (Nvml.DeviceSetFanSpeed(d.Nvml, f, (uint)percent) is var rc && rc != Nvml.Success)
            {
                timer.Fail(Nvml.ErrorString(rc));
                return Nvml.ErrorString(rc);
            }
        }
        return null;
    }

    /// <summary>Returns every fan to the driver's automatic curve. Null on success, else why it failed.</summary>
    public string? RestoreAutoFans(int gpuIndex)
    {
        if (_disposed) return "monitor closed";
        var d = _devices[gpuIndex];
        string? error = null;
        for (uint f = 0; f < d.FanCount; f++) // try every fan even if one fails
        {
            using var timer = Diag.Time("nvml.restore_fan");
            if (Nvml.DeviceSetDefaultFanSpeed(d.Nvml, f) is var rc && rc != Nvml.Success)
            {
                timer.Fail(Nvml.ErrorString(rc));
                error ??= Nvml.ErrorString(rc);
            }
        }
        return error;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var d in _devices) d.Hotspot?.Dispose();
        Nvml.Shutdown();
    }
}
