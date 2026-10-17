using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace LivingWeapon;

/// <summary>
/// Testable core behind the save-edge hooks. The detours read the save header and the extended
/// ids' bag counts on the game thread and hand them here. The load detour also runs BagReplay
/// before publishing its edge, because the game rebuilds its menu templates during the load. File I/O,
/// logging and the second replay run on Engine's tick via <see cref="ExtendedInventory.StepBagSidecar"/>,
/// which drains the two pending slots; a newer edge supersedes an undrained one.
///
/// The key is <c>pt&lt;playTimeSeconds&gt;-&lt;first 12 hex of SHA-1 over the 0xB8 header bytes&gt;</c> with the
/// three save-in-flight marker bytes (<see cref="Offsets.SaveHeaderVolatileOffs"/>) zeroed first. They read
/// 0xFF at a save edge and 0x00 at rest, so an unmasked hash gave one save two keys (seen 2026-08-28).
/// </summary>
internal sealed class SaveEdgeTracker
{
    private readonly object _gate = new();
    private (string Key, Dictionary<int, int> Counts)? _pendingSave;
    private string? _pendingLoad;

    /// <summary>Key of the save the game last serialized or applied (null before either). Logs only.</summary>
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

    /// <summary>The game just serialized a save. Remember its key and the counts at that instant.</summary>
    public void OnSerialized(byte[] header, Dictionary<int, int> counts)
    {
        string key = KeyFromHeader(header);
        lock (_gate) { _pendingSave = (key, new Dictionary<int, int>(counts)); CurrentKey = key; }
    }

    /// <summary>The game just applied a loaded save. Its bag is now the file's (no extended ids).</summary>
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
