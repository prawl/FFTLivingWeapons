using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// BandSweep.ForEachFingerprintHit: the ONE chunked +/-1MB sweep CharmLock and EagleEye share.
/// Contract under test: candidates are matched by AMaxHp u16 then confirmed at the
/// level/brave/faith struct offsets; onHit receives the struct's absolute base address, the
/// chunk buffer, the struct's base index within it, and the matched fingerprint; an empty
/// candidate set short-circuits before any memory read (the cheap-no-enemies path).
/// </summary>
public class BandSweepTests
{
    private const long Lo = Offsets.CombatAnchor - BandSweep.Radius;

    /// <summary>Serves whole chunks by base address (the sweep's TryReadBytes shape) and
    /// counts read attempts; unseeded chunks fail like an unmapped region.</summary>
    private sealed class ChunkFake : IGameMemory
    {
        public readonly Dictionary<long, byte[]> Chunks = new();
        public int Reads;
        public byte U8(long a) => 0;
        public ushort U16(long a) => 0;
        public bool TryReadBytes(long addr, int len, out byte[] buf)
        {
            Reads++;
            buf = new byte[len];
            if (!Chunks.TryGetValue(addr, out var src)) return false;
            Array.Copy(src, buf, Math.Min(len, src.Length));
            return true;
        }
    }

    /// <summary>Plant a struct with the given fingerprint at base index <paramref name="b"/>
    /// of chunk 0 (so AMaxHp lands at b + 0x16).</summary>
    private static ChunkFake WithUnit(int b, (int mhp, int lvl, int br, int fa) fp)
    {
        var m = new ChunkFake();
        var chunk = new byte[BandSweep.Chunk + BandSweep.Overlap];
        chunk[b + Offsets.AMaxHp] = (byte)(fp.mhp & 0xFF);
        chunk[b + Offsets.AMaxHp + 1] = (byte)((fp.mhp >> 8) & 0xFF);
        chunk[b + Offsets.ALevel] = (byte)fp.lvl;
        chunk[b + Offsets.ABrave] = (byte)fp.br;
        chunk[b + Offsets.AFaith] = (byte)fp.fa;
        m.Chunks[Lo] = chunk;
        return m;
    }

    [Fact]
    public void Hit_reports_the_struct_base_address_buffer_index_and_fingerprint()
    {
        var fp = (mhp: 0x1234, lvl: 31, br: 65, fa: 58);
        var m = WithUnit(0x100, fp);
        var hits = new List<(long addr, int baseIdx, (int mhp, int lvl, int br, int fa) fp)>();
        BandSweep.ForEachFingerprintHit(m, new[] { fp }, (addr, buf, b, hit) => hits.Add((addr, b, hit)));
        Assert.Single(hits);
        Assert.Equal(Lo + 0x100, hits[0].addr);
        Assert.Equal(0x100, hits[0].baseIdx);
        Assert.Equal(fp, hits[0].fp);
    }

    [Fact]
    public void Mhp_match_alone_is_not_a_hit_when_the_fingerprint_bytes_disagree()
    {
        var planted = (mhp: 0x1234, lvl: 31, br: 65, fa: 58);
        var sought = (mhp: 0x1234, lvl: 32, br: 65, fa: 58);   // same mhp, different level
        var m = WithUnit(0x100, planted);
        int hits = 0;
        BandSweep.ForEachFingerprintHit(m, new[] { sought }, (_, _, _, _) => hits++);
        Assert.Equal(0, hits);
    }

    [Fact]
    public void Same_mhp_candidates_resolve_to_the_one_whose_bytes_confirm()
    {
        // The byMhp group carries both; only the planted unit's level/brave/faith confirm.
        var planted = (mhp: 0x1234, lvl: 31, br: 65, fa: 58);
        var twinMhp = (mhp: 0x1234, lvl: 40, br: 80, fa: 45);
        var m = WithUnit(0x100, planted);
        var hits = new List<(int mhp, int lvl, int br, int fa)>();
        BandSweep.ForEachFingerprintHit(m, new[] { twinMhp, planted }, (_, _, _, hit) => hits.Add(hit));
        Assert.Single(hits);
        Assert.Equal(planted, hits[0]);
    }

    [Fact]
    public void Empty_candidate_set_short_circuits_before_any_read()
    {
        var m = new ChunkFake();
        BandSweep.ForEachFingerprintHit(m, Array.Empty<(int, int, int, int)>(), (_, _, _, _) => { });
        Assert.Equal(0, m.Reads);   // the 2MB sweep never starts with nothing to find
    }

    [Fact]
    public void Sweep_covers_the_whole_window_in_chunks()
    {
        var m = new ChunkFake();   // no chunks seeded: every read fails, but all are attempted
        BandSweep.ForEachFingerprintHit(m, new[] { (100, 10, 60, 50) }, (_, _, _, _) => { });
        Assert.Equal((int)(BandSweep.Radius * 2 / BandSweep.Chunk), m.Reads);
    }
}
