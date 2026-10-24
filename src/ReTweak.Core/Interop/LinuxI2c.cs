using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ReTweak.Core.Interop;

/// <summary>
/// One device on one of the nvidia driver's I2C buses on Linux, through i2c-dev (/dev/i2c-N). The Linux counterpart of
/// NVAPI's I2C read: each read sends the 1-byte register number, then reads (I2C_RDWR, or an SMBus I2C block read
/// where the bus only does SMBus). It never sends anything else. Needs root and the i2c-dev module.
/// </summary>
public sealed unsafe partial class LinuxI2c : IDisposable // public for the simulation's FindBus check
{
    private const uint I2cFuncs = 0x0705, I2cRdwr = 0x0707, I2cSmbus = 0x0720;
    private const ulong FuncI2c = 0x1, FuncSmbusReadI2cBlock = 0x04000000;
    private const ushort MsgRead = 0x0001;
    private const byte SmbusRead = 1;
    private const uint SmbusI2cBlockData = 8;
    private const int SmbusBlockMax = 32, ORdWr = 2, EAcces = 13, EPerm = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct I2cMsg { public ushort Addr, Flags, Len; public byte* Buf; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RdwrData { public I2cMsg* Msgs; public uint Count; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SmbusData { public byte ReadWrite, Command; public uint Size; public byte* Data; }

    private readonly int _fd;
    private readonly ushort _address;
    private readonly bool _rdwr;
    private bool _disposed;

    private LinuxI2c(int fd, ushort address, bool rdwr) => (_fd, _address, _rdwr) = (fd, address, rdwr);

    /// <summary>
    /// The nvidia driver names each bus "NVIDIA i2c adapter &lt;port&gt; at &lt;bus&gt;:&lt;dev&gt;.&lt;fn&gt;" and puts it under the GPU's
    /// sysfs folder as i2c-N. Returns /dev/i2c-N for <paramref name="port"/>, or null. Only this GPU's buses are looked at.
    /// shortcut: assumes the adapter number is NVAPI's port number (OpenRGB relies on the same); if the chip doesn't
    /// answer on a real card, the diagnostic log lists every bus found, to pick the right one from.
    /// </summary>
    public static string? FindBus(string pciDeviceFolder, int port, out string[] seen)
    {
        var names = new List<string>();
        string? found = null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(pciDeviceFolder, "i2c-*"))
            {
                string name;
                try { name = File.ReadAllText(Path.Combine(dir, "name")).Trim(); }
                catch (IOException) { continue; }
                names.Add($"{Path.GetFileName(dir)}: {name}");
                if (AdapterName().Match(name) is { Success: true } m && int.Parse(m.Groups[1].Value) == port)
                    found = "/dev/" + Path.GetFileName(dir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        seen = [.. names];
        return found;
    }

    [GeneratedRegex(@"^NVIDIA i2c adapter (\d+) at ")]
    private static partial Regex AdapterName();

    /// <summary>Opens <paramref name="devicePath"/> for the 7-bit <paramref name="address"/>. Null on failure, with why.</summary>
    public static LinuxI2c? TryOpen(string devicePath, ushort address, out string? error)
    {
        int fd = open(devicePath, ORdWr);
        if (fd < 0)
        {
            int e = Marshal.GetLastPInvokeError();
            error = e is EAcces or EPerm ? "needs root to read the connector chip"
                  : File.Exists(devicePath) ? $"couldn't open {devicePath} (errno {e})"
                  : $"{devicePath} doesn't exist: load i2c-dev (sudo modprobe i2c-dev)";
            return null;
        }
        ulong funcs = 0;
        if (ioctl(fd, I2cFuncs, &funcs) < 0) funcs = 0;
        bool rdwr = (funcs & FuncI2c) != 0;
        if (!rdwr && (funcs & FuncSmbusReadI2cBlock) == 0)
        {
            close(fd);
            error = $"{devicePath} can't do register reads (functions 0x{funcs:X})";
            return null;
        }
        error = null;
        return new LinuxI2c(fd, address, rdwr);
    }

    /// <summary>Reads <paramref name="buffer"/>.Length bytes from <paramref name="register"/>. False if the bus said no.</summary>
    public bool Read(byte register, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rdwr)
        {
            byte reg = register;
            fixed (byte* data = buffer)
            {
                var msgs = stackalloc I2cMsg[2];
                msgs[0] = new I2cMsg { Addr = _address, Flags = 0, Len = 1, Buf = &reg };
                msgs[1] = new I2cMsg { Addr = _address, Flags = MsgRead, Len = (ushort)buffer.Length, Buf = data };
                var rdwr = new RdwrData { Msgs = msgs, Count = 2 };
                return ioctl(_fd, I2cRdwr, &rdwr) == 2;
            }
        }
        // SMBus I2C block read: block[0] is the length, the data follows.
        if (buffer.Length > SmbusBlockMax) return false;
        var block = stackalloc byte[SmbusBlockMax + 2];
        block[0] = (byte)buffer.Length;
        if (ioctl(_fd, 0x0703 /* I2C_SLAVE */, (void*)_address) < 0) return false;
        var args = new SmbusData { ReadWrite = SmbusRead, Command = register, Size = SmbusI2cBlockData, Data = block };
        if (ioctl(_fd, I2cSmbus, &args) < 0 || block[0] < buffer.Length) return false;
        new ReadOnlySpan<byte>(block + 1, buffer.Length).CopyTo(buffer);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        close(_fd);
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, void* arg);
}
