using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>One single-byte code patch: the address, the byte the vanilla 1.5.2 build carries
/// there (verified before every write), the byte we want, and a label for the log.</summary>
internal readonly record struct BytePatch(long Addr, byte Old, byte New, string Label);

/// <summary>
/// LW-346: applies a whole set of single-byte code patches as one transaction. Two passes: first
/// EVERY site is read and must carry its expected old byte (a single disagreement refuses the
/// whole set with nothing written: the game moved, or another mod got there first); then every
/// site is written, and a refused write rolls back the ones already applied, in reverse order.
/// The rig applied and rolled back site by site (CapBreakBootArm); verify-all-first leaves fewer
/// half-states for the log to explain.
/// </summary>
internal sealed class BytePatchSet
{
    private readonly List<BytePatch> _applied = new();

    public int AppliedCount => _applied.Count;

    /// <summary>Null on success, else the refusal (nothing left applied).</summary>
    public string? Apply(ICodePatcher patcher, IReadOnlyList<BytePatch> patches)
    {
        if (_applied.Count > 0) return null;   // idempotent: already applied
        foreach (var p in patches)
        {
            if (!patcher.TryRead(p.Addr, 1, out var cur))
                return $"{p.Label}: 0x{p.Addr:X} is unreadable";
            if (cur[0] != p.Old)
                return $"{p.Label}: 0x{p.Addr:X} reads {cur[0]:X2}, expected {p.Old:X2}";
        }
        foreach (var p in patches)
        {
            if (!patcher.TryWrite(p.Addr, new[] { p.New }))
            {
                int landed = _applied.Count;
                Rollback(patcher);
                return $"{p.Label}: write refused at 0x{p.Addr:X} ({landed} earlier patch(es) rolled back)";
            }
            _applied.Add(p);
        }
        return null;
    }

    /// <summary>Restores every applied site's old byte, newest first. Idempotent.</summary>
    public void Rollback(ICodePatcher patcher)
    {
        for (int i = _applied.Count - 1; i >= 0; i--)
            patcher.TryWrite(_applied[i].Addr, new[] { _applied[i].Old });
        _applied.Clear();
    }
}

/// <summary>
/// LW-346: a patch that cannot land at boot because its page is still copy-protected then (the
/// two damage-path caps, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md blueprint piece 2): stepped
/// from the tick loop, it waits until the byte reads as VANILLA, writes once, and verifies. A
/// byte that reads as already-patched (the research marker still armed beside this mod) counts
/// as done; any other value is foreign and the patch gives up for the session with one warning,
/// never overwriting what it does not recognise.
/// </summary>
internal sealed class PendingPatch
{
    public enum Phase { Waiting, Applied, AlreadyPatched, Foreign, Unwritable }

    public BytePatch Patch { get; }
    public Phase State { get; private set; } = Phase.Waiting;
    public bool Settled => State != Phase.Waiting;
    /// <summary>The unexpected byte, for the Foreign log line.</summary>
    public byte Observed { get; private set; }

    public PendingPatch(BytePatch patch) => Patch = patch;

    /// <summary>One poll. Returns true exactly on the tick the state leaves Waiting.</summary>
    public bool Step(ICodePatcher patcher)
    {
        if (Settled) return false;
        if (!patcher.TryRead(Patch.Addr, 1, out var cur)) return false;
        Observed = cur[0];
        if (cur[0] == Patch.New) { State = Phase.AlreadyPatched; return true; }
        if (cur[0] != Patch.Old) { State = Phase.Foreign; return true; }
        if (!patcher.TryWrite(Patch.Addr, new[] { Patch.New })
            || !patcher.TryRead(Patch.Addr, 1, out var back) || back[0] != Patch.New)
        {
            State = Phase.Unwritable;
            return true;
        }
        State = Phase.Applied;
        return true;
    }
}
