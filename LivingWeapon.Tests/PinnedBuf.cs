using System;
using System.Runtime.InteropServices;

namespace LivingWeapon.Tests;

/// <summary>
/// A GCHandle-pinned byte buffer standing in for a game struct. Mem uses RPM/WPM on
/// our OWN process, so reads/writes against Addr operate on Bytes for real -- no live
/// game needed. Dispose frees the pin, so a using replaces the try/finally h.Free()
/// idiom the suites used to repeat.
/// </summary>
internal sealed class PinnedBuf : IDisposable
{
    public byte[] Bytes { get; }
    public long Addr { get; }
    private GCHandle _handle;

    private PinnedBuf(byte[] bytes)
    {
        Bytes = bytes;
        _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Addr = _handle.AddrOfPinnedObject().ToInt64();
    }

    public static PinnedBuf Of(int size) => new(new byte[size]);

    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
