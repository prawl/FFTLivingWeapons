using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// IGameMemory wrapper that sums every ReadInto call's returned length -- ReadInto is the single
/// read funnel ChunkReader (hence PoolScan) uses, so this is the one place to pin "never reads
/// more than the budget plus one chunk" without caring how many individual chunk reads it took.
/// Forwards everything else, mirroring the RegionsSpyMem idiom already used across the Display
/// test files (PoolLocatorTests.cs, DisplayPoolPaintTests.cs, DisplayHeartbeatTests.cs).
/// </summary>
internal sealed class BytesSpyMem : IGameMemory
{
    private readonly IGameMemory _inner;
    public long TotalBytesRead { get; private set; }

    public BytesSpyMem(IGameMemory inner) => _inner = inner;

    public byte U8(long addr) => _inner.U8(addr);
    public ushort U16(long addr) => _inner.U16(addr);
    public bool TryReadBytes(long addr, int len, out byte[] buf) => _inner.TryReadBytes(addr, len, out buf);
    public int ReadInto(long addr, byte[] buf, int len)
    {
        int got = _inner.ReadInto(addr, buf, len);
        if (got > 0) TotalBytesRead += got;
        return got;
    }
    public void WriteBytes(long addr, byte[] data) => _inner.WriteBytes(addr, data);
    public void W8(long addr, byte value) => _inner.W8(addr, value);
    public bool Readable(long addr, int len) => _inner.Readable(addr, len);
    public bool Writable(long addr, int len) => _inner.Writable(addr, len);
    public IEnumerable<(long baseAddr, long size)> Regions() => _inner.Regions();
}
