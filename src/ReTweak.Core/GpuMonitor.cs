using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>
/// Read-only monitor for NVIDIA GPUs. General sensors come from NVML; ASUS cards
/// with a Power Detector chip additionally get per-pin connector readings.
/// <see cref="Sample"/> and <see cref="ReadConnector"/> may run on different threads,
/// but each should only be called from one thread at a time.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private sealed record Device(GpuInfo Info, nint Nvml, uint FanCount, AsusPowerDetector? Detector);

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
            if (info.IsAsus && nvapi is not null && nvapiByBus.TryGetValue(pci.Bus, out var nvHandle))
                detector = AsusPowerDetector.TryCreate(nvapi, nvHandle);

            _devices.Add(new Device(info, handle, fans, detector));
        }
    }

    public bool HasConnectorSensor(int gpuIndex) => _devices[gpuIndex].Detector is not null;

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
            includeConnector ? d.Detector?.Read() : null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Nvml.Shutdown();
    }
}
