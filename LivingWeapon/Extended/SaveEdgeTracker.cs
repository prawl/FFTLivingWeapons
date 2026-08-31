using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace LivingWeapon;

/// <summary>
/// LW-353: the testable core behind the save-edge hooks. On the game's thread the detours read
/// the save struct's header and the extended ids' live bag counts and hand them here; since
/// LW-351 the LOAD detour also runs the bag replay (BagReplay) before it publishes its edge,
/// because the game rebuilds its menu templates inside the load itself. Everything else (file
/// I/O, the log line, and the idempotent second replay) runs on Engine's tick through
/// <see cref="ExtendedInventory.StepBagSidecar"/>, which drains the two pending slots below.
/// One pending slot each: a second edge before the tick drains the first simply supersedes it
/// (the newer state is the truth either way).
///
/// KEY: <c>pt&lt;playTimeSeconds&gt;-&lt;first 12 hex of SHA-1 over the 0xB8 header bytes&gt;</c>,
/// with the three save-in-flight marker bytes (<see cref="Offsets.SaveHeaderVolatileOffs"/>)
/// zeroed first: they read 0xFF at a save edge and 0x00 at rest, so an unmasked hash gave the
/// same save two different keys and no load ever found its own counts (the owner's 2026-08-28
/// session). The rest of the header holds the slot list's metadata (play time, chapter, party
/// names) and the file round-trips it verbatim, so the same save yields the same key when it
/// is later applied.
/// </summary>
internal sealed class SaveEdgeTracker
{
    private readonly object _gate = new();
    private (string Key, Dictionary<int, int> Counts)? _pendingSave;
    private string? _pendingLoad;

    /// <summary>The key of the save the game most recently serialized or applied (null before
    /// either). Diagnostic and for the log lines; the replay never depends on it.</summary>
    public string? CurrentKey { get; private set; }

    public static string KeyFromHeader(byte[] header)
    {
        if (header == null || header.Length < Offsets.SaveHeaderKeyLen)
            throw new ArgumentException("header must be the full key window", nameof(header));
        int pt = Offsets.SaveHeaderPlayTimeOff - Offsets.SaveHeaderKeyOff;
        uint playTime = (uint)(header[pt] | header[pt + 1] << 8 | header[pt + 2] << 16 | header[pt + 3] << 24);
        byte[] masked = header.AsSpan(0, Offsets.SaveHeaderKeyLen).ToArray();
        foreach (int off in Offsets.SaveHeaderVolatileOffs) masked[off] = 0;
        var sha = SHA1.HashData(masked);
        return $"pt{playTime}-{Convert.ToHexString(sha, 0, 6).ToLowerInvariant()}";
    }

    /// <summary>The game just serialized a save: remember its key and the counts at that instant.</summary>
    public void OnSerialized(byte[] header, Dictionary<int, int> counts)
    {
        string key = KeyFromHeader(header);
        lock (_gate) { _pendingSave = (key, new Dictionary<int, int>(counts)); CurrentKey = key; }
    }

    /// <summary>The game just applied a loaded save: its bag is now the file's (no extended ids).</summary>
    public void OnApplied(byte[] header)
    {
        string key = KeyFromHeader(header);
        lock (_gate) { _pendingLoad = key; CurrentKey = key; }
    }

    public bool TryTakePendingSave(out string key, out Dictionary<int, int> counts)
    {
        lock (_gate)
        {
            if (_pendingSave == null) { key = ""; counts = new(); return false; }
            (key, counts) = _pendingSave.Value;
            _pendingSave = null;
            return true;
        }
    }

    public bool TryTakePendingLoad(out string key)
    {
        lock (_gate)
        {
            key = _pendingLoad ?? "";
            bool had = _pendingLoad != null;
            _pendingLoad = null;
            return had;
        }
    }
}
