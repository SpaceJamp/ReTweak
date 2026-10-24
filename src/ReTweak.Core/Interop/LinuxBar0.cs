using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ReTweak.Core.Interop;

/// <summary>
/// Read-only window onto a few GPU registers on Linux: the page holding them is mapped from the GPU's PCI
/// resource file (/sys/bus/pci/devices/&lt;address&gt;/resource0, BAR0) with PROT_READ only, so nothing can be
/// written through it. Needs root; kernels built with CONFIG_IO_STRICT_DEVMEM refuse to map a device the nvidia
/// driver owns unless booted with iomem=relaxed. The Linux counterpart of the PawnIO path on Windows.
/// </summary>
internal sealed unsafe partial class LinuxBar0 : IDisposable
{
    private const int ProtRead = 1, MapShared = 1, ORdOnly = 0;
    private const int EPerm = 1, ENoEnt = 2, EAcces = 13, EInval = 22;
    private const long PageMask = 0xFFF;

    private readonly nint _map;
    private readonly nuint _length;
    private readonly long _base;
    private bool _disposed;

    private LinuxBar0(nint map, nuint length, long pageBase) => (_map, _length, _base) = (map, length, pageBase);

    /// <summary>Maps <paramref name="length"/> bytes from <paramref name="offset"/>. Null on failure, with why.</summary>
    public static LinuxBar0? TryOpen(string resourceFile, long offset, int length, out HotspotAvailability failure, out string? error)
    {
        long pageBase = offset & ~PageMask;
        var mapLength = (nuint)((offset - pageBase + length + PageMask) & ~PageMask);
        int fd = open(resourceFile, ORdOnly);
        if (fd < 0)
        {
            int e = Marshal.GetLastPInvokeError();
            (failure, error) = e switch
            {
                EAcces or EPerm => (HotspotAvailability.NeedsAdmin, "needs root to read the GPU's registers"),
                ENoEnt => (HotspotAvailability.Unsupported, $"{resourceFile} doesn't exist"),
                _ => (HotspotAvailability.Failed, $"couldn't open {resourceFile} (errno {e})"),
            };
            return null;
        }
        nint map = mmap(0, mapLength, ProtRead, MapShared, fd, pageBase);
        int mapErrno = Marshal.GetLastPInvokeError();
        close(fd); // the mapping stays valid without the descriptor
        if (map == -1)
        {
            // Lockdown answers EPERM; strict devmem (memory the nvidia driver claimed) answers EINVAL from pci-sysfs.
            (failure, error) = mapErrno is EPerm or EAcces or EInval
                ? (HotspotAvailability.Failed, LockedDown()
                    ? "Secure Boot lockdown blocks reading the GPU's registers from user space"
                    : "the kernel blocks reading the GPU's registers (boot with iomem=relaxed to allow it)")
                : (HotspotAvailability.Failed, $"couldn't map the GPU's registers (errno {mapErrno})");
            return null;
        }
        (failure, error) = (HotspotAvailability.Available, null);
        return new LinuxBar0(map, mapLength, pageBase);
    }

    /// <summary>Kernel lockdown on ("[integrity]" or "[confidentiality]"; Fedora turns it on with Secure Boot) blocks the mapping outright.</summary>
    private static bool LockedDown()
    {
        try { return !File.ReadAllText("/sys/kernel/security/lockdown").Contains("[none]"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>One 32-bit register; must lie inside the mapped range.</summary>
    public uint Read32(long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long at = offset - _base;
        if (at < 0 || at + 4 > (long)_length) throw new ArgumentOutOfRangeException(nameof(offset));
        return Volatile.Read(ref Unsafe.AsRef<uint>((void*)(_map + (nint)at))); // a real 32-bit read of the register
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        munmap(_map, _length);
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
    [LibraryImport("libc", SetLastError = true)]
    private static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(nint addr, nuint length);
}
