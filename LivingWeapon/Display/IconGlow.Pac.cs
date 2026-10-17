using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The same-session modded.pac mirror, split from IconGlow.Apply.cs. That file writes the
/// deployed loose tex the launch merge re-reads next boot; this one is a visibility bonus queued
/// from ApplyId after WriteDeployedTex succeeded and flushed once per ApplyDiff pass.
///
/// Ledger row [live-icon-repaint] (2026-09-01, tools/probes/live_icon_patch_probe.py) showed a
/// same-length in-place overwrite inside modded.pac lands live with no exclusive lock, and that a
/// drawn icon refreshes on the next list redraw, not on a cursor move. That overturned the
/// 2026-08-26 verdict that icons cache for the whole process, and retracted [icon-refresh-unreliable].
/// Before this a mid-session tier change needed a relaunch.
///
/// A batch re-tier (tools/glow_bounce.py) touches all 242 managed entries, so the mirrors queue
/// during ApplyId and flush once at the end of ApplyDiff, one pass over the pac instead of 242.
/// See <see cref="IIconGlowStore.PatchPacBatch"/> and <see cref="ModdedPac.PatchBatch"/>.
///
/// Fail soft. Nothing here touches ApplyId's allOk or calls MarkUnmanaged. The loose write already
/// produced the correct result, so any pac outcome short of success means restart-only this session.
/// </summary>
internal sealed partial class IconGlow
{
    // "No pac this launch" is one environment fact, so it warns once total, not once per id.
    private bool _pacUnavailableWarned;
    // Separate from _warnedOnce, which also gates MarkUnmanaged and the loose-tex failure warning;
    // sharing it would mute a later real loose-tex failure for the same id.
    private readonly HashSet<int> _pacWarnedOnce = new();
    // Reset and read within one ApplyDiff call; _applying serializes the task so no lock.
    // AlreadyCurrent wrote nothing and is counted apart from Patched.
    private int _pacPatchedThisApply;
    private int _pacAlreadyCurrentThisApply;
    private int _hidesThisApply;   // surfaces the hider reported Hidden this pass, for the summary line
    private readonly List<(int Id, IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes)> _pendingPacPatches = new();   // queued this pass, FlushPacBatch drains it
    private readonly List<(int Id, string Surface, IconGlowEntry Entry)> _pacPatchedThisPass = new();   // landed as Patched this pass, the cache-hide set

    /// <summary>Queues the bytes a successful WriteDeployedTex just wrote for the pac mirror. Nothing
    /// is queued without prior deployed bytes, on a length mismatch against the manifest (never
    /// patchable in place, see <see cref="ModdedPac.Patch"/>), or when the bytes already match. The
    /// last case keeps a pristine id's first write silent.</summary>
    private void QueuePacMirror(int id, IconGlowEntry entry, byte[]? oldBytes, byte[] newBytes)
    {
        if (oldBytes == null || oldBytes.Length != entry.Length) return;
        if (oldBytes.SequenceEqual(newBytes)) return;
        _pendingPacPatches.Add((id, entry, oldBytes, newBytes));
    }

    /// <summary>Drains everything <see cref="QueuePacMirror"/> queued this pass in one
    /// <see cref="IIconGlowStore.PatchPacBatch"/> call, at the end of <see cref="ApplyDiff"/> after
    /// every loose write. Loose tex first, pac second, never the other way round.</summary>
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

        // Hand every surface that landed to the cache hider (IconGlow.Hide.cs) so the next screen
        // open shows it instead of the old art.
        HideThisPassPatched();
    }

    /// <summary>Routes one result. NotFound is steady state, not a failure, and AlreadyCurrent
    /// counts apart from a real write.</summary>
    private void RoutePacResult(int id, IconGlowEntry entry, PacPatchResult result)
    {
        switch (result)
        {
            case PacPatchResult.Patched:
                _pacPatchedThisApply++;
                _pacPatchedThisPass.Add((id, entry.Surface, entry));   // hide-eligible this pass
                break;
            case PacPatchResult.AlreadyCurrent:
                _pacAlreadyCurrentThisApply++;   // nothing was written
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
                // The launch merge reads the loose tex once at launch, so an icon re-tiered after
                // that merge has its old bytes absent from the pac. Measured live, 6 of 242
                // deployed texes were this case and none was a problem. Heals on the next launch.
                // Debug only, a warning here would train the owner to ignore the channel.
                ModLogger.Debug(LogVerb.Display,
                    $"IconGlow found no modded.pac occurrence to mirror icon {id} ({entry.Surface}) into this session (expected when this session re-tiered the icon after the launch merge already ran); it will still show correctly after a restart.");
                break;
            default:   // Ambiguous and Failed are unexpected, on the dedicated warn set
                WarnPacOnce(id, $"IconGlow could not mirror icon {id} ({entry.Surface}) into modded.pac this session ({result}); it will still show correctly after a restart.");
                break;
        }
    }

    /// <summary>Once-per-id warn for pac mirror failures, kept apart from <see cref="_warnedOnce"/>
    /// (see the field comment).</summary>
    private void WarnPacOnce(int id, string message)
    {
        lock (_lock) { if (!_pacWarnedOnce.Add(id)) return; }
        ModLogger.Warn(LogVerb.Display, message);
    }
}
