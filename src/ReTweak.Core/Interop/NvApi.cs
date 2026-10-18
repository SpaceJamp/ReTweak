using System.Runtime.InteropServices;

namespace ReTweak.Core.Interop;

/// <summary>
/// The small slice of NVAPI needed for I2C reads. Functions are resolved by ID
/// through nvapi_QueryInterface, the same way GPU Tweak III does it.
/// </summary>
internal sealed unsafe class NvApi
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetBusId = 0x1BE0B8E5;
    private const uint IdI2CReadEx = 0x4D7B0709;

    public enum I2CSpeed : uint { Default = 0, Khz3 = 1, Khz10 = 2, Khz33 = 3, Khz100 = 4, Khz200 = 5, Khz400 = 6 }

    [StructLayout(LayoutKind.Sequential)]
    private struct I2CInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DevAddress;     // 8-bit form (7-bit address << 1)
        public byte* RegAddress;
        public uint RegAddrSize;
        public byte* Data;
        public uint DataSize;
        public uint SpeedDeprecated;
        public I2CSpeed SpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    private readonly delegate* unmanaged[Cdecl]<nint*, uint*, int> _enumGpus;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getBusId;
    private readonly delegate* unmanaged[Cdecl]<nint, I2CInfoV3*, uint*, int> _i2cReadEx;

    private NvApi(nint lib)
    {
        var qi = (delegate* unmanaged[Cdecl]<uint, nint>)NativeLibrary.GetExport(lib, "nvapi_QueryInterface");
        nint Resolve(uint id) => qi(id) is var p && p != 0 ? p : throw new NvApiException($"NVAPI function 0x{id:X8} unavailable");

        var init = (delegate* unmanaged[Cdecl]<int>)Resolve(IdInitialize);
        if (init() is var rc && rc != 0) throw new NvApiException($"NvAPI_Initialize failed ({rc})");
        _enumGpus = (delegate* unmanaged[Cdecl]<nint*, uint*, int>)Resolve(IdEnumPhysicalGpus);
        _getBusId = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Resolve(IdGetBusId);
        _i2cReadEx = (delegate* unmanaged[Cdecl]<nint, I2CInfoV3*, uint*, int>)Resolve(IdI2CReadEx);
    }

    /// <summary>Loads NVAPI, or returns null when no NVIDIA driver is present.</summary>
    public static NvApi? TryLoad()
    {
        string name = Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll";
        if (!NativeLibrary.TryLoad(name, out var lib)) return null;
        try { return new NvApi(lib); }
        catch (NvApiException) { return null; }
    }

    /// <summary>Physical GPU handles keyed by PCI bus number.</summary>
    public Dictionary<uint, nint> GpusByBus()
    {
        var handles = stackalloc nint[64];
        uint count;
        if (_enumGpus(handles, &count) != 0) return [];
        var result = new Dictionary<uint, nint>();
        for (int i = 0; i < count; i++)
        {
            uint bus;
            if (_getBusId(handles[i], &bus) == 0) result[bus] = handles[i];
        }
        return result;
    }

    /// <summary>Reads <paramref name="buffer"/>.Length bytes starting at a 1-byte register.</summary>
    public int I2CRead(nint gpu, byte port, byte devAddress8, byte register, Span<byte> buffer, I2CSpeed speed = I2CSpeed.Khz100)
    {
        byte reg = register;
        fixed (byte* data = buffer)
        {
            var info = new I2CInfoV3
            {
                Version = (uint)sizeof(I2CInfoV3) | (3u << 16),
                DevAddress = devAddress8,
                RegAddress = &reg,
                RegAddrSize = 1,
                Data = data,
                DataSize = (uint)buffer.Length,
                SpeedDeprecated = 0xFFFF,
                SpeedKhz = speed,
                PortId = port,
                IsPortIdSet = 1,
            };
            uint unknown = 1; // matches GPU Tweak III
            return _i2cReadEx(gpu, &info, &unknown);
        }
    }
}

public sealed class NvApiException(string message) : Exception(message);
