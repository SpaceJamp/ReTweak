using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ReTweak.Core.Interop;

/// <summary>
/// Minimal client for the PawnIO driver (https://pawnio.eu/): opens the device, loads one
/// signed module and calls its functions. Talks to the device directly, as
/// LibreHardwareMonitor does, so PawnIOLib.dll isn't needed. Opening the device needs
/// administrator rights.
/// </summary>
internal sealed unsafe partial class PawnIo : IDisposable
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821 << 2);
    private const uint IoctlExecute = DeviceType | (0x841 << 2);
    private const int FunctionNameLength = 32;

    private readonly SafeFileHandle _handle;

    private PawnIo(SafeFileHandle handle) => _handle = handle;

    /// <summary>Opens PawnIO and loads <paramref name="module"/>. Null on failure, with a reason for the user.</summary>
    public static PawnIo? TryOpen(ReadOnlySpan<byte> module, out HotspotAvailability failure, out string? error)
    {
        var handle = CreateFileW(DevicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            int win32 = Marshal.GetLastPInvokeError();
            handle.Dispose();
            (failure, error) = win32 switch
            {
                2 or 3 => (HotspotAvailability.PawnIoMissing, "PawnIO isn't installed"),
                5 => (HotspotAvailability.NeedsAdmin, "needs administrator rights"),
                _ => (HotspotAvailability.Failed, $"couldn't open PawnIO (error {win32})"),
            };
            return null;
        }

        fixed (byte* blob = module)
        {
            if (!DeviceIoControl(handle, IoctlLoadBinary, blob, (uint)module.Length, null, 0, out _, 0))
            {
                int win32 = Marshal.GetLastPInvokeError();
                handle.Dispose();
                (failure, error) = (HotspotAvailability.Failed, $"PawnIO refused the module (error {win32}); update PawnIO");
                return null;
            }
        }
        (failure, error) = (HotspotAvailability.Available, null);
        return new PawnIo(handle);
    }

    /// <summary>Calls a module function. Returns the cells it wrote, or null if the call failed.</summary>
    public long[]? Execute(string name, ReadOnlySpan<long> input, int outputCells)
    {
        if (name.Length >= FunctionNameLength) throw new ArgumentException("Function name too long.", nameof(name));
        var request = new byte[FunctionNameLength + input.Length * sizeof(long)];
        Encoding.ASCII.GetBytes(name, request);
        MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(FunctionNameLength));
        var output = new long[outputCells];

        fixed (byte* pIn = request)
        fixed (long* pOut = output)
        {
            if (!DeviceIoControl(_handle, IoctlExecute, pIn, (uint)request.Length, pOut, (uint)(outputCells * sizeof(long)), out uint returned, 0))
                return null;
            return returned / sizeof(long) == outputCells ? output : output[..(int)(returned / sizeof(long))];
        }
    }

    public void Dispose() => _handle.Dispose();

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, uint inSize,
                                                void* outBuffer, uint outSize, out uint returned, nint overlapped);
}
