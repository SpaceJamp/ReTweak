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
                                 NvidiaHotspot? Hotspot, HotspotAvailability HotspotState, string HotspotNote);

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
            NvidiaHotspot? hotspot = null;
            string hotspotNote = "Not reported by this card";
            var hotspotState = HotspotAvailability.NotApplicable;
            if (nvapi is not null && nvapiByBus.TryGetValue(pci.Bus, out var nvHandle))
            {
                if (info.IsAsus) detector = AsusPowerDetector.TryCreate(nvapi, nvHandle);
                uint? nvmlCore = Nvml.DeviceGetTemperature(handle, 0, out uint c) == Nvml.Success ? c : null;
                if (Nvml.DeviceGetArchitecture(handle, out uint arch) == Nvml.Success)
                {
                    thermal = NvidiaThermal.TryCreate(nvapi, nvHandle, arch, nvmlCore);
                    // RTX 50 dropped the hotspot from the driver APIs; read the GPU register through PawnIO instead.
                    if (arch == Nvml.ArchBlackwell)
                    {
                        hotspot = NvidiaHotspot.TryCreate(pci.Bus, pci.Device, PciFunction(pci), out hotspotState, out string why);
                        hotspotNote = hotspot is null ? $"Unavailable: {why}" : "from GPU register via PawnIO";
                    }
                }
            }

            _devices.Add(new Device(info, handle, fans, detector, thermal, hotspot, hotspotState, hotspotNote));
        }
    }

    /// <summary>"0000:01:00.0" -> 0. NVML's PCI struct has no separate function field.</summary>
    private static unsafe uint PciFunction(Nvml.PciInfo pci)
    {
        var id = System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(pci.BusId, 32)).TrimEnd('\0');
        int dot = id.LastIndexOf('.');
        return dot >= 0 && uint.TryParse(id.AsSpan(dot + 1), System.Globalization.NumberStyles.HexNumber, null, out uint f) ? f : 0;
    }

    public bool HasHotspot(int gpuIndex) => _devices[gpuIndex].Hotspot is not null;

    public HotspotAvailability HotspotState(int gpuIndex) => _devices[gpuIndex].HotspotState;

    /// <summary>Where the hotspot comes from, or why there isn't one (shown under the value).</summary>
    public string HotspotNote(int gpuIndex) => _devices[gpuIndex].HotspotNote;

    public bool HasConnectorSensor(int gpuIndex) => _devices[gpuIndex].Detector is not null;

    /// <summary>True if a memory temperature is available (ASUS board sensors, or the chip sensor on Blackwell).</summary>
    public bool HasMemoryTemperature(int gpuIndex) =>
        _devices[gpuIndex].Detector is not null || _devices[gpuIndex].Thermal?.HasMemory == true;

    /// <summary>Connector-only read (fast path for high-rate sampling). Null if unsupported or the read failed.</summary>
    public ConnectorReading? ReadConnector(int gpuIndex) => _devices[gpuIndex].Detector?.Read();

    /// <param name="includeConnector">False when a <see cref="ConnectorSampler"/> already polls the connector.</param>
    public GpuSnapshot Sample(int gpuIndex, bool includeConnector = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var d = _devices[gpuIndex];
        var h = d.Nvml;

        static T? Get<T>(int rc, T value) where T : struct => rc == Nvml.Success ? value : null;

        uint? temp = Get(Nvml.DeviceGetTemperature(h, 0, out uint t), t);
        uint? core = Get(Nvml.DeviceGetClockInfo(h, Nvml.ClockType.Graphics, out uint c), c);
        uint? mem = Get(Nvml.DeviceGetClockInfo(h, Nvml.ClockType.Memory, out uint m), m);
        int utilRc = Nvml.DeviceGetUtilizationRates(h, out var util);
        int powerRc = Nvml.DeviceGetPowerUsage(h, out uint mw);
        int limitRc = Nvml.DeviceGetEnforcedPowerLimit(h, out uint limitMw);
        int vramRc = Nvml.DeviceGetMemoryInfo(h, out var vram);

        var fans = new List<uint>();
        for (uint f = 0; f < d.FanCount; f++)
            if (Nvml.DeviceGetFanSpeed(h, f, out uint pct) == Nvml.Success) fans.Add(pct);

        var (corePrecise, memChip) = d.Thermal?.Read() ?? (null, null);

        // Memory temperature: on ASUS boards use the board sensors by the memory (what GPU Tweak III
        // shows); otherwise fall back to the memory chips' own sensor (reads ~10 °C higher).
        var board = d.Detector?.ReadBoardTemps();
        double? memBoard = board is null ? null
            : new[] { board[AsusPowerDetector.MemorySensor1].Celsius, board[AsusPowerDetector.MemorySensor2].Celsius }.Max();
        double? memTemp = memBoard ?? memChip;
        string? memSource = memBoard is not null ? "ASUS board sensors" : memChip is not null ? "memory chip sensor" : null;

        return new GpuSnapshot(
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
            d.Hotspot?.Read());
    }

    public int FanCount(int gpuIndex) => (int)_devices[gpuIndex].FanCount;

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
            if (Nvml.DeviceSetFanSpeed(d.Nvml, f, (uint)percent) is var rc && rc != Nvml.Success)
                return Nvml.ErrorString(rc);
        return null;
    }

    /// <summary>Returns every fan to the driver's automatic curve. Null on success, else why it failed.</summary>
    public string? RestoreAutoFans(int gpuIndex)
    {
        if (_disposed) return "monitor closed";
        var d = _devices[gpuIndex];
        string? error = null;
        for (uint f = 0; f < d.FanCount; f++) // try every fan even if one fails
            if (Nvml.DeviceSetDefaultFanSpeed(d.Nvml, f) is var rc && rc != Nvml.Success)
                error ??= Nvml.ErrorString(rc);
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
