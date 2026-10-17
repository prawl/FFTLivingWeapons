using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Portable anchor re-find scan core. Like FingerprintGuard.cs and HookLandmark.cs it has
/// no project dependencies, so a sibling mod can copy the file. Game knowledge lives in the adapter
/// that builds an <see cref="AnchorSpec"/>. Not thread-safe: drive <see cref="AnchorScan.Step"/>
/// from one host loop thread.
/// </summary>
internal delegate bool AnchorTryRead(long addr, int len, out byte[] buf);

internal enum AnchorVerdict { Scanning, FoundAtPin, FoundElsewhere, Ambiguous, NotFound }

/// <summary>One anchor's search parameters: signature, base offset from a hit, pinned address,
/// region, alignment filter and optional confirm predicate.</summary>
internal sealed class AnchorSpec
{
    public string Name { get; }
    public byte[] Signature { get; }
    /// <summary>candidate base = hit start address - SignatureOffset.</summary>
    public int SignatureOffset { get; }
    public long PinnedAddress { get; }
    public long RegionLo { get; }
    public long RegionHi { get; }
    /// <summary>1 = no alignment requirement. A misaligned base is rejected before
    /// <see cref="Confirm"/> runs, so it costs no extra reads.</summary>
    public int BaseAlignment { get; }
    /// <summary>Null always confirms. Otherwise the adapter can require a whole-row read at the
    /// candidate base before it counts as a hit.</summary>
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

/// <summary>One spec's incremental scan. <see cref="Step"/> reads one chunk per call so the
/// caller can spread a large region across host-loop ticks.
///
/// No dedup set is needed. Each Step reads <c>[cursor, cursor + chunkBytes + Signature.Length - 1)</c>
/// so a signature straddling a chunk boundary is found, but only matches starting in
/// <c>[cursor, cursor + chunkBytes)</c> count for this chunk. The next chunk owns the overlap
/// tail, so no hit is counted twice or missed.</summary>
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

    /// <summary>Restarts from the region start, clearing bases and the unreadable count.</summary>
    public void Reset()
    {
        _cursor = _spec.RegionLo;
        _bases.Clear();
        UnreadableChunks = 0;
        Verdict = AnchorVerdict.Scanning;
    }

    /// <summary>Scans one chunk. True while region remains; false once Verdict is final
    /// (further calls are no-ops).</summary>
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
        // Clamp the tail at RegionHi so we never read past the region.
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
