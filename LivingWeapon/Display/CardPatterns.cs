using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Per-weapon search patterns encoded once at startup, for enc=1 (ASCII) and enc=2 (UTF-16LE): Name,
/// Flavor and Grows, plus the "Kills: " literal and the valid 2-char suffix slots.
/// </summary>
internal sealed class CardPatterns
{
    /// <summary>
    /// An (id, enc) pattern set that CardScanner searches via ByteScan.FindAll. Grows is the
    /// baked "Grows: ..." line, empty for ids with no GrowsLine.
    /// </summary>
    internal readonly record struct Entry(int Id, int Enc, byte[] Name, byte[] Flavor, byte[] Grows);

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

        // Name, Flavor and Grows patterns for each (id, enc).
        foreach (var kv in meta)
        {
            int id = kv.Key;
            // Newtonsoft can produce nulls from explicit JSON nulls.
            string name = kv.Value.Name ?? "";
            string flavor = kv.Value.Flavor ?? "";
            string grows = kv.Value.GrowsLine ?? "";

            // Flavor and grows may be empty; name may not.
            if (string.IsNullOrEmpty(name))
                continue;

            // enc = 1 (ASCII)
            byte[] nameAscii = ByteScan.Ascii(name);
            byte[] flavorAscii = ByteScan.Ascii(flavor);
            byte[] growsAscii = ByteScan.Ascii(grows);
            var e1 = new Entry(id, 1, nameAscii, flavorAscii, growsAscii);
            _entries[(id, 1)] = e1;
            entries.Add(e1);

            // enc = 2 (UTF-16LE)
            byte[] nameUtf16 = ByteScan.Utf16(name);
            byte[] flavorUtf16 = ByteScan.Utf16(flavor);
            byte[] growsUtf16 = ByteScan.Utf16(grows);
            var e2 = new Entry(id, 2, nameUtf16, flavorUtf16, growsUtf16);
            _entries[(id, 2)] = e2;
            entries.Add(e2);
        }

        Entries = entries.AsReadOnly();

        // Max over encoded byte lengths, since UTF-16LE doubles the char count. Grows counts too,
        // as it is searched like Flavor (CardScanner.AnchorCandidates).
        foreach (var e in entries)
        {
            _maxAnchorLen = Math.Max(_maxAnchorLen, e.Name.Length);
            _maxAnchorLen = Math.Max(_maxAnchorLen, e.Flavor.Length);
            _maxAnchorLen = Math.Max(_maxAnchorLen, e.Grows.Length);
        }

        // Encoded "Kills: " literal.
        _killsAscii = ByteScan.Ascii("Kills: ");
        _killsUtf16 = ByteScan.Utf16("Kills: ");

        // Encoded suffix slots from Tuning.Suffix.
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
    /// Max byte length over every encoded Name, Flavor and Grows array, both encodings.
    /// Display's startup check compares it to the sweep lookback via <see cref="FitsLookback"/>.
    /// </summary>
    public int MaxAnchorLen => _maxAnchorLen;

    /// <summary>True when the lookback fits the longest anchor plus the widest slot (the UTF-16
    /// "Kills: " literal plus a 4-char counter). This class stays immutable and unaware of EarnedAnchors
    /// (docs/RELIQUARY_AC.md, decision 12) because EarnedAnchors.TryEncode forces each earned pattern to
    /// the byte length of its baked Flavor pattern, so MaxAnchorLen stays a valid bound. Display.cs's ctor
    /// checks it at startup; see DisplayStoryLineTests.FitsLookback_with_earned_patterns_registered.</summary>
    public bool FitsLookback(int lookback)
    {
        int widestSlot = Math.Max(Kills(2).Length + Signatures.KillsMeterSlotChars * 2, 2 * 2);
        return lookback >= _maxAnchorLen + widestSlot;
    }

    /// <summary>True when the trailing slack (ChunkReader.TrailSlack / DisplaySweep.TrailSlack) covers the
    /// forward reach of a new-layout card, where the bidirectional search in CardScanner can find the flavor
    /// (or the LW-332 Grows line) after "Kills: " (the equip-meter layout, docs/TODO.md). That is the widest
    /// anchor plus the Kills literal plus the meter slot in UTF-16, plus the "\n\n" gap to the next card.
    /// A short slack only degrades painting, so Display's ctor logs and continues.</summary>
    public bool FitsTrailSlack(int trailSlack)
    {
        int worstCase = _maxAnchorLen + Kills(2).Length + Signatures.KillsMeterSlotChars * 2 + 4;
        return trailSlack >= worstCase;
    }
}
