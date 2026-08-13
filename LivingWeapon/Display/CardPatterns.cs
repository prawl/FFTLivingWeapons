using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pre-encoded per-weapon search patterns built once at startup. Each weapon
/// has an Entry for enc=1 (ASCII) and enc=2 (UTF-16LE), holding the encoded
/// Name, Flavor, and the literal "Kills: " and valid 2-char suffix slots.
/// Zero allocation after construction; all lookups are O(1).
/// </summary>
internal sealed class CardPatterns
{
    /// <summary>
    /// An (id, enc) -> (Name, Flavor) pattern pair. Name and Flavor are the
    /// encoded byte sequences CardScanner searches for via ByteScan.FindAll.
    /// </summary>
    internal readonly record struct Entry(int Id, int Enc, byte[] Name, byte[] Flavor);

    private readonly Dictionary<(int id, int enc), Entry> _entries = new();
    private readonly byte[] _killsAscii;
    private readonly byte[] _killsUtf16;
    private readonly List<byte[]> _slotsAscii;
    private readonly List<byte[]> _slotsUtf16;
    private readonly int _maxAnchorLen;

    public IReadOnlyList<Entry> Entries { get; }

    public CardPatterns(IReadOnlyDictionary<int, WeaponMeta> meta)
    {
        var entries = new List<Entry>();

        // Build Name and Flavor patterns for each (id, enc) pair.
        foreach (var kv in meta)
        {
            int id = kv.Key;
            // Coalesce nulls that Newtonsoft can produce from explicit JSON null values.
            string name = kv.Value.Name ?? "";
            string flavor = kv.Value.Flavor ?? "";

            // Skip if name is empty (flavor may be empty).
            if (string.IsNullOrEmpty(name))
                continue;

            // enc = 1 (ASCII)
            byte[] nameAscii = ByteScan.Ascii(name);
            byte[] flavorAscii = ByteScan.Ascii(flavor);
            var e1 = new Entry(id, 1, nameAscii, flavorAscii);
            _entries[(id, 1)] = e1;
            entries.Add(e1);

            // enc = 2 (UTF-16LE)
            byte[] nameUtf16 = ByteScan.Utf16(name);
            byte[] flavorUtf16 = ByteScan.Utf16(flavor);
            var e2 = new Entry(id, 2, nameUtf16, flavorUtf16);
            _entries[(id, 2)] = e2;
            entries.Add(e2);
        }

        Entries = entries.AsReadOnly();

        // MaxAnchorLen: maximum over encoded BYTE lengths (not char counts). UTF-16LE
        // doubles every char, so computing from chars would understate by 2x for that enc.
        foreach (var e in entries)
        {
            _maxAnchorLen = Math.Max(_maxAnchorLen, e.Name.Length);
            _maxAnchorLen = Math.Max(_maxAnchorLen, e.Flavor.Length);
        }

        // Cache the encoded "Kills: " literal (7 chars = 7 for enc=1, 14 for enc=2).
        _killsAscii = ByteScan.Ascii("Kills: ");
        _killsUtf16 = ByteScan.Utf16("Kills: ");

        // Cache the encoded slot values from Tuning.Suffix.
        _slotsAscii = ByteScan.Slots(1, Tuning.Suffix);
        _slotsUtf16 = ByteScan.Slots(2, Tuning.Suffix);
    }

    /// <summary>The encoded literal "Kills: " for the given encoding.</summary>
    public byte[] Kills(int enc) => enc == 1 ? _killsAscii : _killsUtf16;

    /// <summary>The encoded valid 2-char suffix slots for the given encoding.</summary>
    public IReadOnlyList<byte[]> Slots(int enc) => enc == 1 ? _slotsAscii : _slotsUtf16;

    /// <summary>Retrieve an Entry by (id, enc). Returns false if not found.</summary>
    public bool TryGet(int id, int enc, out Entry e)
    {
        return _entries.TryGetValue((id, enc), out e);
    }

    /// <summary>
    /// The maximum BYTE length over all encoded Name and Flavor arrays (both
    /// enc=1 and enc=2). UTF-16LE doubles every char, so UTF-16 patterns are
    /// the largest. Display's startup invariant checks this against the sweep
    /// lookback (via <see cref="FitsLookback"/>) so every anchor + slot fits
    /// the chunk-boundary prefix.
    /// </summary>
    public int MaxAnchorLen => _maxAnchorLen;

    /// <summary>True when the given sweep lookback fits the longest anchor plus the widest
    /// slot (the UTF-16 "Kills: " literal + 4-char counter outweighs the 2-char suffix).
    ///
    /// Reliquary Phase 1 note (docs/RELIQUARY_AC.md, decision 12): this class stays IMMUTABLE
    /// and unaware of EarnedAnchors -- an earned/current/previous composed-line pattern is
    /// enforced (EarnedAnchors.TryEncode) to have the SAME encoded byte length as its weapon's
    /// baked Flavor pattern here, so MaxAnchorLen (computed once, from baked Name/Flavor only)
    /// remains a valid upper bound even once earned lines are registered. See Display.cs's ctor
    /// for where this invariant is exercised at startup, and
    /// DisplayStoryLineTests.FitsLookback_with_earned_patterns_registered for the test.</summary>
    public bool FitsLookback(int lookback)
    {
        int widestSlot = Math.Max(Kills(2).Length + Signatures.KillsMeterSlotChars * 2, 2 * 2);
        return lookback >= _maxAnchorLen + widestSlot;
    }

    /// <summary>True when the given trailing slack (ChunkReader.TrailSlack / DisplaySweep.TrailSlack)
    /// fits the worst-case FORWARD reach a new-layout card needs: the bidirectional attribution
    /// search (CardScanner) can now find a weapon's flavor AFTER its "Kills: " hit (the Reliquary
    /// Phase-2 equip-meter layout, docs/TODO.md), so the trailing slack (not just the leading
    /// Lookback prefix FitsLookback guards) must hold the widest anchor plus the Kills literal
    /// plus the meter slot, all in UTF-16 (the widest encoding), plus the "\n\n" gap between the
    /// slot and the next card's own leading bytes. Mirrors FitsLookback for the forward direction;
    /// see Display's ctor for where this is exercised at startup (log-and-continue, same posture
    /// as FitsLookback: a too-short slack only degrades painting, never crashes).</summary>
    public bool FitsTrailSlack(int trailSlack)
    {
        int worstCase = _maxAnchorLen + Kills(2).Length + Signatures.KillsMeterSlotChars * 2 + 4;
        return trailSlack >= worstCase;
    }
}
