using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>One byte of game code to change, with the byte the game ships there and a name for the log.</summary>
internal readonly record struct BytePatch(long Addr, byte Old, byte New, string Label);

/// <summary>
/// Changes a set of single bytes in game code all together or not at all. Every site must still carry the byte the
/// game ships before anything is written, so one changed site refuses the set, and a refused write puts the earlier ones back.
/// </summary>
internal sealed class BytePatchSet
{
    private readonly List<BytePatch> _applied = new();

    public int AppliedCount => _applied.Count;

    /// <summary>Null on success, else why not, with nothing left applied.</summary>
    public string? Apply(ICodePatcher patcher, IReadOnlyList<BytePatch> patches)
    {
        if (_applied.Count > 0) return null;   // a second call changes nothing
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

    /// <summary>Puts every applied site's old byte back, newest first. Safe to call again.</summary>
    public void Rollback(ICodePatcher patcher)
    {
        for (int i = _applied.Count - 1; i >= 0; i--)
            patcher.TryWrite(_applied[i].Addr, new[] { _applied[i].Old });
        _applied.Clear();
    }
}

/// <summary>
/// A byte patch for a site the game keeps copy protected at boot, so it is applied later from the tick loop. It polls
/// until the site shows the byte the game ships, writes once and reads it back. A site already holding the wanted byte
/// counts as done, whoever put it there, and a byte that is neither means someone else changed the site, so it gives up.
/// </summary>
internal sealed class PendingPatch
{
    public enum Phase { Waiting, Applied, AlreadyPatched, Foreign, Unwritable }

    public BytePatch Patch { get; }
    public Phase State { get; private set; } = Phase.Waiting;
    public bool Settled => State != Phase.Waiting;
    /// <summary>The byte seen last, so the log can say what sat there when it gave up.</summary>
    public byte Observed { get; private set; }

    public PendingPatch(BytePatch patch) => Patch = patch;

    /// <summary>One poll. True only on the tick the state leaves Waiting.</summary>
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
