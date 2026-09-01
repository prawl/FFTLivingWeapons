using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// LW-381: the same-session modded.pac mirror, split out of IconGlow.Apply.cs (200-line
/// trigger) as a real seam -- that file decides and writes the DEPLOYED loose tex (the durable
/// half the launch merge re-reads next boot); this file is strictly a visibility bonus layered on
/// top of an already-correct loose write, queued from <c>ApplyId</c> only after
/// <c>WriteDeployedTex</c> has already succeeded and flushed ONCE per <c>ApplyDiff</c> pass
/// (R2-2 -- see <see cref="FlushPacBatch"/>).
///
/// WHY THIS EXISTS NOW: ledger row [live-icon-repaint], PROVEN 2026-09-01 (owner-run
/// tools/probes/live_icon_patch_probe.py, three same-process patches all landed), established
/// that an in-place same-length overwrite of an icon's bytes inside modded.pac lands live with no
/// exclusive lock, and that an already-drawn icon refreshes on the next list redraw (re-entering
/// the list, or moving between party-menu sections -- a cursor move inside an open list is not
/// enough). That OVERTURNS the 2026-08-26 verdict ("icons cache at first draw for the whole
/// process, so only the loose tex can ever display correctly") IconGlow.cs used to justify never
/// touching modded.pac; companion ledger row [icon-refresh-unreliable] is RETRACTED as of the
/// same date. Before this, a mid-session tier change was invisible until the player quit and
/// relaunched (tools/glow_bounce.py's "the rhythm is TWO launches").
///
/// R2-2 (batching): a batch re-tier (tools/glow_bounce.py) touches all 242 managed entries in one
/// pass; queuing every surface's mirror during <c>ApplyId</c> and flushing ONCE at the end of
/// <c>ApplyDiff</c>, rather than calling the store per surface inline, is what turns that into ONE
/// shared pass over the pac instead of 242 -- see <see cref="IIconGlowStore.PatchPacBatch"/> and
/// <see cref="ModdedPac.PatchBatch"/> for where the actual single-pass search happens.
///
/// FAIL-SOFT BY DESIGN: nothing here may affect <c>ApplyId</c>'s <c>allOk</c> or ever call
/// <c>MarkUnmanaged</c>. The loose write already produced today's correct, shipped result before
/// this ever runs, so any pac outcome short of success just means "still restart-only this
/// session" -- never a retry, never a failure of the apply.
/// </summary>
internal sealed partial class IconGlow
{
    // _pacUnavailableWarned is a dedicated once-EVER flag (not WarnOnce, which is keyed per icon
    // id): "no pac this launch" is one environment fact (missing install, relocated Reloaded-II),
    // not a per-icon one, so it must warn exactly once total, not once per icon.
    // _pacWarnedOnce (R2-3) is a SEPARATE per-id warn-once set from _warnedOnce: that shared set
    // also gates MarkUnmanaged and the loose-tex write failure warning, so routing a cosmetic pac
    // failure through it would permanently mute a later genuine loose-tex failure warning for the
    // same id -- the failure that actually breaks the feature. See WarnPacOnce.
    // _pacPatchedThisApply / _pacAlreadyCurrentThisApply (R2-6) are reset and read back within a
    // single ApplyDiff call (the background task is serialized by _applying, so plain ints need
    // no lock -- they never survive past the call that produced them). Counted SEPARATELY: an
    // AlreadyCurrent result means nothing was written, so folding it into "pac-patched" would
    // report writes that never happened.
    // _pendingPacPatches accumulates every surface queued this ApplyDiff pass; FlushPacBatch
    // drains it in ONE store call at the end of the pass.
    private bool _pacUnavailableWarned;
    private readonly HashSet<int> _pacWarnedOnce = new();
    private int _pacPatchedThisApply;
    private int _pacAlreadyCurrentThisApply;
    private readonly List<(int Id, IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes)> _pendingPacPatches = new();

    /// <summary>After a successful <c>WriteDeployedTex</c>, queues the same bytes to be mirrored
    /// into modded.pac when this pass's batch flushes. Skips queuing entirely when there is
    /// nothing to mirror: no prior deployed bytes to diff against, a length mismatch against the
    /// manifest (never patchable in place; see <see cref="ModdedPac.Patch"/>), or the deployed
    /// bytes already matching the target -- that last case is also what makes a pristine id's
    /// very first write silent, since JudgeId's snapshot pass means ApplyId is never called for
    /// an id that has not actually changed tier.</summary>
    private void QueuePacMirror(int id, IconGlowEntry entry, byte[]? oldBytes, byte[] newBytes)
    {
        if (oldBytes == null || oldBytes.Length != entry.Length) return;
        if (oldBytes.SequenceEqual(newBytes)) return;
        _pendingPacPatches.Add((id, entry, oldBytes, newBytes));
    }

    /// <summary>R2-2: drains every surface <see cref="QueuePacMirror"/> queued this pass in ONE
    /// <see cref="IIconGlowStore.PatchPacBatch"/> call (a no-op, cheaply, when nothing was
    /// queued). Called once, at the end of <see cref="ApplyDiff"/>, strictly AFTER every loose
    /// write in this pass has already happened -- see the class remarks: loose-tex first, pac
    /// mirror second, never the other way round.</summary>
    private void FlushPacBatch()
    {
        if (_pendingPacPatches.Count == 0) return;

        var items = _pendingPacPatches.Select(p => (p.Entry, p.OldBytes, p.NewBytes)).ToList();
        var results = _store.PatchPacBatch(items);
        foreach (var p in _pendingPacPatches)
        {
            var result = results.TryGetValue((p.Id, p.Entry.Surface), out var r) ? r : PacPatchResult.Failed;
            RoutePacResult(p.Id, p.Entry, result);
        }
        _pendingPacPatches.Clear();
    }

    /// <summary>Routes one item's result exactly as round 1 did, with two R2 corrections:
    /// <see cref="PacPatchResult.NotFound"/> is expected steady state, not a failure (R2-4), and
    /// <see cref="PacPatchResult.AlreadyCurrent"/> is counted separately from a real write (R2-6).</summary>
    private void RoutePacResult(int id, IconGlowEntry entry, PacPatchResult result)
    {
        switch (result)
        {
            case PacPatchResult.Patched:
                _pacPatchedThisApply++;
                break;
            case PacPatchResult.AlreadyCurrent:
                _pacAlreadyCurrentThisApply++;   // R2-6: nothing was written -- never counted as a write
                break;
            case PacPatchResult.Unavailable:
                if (!_pacUnavailableWarned)
                {
                    _pacUnavailableWarned = true;
                    ModLogger.Warn(LogVerb.Display,
                        "IconGlow cannot locate modded.pac this launch (Reloaded-II may be installed outside the game folder); icon tier changes stay restart-only this session. The deployed loose tex is unaffected.");
                }
                break;
            case PacPatchResult.NotFound:
                // R2-4: legitimately normal, not a bug. The pac's incremental launch merge reads
                // the deployed loose tex ONCE, at launch; a weapon this runtime re-tiers AFTER
                // that merge has already run leaves the OLD bytes (the ones this search looked
                // for) genuinely absent from the pac -- measured on the live install, 6 of 242
                // deployed texes (the two surfaces of every weapon then above tier 0) were
                // exactly this case, zero of them a real problem. Self-heals on the next launch's
                // merge. Debug only -- promoting this to a warning would train the owner to
                // ignore the channel.
                ModLogger.Debug(LogVerb.Display,
                    $"IconGlow found no modded.pac occurrence to mirror icon {id} ({entry.Surface}) into this session (expected when this session re-tiered the icon after the launch merge already ran); it will still show correctly after a restart.");
                break;
            default:   // Ambiguous, Failed -- genuinely unexpected, on the R2-3 dedicated warn set
                WarnPacOnce(id, $"IconGlow could not mirror icon {id} ({entry.Surface}) into modded.pac this session ({result}); it will still show correctly after a restart.");
                break;
        }
    }

    /// <summary>R2-3: a DEDICATED once-per-id warn set for pac-mirror failures, independent of
    /// <see cref="_warnedOnce"/> (see the field remarks above for why sharing it would be a
    /// defect, not just a style choice).</summary>
    private void WarnPacOnce(int id, string message)
    {
        lock (_lock) { if (!_pacWarnedOnce.Add(id)) return; }
        ModLogger.Warn(LogVerb.Display, message);
    }
}
