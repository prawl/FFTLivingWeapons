using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>IGameMemory adapter that remaps the three Display-static game addresses
/// (MirrorWeapon, MirrorOffHand, WpScratch) to caller-specified addresses in the
/// underlying heap, forwarding everything else unchanged. Lets tests exercise the full
/// Display pipeline without requiring a live game process.</summary>
internal sealed class OffsetRemapMem : IGameMemory
{
    private readonly IGameMemory _inner;
    private readonly long _mwAddr, _moAddr, _wsAddr;

    public OffsetRemapMem(IGameMemory inner, long mirrorWeaponAddr, long mirrorOffHandAddr,
                          long wpScratchAddr)
    {
        _inner = inner;
        _mwAddr = mirrorWeaponAddr;
        _moAddr = mirrorOffHandAddr;
        _wsAddr = wpScratchAddr;
    }

    private long Remap(long addr)
    {
        if (addr == Offsets.MirrorWeapon)  return _mwAddr;
        if (addr == Offsets.MirrorOffHand) return _moAddr;
        if (addr == Offsets.WpScratch)     return _wsAddr;
        return addr;
    }

    public byte   U8(long addr)                                    => _inner.U8(Remap(addr));
    public ushort U16(long addr)                                   => _inner.U16(Remap(addr));
    public bool   TryReadBytes(long addr, int len, out byte[] buf) => _inner.TryReadBytes(Remap(addr), len, out buf);
    public int    ReadInto(long addr, byte[] buf, int len)         => _inner.ReadInto(Remap(addr), buf, len);
    public void   WriteBytes(long addr, byte[] data)               => _inner.WriteBytes(Remap(addr), data);
    public void   W8(long addr, byte v)                            => _inner.W8(Remap(addr), v);
    public bool   Readable(long addr, int len)                     => _inner.Readable(Remap(addr), len);
    public bool   Writable(long addr, int len)                     => _inner.Writable(Remap(addr), len);
    public System.Collections.Generic.IEnumerable<(long baseAddr, long size)> Regions()
        => _inner.Regions();
}
