using System.Collections.Generic;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure half of the TurnOwnerSpike passive recorder (LW-31 stage 2's live-pass blocker,
/// docs/TODO.md Now entry). Everything here is pure string/byte/int logic, always compiled and
/// unit-tested; the #if LWDEV shell that drives the actual memory reads and Trace logging is
/// TurnOwnerSpike.cs.
///
/// PURPOSE: the attack-card dossier (AttackCard.cs) shows the PREVIOUS actor's weapon at menu
/// time, because the ActorRegister only names a unit once it acts, and the player opens the
/// Abilities menu before acting (docs/TODO.md's STAGE 2 LIVE PASS note, 2026-07-05). The fix
/// needs a LEADING turn-owner signal readable at menu time, and two candidates exist, neither
/// proven live yet:
///
/// HYPOTHESIS 1, the scheduler CT: each band unit's CT byte (Offsets.ACtSlam, band-entry +0x25,
/// the same field CharmLock/Maim/ExtraTurn already read or hold) should sit at the ceiling for
/// exactly the turn owner while their own menu is open. Offsets.cs already flags this as
/// unreliable on a player's OWN actively-managed unit in at least one prior probe (clean 100 in
/// one reading, stale ~85 in another), which is exactly why a full recorded battle is needed
/// instead of another spot check.
///
/// HYPOTHESIS 2, the cursor-follower struct: the condensed active-unit struct at
/// Offsets.TurnQueue is a known trap (it follows the CURSOR, not the turn owner). During a
/// unit's own Abilities menu, though, the cursor sits on that unit by construction, which may
/// make this struct accidentally correct at exactly the moment the dossier needs it.
///
/// CORRELATION METHOD: TurnOwnerSpike records both candidate tapes plus the ActorRegister
/// baseline (LastPlayerNameId/LastPlayerArrivalTick/Trusted, the same fields AttackCard already
/// trusts) on every real change, into the same livingweapon.log file the painter already writes
/// its own "attack-card desc repainted" line to (AttackCard.Paint.cs, Debug tier, file-always).
/// Lining these tapes up by timestamp marks each menu-hover moment, so a human reviewer can read
/// whether either candidate's value was already correct BEFORE the register caught up. One
/// recorded battle then adjudicates offline; this class only supplies the pure comparisons and
/// formatting the spike's tick loop drives.
/// </summary>
internal static class TurnOwnerProbe
{
    /// <summary>True when <paramref name="current"/> differs from <paramref name="last"/>, or
    /// <paramref name="last"/> is null: the first sample always logs (there is no prior value to
    /// compare against, so a fresh instrument must announce its baseline).</summary>
    internal static bool Changed(byte[]? last, byte[] current)
    {
        if (last is null) return true;
        if (last.Length != current.Length) return true;
        for (int i = 0; i < last.Length; i++)
            if (last[i] != current[i]) return true;
        return false;
    }

    /// <summary>String form of the same gate (the CT snapshot line, the register snapshot line):
    /// first sample always logs, an identical string suppresses, any difference logs.</summary>
    internal static bool Changed(string? last, string current) => last is null || last != current;

    /// <summary>Sampling throttle: true when at least 1000/<paramref name="samplesPerSecond"/> ms
    /// have elapsed since <paramref name="lastSampleMs"/>, or this is the very first sample
    /// (<paramref name="lastSampleMs"/> is null). The boundary is inclusive: a read landing
    /// exactly on the interval samples.</summary>
    internal static bool ShouldSample(long nowMs, long? lastSampleMs, int samplesPerSecond)
        => lastSampleMs is null || nowMs - lastSampleMs.Value >= 1000L / samplesPerSecond;

    /// <summary>One compact one-line snapshot of every valid band slot's identity and CT, plus
    /// the single global acted byte (Offsets.Acted has no per-slot analog: it is one engine
    /// global, not a band-entry field, so it is repeated against every slot's entry here purely
    /// so a reader can eyeball CT-versus-acted for the same tick without cross-referencing a
    /// second log line). Per slot, four turn-owner candidate fields are logged: slam (ACtSlam
    /// 0x25, hypothesis 1), turn (ACtTurn 0x09, hypothesis 3: the per-unit completed-turn CT),
    /// k (AArec kind, hypothesis 4: 5=performing/6=receiving), idx (AArec owner seat, == seat-8).
    /// Format: "turn-owner-probe: ct slots=[s0:lvl/br/fa slam=NN turn=NN k=N idx=N acted=N,
    /// ...]".</summary>
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

    /// <summary>Hex-plus-printable-gloss dump of the cursor-follower struct's raw bytes (same
    /// shape as HeaderProbeText/AttackCardProbeText's context dump), prefixed with a gloss of the
    /// known TurnQueue fields (level/team/nameId/hp/maxHp) so a reader does not have to
    /// hand-decode the hex. Never throws: a buffer shorter than the fields it needs reports a
    /// short read instead.</summary>
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

    /// <summary>One-line snapshot of the ActorRegister fields this instrument watches for change:
    /// LastPlayerNameId, LastPlayerArrivalTick, and Trusted, the same trio AttackCard already
    /// consults for its own turn-owner resolve.</summary>
    internal static string FormatRegisterSnapshot(ushort lastPlayerNameId, int lastPlayerArrivalTick, bool trusted)
        => $"turn-owner-probe: register nameId={lastPlayerNameId} arrivalTick={lastPlayerArrivalTick} trusted={trusted}";
}
