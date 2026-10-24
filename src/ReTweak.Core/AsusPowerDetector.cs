using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>
/// Reads ASUS "Power Detector+" per-pin voltage/current from the monitor chip on
/// ASUS cards (e.g. ROG Astral). Protocol recovered from GPU Tweak III's
/// ExpanModule.dll; see docs/power-detector.md. Read-only.
/// </summary>
internal sealed class AsusPowerDetector
{
    private const byte Port = 1;
    private const byte Address = 0x56;      // 8-bit (7-bit 0x2B)
    private const byte RegIdLow = 0x1E;
    private const byte RegIdHigh = 0x1F;
    private const byte RegPins = 0x80;      // 6 x (u16 BE mV, u16 BE mA)
    private const byte RegBoardTemps = 0x60; // 8 x s16 BE, tenths of a °C
    private const ushort MinVersion = 0x500; // same gate GPU Tweak III uses
    private const double MaxSaneAmps = 50;   // GPU Tweak III treats more as a bad read
    // A running card can't have a pin outside this range; 0x0000 / 0xFFFF frames are bus glitches.
    private const double MinSaneVolts = 6, MaxSaneVolts = 15;

    /// <summary>
    /// Board sensor names in register order. Positions 2 and 3 sit by the memory and match
    /// GPU Tweak III's memory temperature; the rest are named after where they appear to be.
    /// </summary>
    public static readonly string[] BoardSensorNames =
        ["VRM 1", "Core", "Memory 1", "Memory 2", "VRM 2", "Sensor 6", "Sensor 7", "Sensor 8"];
    public const int MemorySensor1 = 2, MemorySensor2 = 3;

    /// <summary>Reads buffer.Length bytes from a register of the chip; false if the bus said no.</summary>
    private delegate bool RegisterRead(byte register, Span<byte> buffer);

    private readonly RegisterRead _read;
    // The connector sampler and the GPU poll run on different threads; keep their I2C transactions apart.
    private readonly object _bus = new();

    public ushort Version { get; }

    private AsusPowerDetector(RegisterRead read, ushort version)
    {
        _read = read;
        Version = version;
    }

    /// <summary>Windows: through NVAPI. Returns a detector if the chip answers with a supported version.</summary>
    public static AsusPowerDetector? TryCreate(NvApi nvapi, nint gpu) =>
        TryCreate((reg, buf) => nvapi.I2CRead(gpu, Port, Address, reg, buf) == 0);

    /// <summary>
    /// Linux: through the nvidia driver's I2C bus for the same port, from i2c-dev. The bus stays open for the
    /// session (ReTweak never closes a GpuMonitor's sensors early). Null with <paramref name="note"/> saying why.
    /// </summary>
    public static AsusPowerDetector? TryCreateLinux(string pciDeviceFolder, out string note)
    {
        string? device = LinuxI2c.FindBus(pciDeviceFolder, Port, out var seen);
        if (device is null)
        {
            note = $"no \"NVIDIA i2c adapter {Port}\" under {pciDeviceFolder} (found: {(seen.Length > 0 ? string.Join("; ", seen) : "none")})";
            return null;
        }
        var bus = LinuxI2c.TryOpen(device, Address >> 1, out string? error); // i2c-dev takes the 7-bit address
        if (bus is null) { note = error!; return null; }
        var detector = TryCreate(bus.Read);
        if (detector is null)
        {
            bus.Dispose();
            note = $"the connector chip didn't answer on {device}";
            return null;
        }
        note = $"connector chip v{detector.Version:X} on {device}";
        return detector;
    }

    private static AsusPowerDetector? TryCreate(RegisterRead read)
    {
        Span<byte> lo = stackalloc byte[1], hi = stackalloc byte[1];
        if (!read(RegIdLow, lo) || !read(RegIdHigh, hi)) return null;
        ushort version = (ushort)(hi[0] << 8 | lo[0]);
        return version >= MinVersion ? new AsusPowerDetector(read, version) : null;
    }

    public ConnectorReading? Read()
    {
        Span<byte> buf = stackalloc byte[24];
        lock (_bus)
            if (!_read(RegPins, buf)) return null;

        var pins = new PinReading[6];
        for (int i = 0; i < 6; i++)
        {
            int off = (5 - i) * 4; // pin 1 is the last group, as in GPU Tweak III
            double volts = (buf[off] << 8 | buf[off + 1]) / 1000.0;
            double amps = (buf[off + 2] << 8 | buf[off + 3]) / 1000.0;
            bool valid = amps <= MaxSaneAmps && volts >= MinSaneVolts && volts <= MaxSaneVolts;
            pins[i] = new PinReading(i + 1, volts, amps, valid);
        }
        return new ConnectorReading(pins);
    }

    /// <summary>
    /// The 8 board temperatures from register 0x60, or null if the read failed. A sensor
    /// outside -40..150 °C is reported as null (absent or glitched).
    /// </summary>
    public IReadOnlyList<BoardTemp>? ReadBoardTemps()
    {
        Span<byte> buf = stackalloc byte[16];
        lock (_bus)
            if (!_read(RegBoardTemps, buf)) return null;

        var temps = new BoardTemp[8];
        for (int i = 0; i < 8; i++)
        {
            double c = (short)(buf[2 * i] << 8 | buf[2 * i + 1]) / 10.0;
            temps[i] = new BoardTemp(BoardSensorNames[i], c is > -40 and < 150 ? c : null);
        }
        return temps;
    }
}
