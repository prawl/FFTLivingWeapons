using System.Collections.Generic;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure helpers for the TurnOwnerSpike passive recorder. It was built for the Attack-menu painter, which
/// shipped 2b2f5b4 on 2026-07-07. It still ticks in Engine as a
/// correlation recorder, and its only likely consumer is the turn-order rebuild in the backlog;
/// if that declines, unwire it. The #if LWDEV shell that does the memory reads and Trace logging
/// is TurnOwnerSpike.cs.
///
/// The attack-card dossier (AttackCard.cs) shows the previous actor's weapon at menu time, because
/// the ActorRegister only names a unit once it acts (docs/TODO.md STAGE 2 LIVE PASS note,
/// 2026-07-05). A turn-owner signal readable at menu time is needed. Two candidates, both untested:
///
/// Hypothesis 1, scheduler CT. Offsets.ACtSlam (band-entry +0x25) should sit at the ceiling for the
/// turn owner while their menu is open. Offsets.cs notes it was clean 100 in one probe and stale
/// ~85 in another on a player's own unit, hence a full recorded battle.
///
/// Hypothesis 2, cursor-follower struct. The condensed struct at Offsets.TurnQueue follows the
/// cursor, not the turn owner, but during a unit's own Abilities menu the cursor is on that unit.
///
/// TurnOwnerSpike logs both tapes and the ActorRegister baseline (LastPlayerNameId,
/// LastPlayerArrivalTick, Trusted) on change to livingweapon.log, next to the "attack-card desc
/// repainted" line (AttackCard.Paint.cs). Timestamps show whether either candidate was right before
/// the register caught up.
/// </summary>
internal static class TurnOwnerProbe
{
    /// <summary>True when <paramref name="current"/> differs from <paramref name="last"/> or
    /// <paramref name="last"/> is null, so the first sample logs a baseline.</summary>
    internal static bool Changed(byte[]? last, byte[] current)
    {
        if (last is null) return true;
        if (last.Length != current.Length) return true;
        for (int i = 0; i < last.Length; i++)
            if (last[i] != current[i]) return true;
        return false;
    }

    /// <summary>String form of the same change gate.</summary>
    internal static bool Changed(string? last, string current) => last is null || last != current;

    /// <summary>Sampling throttle. True when 1000/<paramref name="samplesPerSecond"/> ms have elapsed
    /// since <paramref name="lastSampleMs"/> (inclusive) or it is null.</summary>
    internal static bool ShouldSample(long nowMs, long? lastSampleMs, int samplesPerSecond)
        => lastSampleMs is null || nowMs - lastSampleMs.Value >= 1000L / samplesPerSecond;

    /// <summary>One-line snapshot of every valid band slot's identity and candidate fields. The acted
    /// byte (Offsets.Acted) is a single engine global, repeated on each slot for easy comparison.
    /// Per slot. Slam (ACtSlam 0x25, hypothesis 1), turn (ACtTurn 0x09, hypothesis 3, the per-unit
    /// completed-turn CT), k (AArec kind, hypothesis 4, 5=performing/6=receiving), idx (AArec owner
    /// seat, == seat-8). Format "turn-owner-probe. Ct slots=[s0:lvl/br/fa slam=NN turn=NN k=N idx=N
    /// acted=N, ...]".</summary>
    internal static string FormatCtSnapshot(
        IReadOnlyList<(int slot, int lvl, int br, int fa, int slam, int turn, int kind, int idx)> slots, int acted)
    {
        var sb = new StringBuilder("turn-owner-probe: ct slots=[");
        for (int i = 0; i < slots.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var e = slots[i];
            sb.Append('s').Append(e.slot).Append(':').Append(e.lvl).Append('/').Append(e.br).Append('/').Append(e.fa)
              .Append(" slam=").Append(e.slam).Append(" turn=").Append(e.turn)
              .Append(" k=").Append(e.kind).Append(" idx=").Append(e.idx).Append(" acted=").Append(acted);
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Hex-plus-printable dump of the cursor-follower struct (same shape as
    /// HeaderProbeText/AttackCardProbeText), prefixed with the decoded TurnQueue fields
    /// (level/team/nameId/hp/maxHp). A buffer too short for them reports a short read.</summary>
    internal static string FormatCursorDump(byte[] buffer)
    {
        const int MinLen = 0x12;   // past TqMaxHp (0x10, 2 bytes)
        if (buffer == null || buffer.Length < MinLen) return "(short read)";

        int lvl = buffer[Offsets.TqLevel] | (buffer[Offsets.TqLevel + 1] << 8);
        int team = buffer[Offsets.TqTeam] | (buffer[Offsets.TqTeam + 1] << 8);
        int nameId = buffer[Offsets.TqNameId] | (buffer[Offsets.TqNameId + 1] << 8);
        int hp = buffer[Offsets.TqHp] | (buffer[Offsets.TqHp + 1] << 8);
        int maxHp = buffer[Offsets.TqMaxHp] | (buffer[Offsets.TqMaxHp + 1] << 8);
        return $"turn-owner-probe: cursor lvl={lvl} team={team} nameId={nameId} hp={hp}/{maxHp}; " + Dump(buffer);
    }

    private static string Dump(byte[] buffer)
    {
        var hex = new StringBuilder(buffer.Length * 3);
        var gloss = new StringBuilder(buffer.Length);
        for (int i = 0; i < buffer.Length; i++)
        {
            byte b = buffer[i];
            if (i > 0) hex.Append(' ');
            hex.Append(b.ToString("X2"));
            gloss.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return hex + " [" + gloss + "]";
    }

    /// <summary>One-line snapshot of the ActorRegister fields AttackCard also reads for its
    /// turn-owner resolve. LastPlayerNameId, LastPlayerArrivalTick, Trusted.</summary>
    internal static string FormatRegisterSnapshot(ushort lastPlayerNameId, int lastPlayerArrivalTick, bool trusted)
        => $"turn-owner-probe: register nameId={lastPlayerNameId} arrivalTick={lastPlayerArrivalTick} trusted={trusted}";
}
