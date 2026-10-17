using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>One single-byte code patch. Address, expected vanilla 1.5.2 byte, wanted byte, log label.</summary>
internal readonly record struct BytePatch(long Addr, byte Old, byte New, string Label);

/// <summary>
/// Applies single-byte code patches as one transaction. Every site must carry its expected old byte before
/// any write (a mismatch refuses the set); a refused write rolls back earlier ones in reverse (CapBreakBootArm went site by site).
/// </summary>
internal sealed class BytePatchSet
{
    private readonly List<BytePatch> _applied = new();

    public int AppliedCount => _applied.Count;

    /// <summary>Null on success, else the refusal (nothing left applied).</summary>
    public string? Apply(ICodePatcher patcher, IReadOnlyList<BytePatch> patches)
    {
        if (_applied.Count > 0) return null;   // idempotent. Already applied
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
/// A patch whose page is copy-protected at boot (the two damage-path caps, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md
/// blueprint piece 2). Stepped from the tick loop. Waits for the vanilla byte, writes once, verifies. Already-patched counts
/// as done; a foreign byte makes it give up with one warning.
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

    /// <summary>One poll. Returns true on the tick the state leaves Waiting.</summary>
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
