using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Portable anchor re-find scan core (LW-82). COPY-FILE PORTABILITY CONTRACT (the
/// FingerprintGuard.cs / HookLandmark.cs pattern): this file has zero project dependencies (no
/// Offsets, no ModLogger, no IGameMemory, no Mem, no Flight, no Reloaded types, no Barrage, no
/// LaunchGuard), so a sibling mod adopts the mechanism by copying this one file, not by
/// referencing a shared library. All game knowledge (which bytes, which addresses, what a hit
/// means) lives in the adapter that builds an <see cref="AnchorSpec"/>; this core only knows how
/// to walk a byte region looking for one.
///
/// SINGLE-THREAD CONTRACT (mirrors FingerprintGuard.Step): an <see cref="AnchorScan"/> instance is
/// meant to be driven by <see cref="AnchorScan.Step"/> from one host loop thread only. It keeps no
/// locks and offers no thread-safety guarantee beyond that.
/// </summary>
internal delegate bool AnchorTryRead(long addr, int len, out byte[] buf);

internal enum AnchorVerdict { Scanning, FoundAtPin, FoundElsewhere, Ambiguous, NotFound }

/// <summary>One anchor's search parameters: the byte signature to scan a region for, where the
/// anchor's own base sits relative to a signature hit, its previously known (pinned) address, the
/// region to search, an alignment pre-filter, and an optional confirm predicate.</summary>
internal sealed class AnchorSpec
{
    public string Name { get; }
    public byte[] Signature { get; }
    /// <summary>candidate base = hit start address - SignatureOffset.</summary>
    public int SignatureOffset { get; }
    public long PinnedAddress { get; }
    public long RegionLo { get; }
    public long RegionHi { get; }
    /// <summary>1 = no alignment requirement. A candidate base failing
    /// <c>base % BaseAlignment != 0</c> is rejected BEFORE <see cref="Confirm"/> ever runs, so a
    /// misaligned hit costs zero reads beyond the signature scan itself.</summary>
    public int BaseAlignment { get; }
    /// <summary>Null always confirms (no check beyond the signature match and the alignment
    /// filter). Non-null lets the adapter demand a further whole-row read at the candidate base
    /// before it counts as a hit.</summary>
    public Func<long, bool>? Confirm { get; }

    public AnchorSpec(string name, byte[] signature, int signatureOffset, long pinnedAddress,
        long regionLo, long regionHi, int baseAlignment = 1, Func<long, bool>? confirm = null)
    {
        Name = name;
        Signature = signature;
        SignatureOffset = signatureOffset;
        PinnedAddress = pinnedAddress;
        RegionLo = regionLo;
        RegionHi = regionHi;
        BaseAlignment = baseAlignment;
        Confirm = confirm;
    }
}

/// <summary>One spec's incremental scan: <see cref="Step"/> reads one chunk at a time so a caller
/// can budget scan work across many host-loop ticks instead of blocking on a multi-megabyte region
/// scan in a single call.
///
/// NO DEDUP SET (deliberate; a prior "overlap doubles a hit" draft was falsified by this
/// arithmetic): each Step reads <c>[cursor, cursor + chunkBytes + Signature.Length - 1)</c> so a
/// signature straddling the chunk boundary is still found (the tail overlap), then advances the
/// cursor by exactly <c>chunkBytes</c>. A match is only ATTRIBUTED to the current chunk when its
/// start position falls in <c>[cursor, cursor + chunkBytes)</c>: a match starting in the overlap
/// tail belongs to the NEXT chunk's own window and is skipped here, where it cannot be found again
/// (the next chunk's read starts exactly at that boundary, past the match's start). Consecutive
/// chunks' attributed match-start ranges are therefore disjoint by construction: no hit is ever
/// counted twice, and none is missed at a boundary.</summary>
internal sealed class AnchorScan
{
    private readonly AnchorSpec _spec;
    private readonly AnchorTryRead _tryRead;
    private readonly int _chunkBytes;
    private long _cursor;
    private readonly List<long> _bases = new();

    public AnchorScan(AnchorSpec spec, AnchorTryRead tryRead, int chunkBytes)
    {
        _spec = spec;
        _tryRead = tryRead;
        _chunkBytes = chunkBytes;
        Reset();
    }

    public AnchorVerdict Verdict { get; private set; } = AnchorVerdict.Scanning;
    public IReadOnlyList<long> Bases => _bases;
    public int UnreadableChunks { get; private set; }

    /// <summary>Re-arms the scan from the region's start: clears every collected base, the
    /// unreadable-chunk count, and returns Verdict to Scanning.</summary>
    public void Reset()
    {
        _cursor = _spec.RegionLo;
        _bases.Clear();
        UnreadableChunks = 0;
        Verdict = AnchorVerdict.Scanning;
    }

    /// <summary>Scans one chunk. Returns true while region remains to scan (Verdict stays
    /// Scanning); returns false once the region is exhausted, at which point Verdict is final.
    /// A call after Verdict is already final is a no-op that returns false.</summary>
    public bool Step()
    {
        if (Verdict != AnchorVerdict.Scanning) return false;

        long remaining = _spec.RegionHi - _cursor;
        if (remaining <= 0)
        {
            Conclude();
            return false;
        }

        int sigLen = _spec.Signature.Length;
        // Tail CLAMPED at RegionHi: never read (or even ask to read) past the region's end.
        int readLen = (int)Math.Min(_chunkBytes + sigLen - 1, remaining);
        long attributedEnd = Math.Min(_cursor + _chunkBytes, _spec.RegionHi);

        if (_tryRead(_cursor, readLen, out byte[] buf) && buf.Length >= readLen)
        {
            for (int i = 0; i + sigLen <= buf.Length; i++)
            {
                long matchStart = _cursor + i;
                if (matchStart >= attributedEnd) break;   // belongs to the next chunk's own window
                if (!SignatureMatchesAt(buf, i, sigLen)) continue;

                long baseAddr = matchStart - _spec.SignatureOffset;
                if (_spec.BaseAlignment > 1 && baseAddr % _spec.BaseAlignment != 0) continue;
                if (_spec.Confirm != null && !_spec.Confirm(baseAddr)) continue;
                _bases.Add(baseAddr);
            }
        }
        else
        {
            UnreadableChunks++;
        }

        _cursor += _chunkBytes;
        if (_cursor >= _spec.RegionHi)
        {
            Conclude();
            return false;
        }
        return true;
    }

    private bool SignatureMatchesAt(byte[] buf, int offset, int sigLen)
    {
        for (int k = 0; k < sigLen; k++)
            if (buf[offset + k] != _spec.Signature[k]) return false;
        return true;
    }

    private void Conclude()
    {
        Verdict = _bases.Count switch
        {
            0 => AnchorVerdict.NotFound,
            1 => _bases[0] == _spec.PinnedAddress ? AnchorVerdict.FoundAtPin : AnchorVerdict.FoundElsewhere,
            _ => AnchorVerdict.Ambiguous,
        };
    }
}
