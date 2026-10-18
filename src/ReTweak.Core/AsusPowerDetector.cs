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
    private const ushort MinVersion = 0x500; // same gate GPU Tweak III uses
    private const double MaxSaneAmps = 50;   // GPU Tweak III treats more as a bad read
    // A running card can't have a pin outside this range; 0x0000 / 0xFFFF frames are bus glitches.
    private const double MinSaneVolts = 6, MaxSaneVolts = 15;

    private readonly NvApi _nvapi;
    private readonly nint _gpu;

    public ushort Version { get; }

    private AsusPowerDetector(NvApi nvapi, nint gpu, ushort version)
    {
        _nvapi = nvapi;
        _gpu = gpu;
        Version = version;
    }

    /// <summary>Returns a detector if the chip answers with a supported version.</summary>
    public static AsusPowerDetector? TryCreate(NvApi nvapi, nint gpu)
    {
        Span<byte> lo = stackalloc byte[1], hi = stackalloc byte[1];
        if (nvapi.I2CRead(gpu, Port, Address, RegIdLow, lo) != 0) return null;
        if (nvapi.I2CRead(gpu, Port, Address, RegIdHigh, hi) != 0) return null;
        ushort version = (ushort)(hi[0] << 8 | lo[0]);
        return version >= MinVersion ? new AsusPowerDetector(nvapi, gpu, version) : null;
    }

    public ConnectorReading? Read()
    {
        Span<byte> buf = stackalloc byte[24];
        if (_nvapi.I2CRead(_gpu, Port, Address, RegPins, buf) != 0) return null;

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
}
