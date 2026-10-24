namespace LivingWeapon;

/// <summary>The Provoke hold's way out when the game rewinds a battle, with no battle edge to cue it.</summary>
internal sealed partial class ProvokeHold
{
    // True while the hold is waiting on a marked enemy or a party member is hidden right now.
    internal bool Holding => _state == HoldState.Armed || _flaggedNow.Count > 0;

    // The hidden party members come back into view and the hold starts over.
    // Unlike ResetBattle the enemy mark and any player side mark stay, because after a rewind they
    // belong to the restored game, and the idle path re-arms on a mark that survived.
    internal void ReleaseForRewind()
    {
        if (!_enabled) return;
        foreach (var id in _everFlagged)
        {
            long e = LocateByIdentity(_mem, id);
            if (e != 0) SetInvisible(_mem, e, false);
        }
        EnterIdle();
        _liveElapsed = 0;
        _flaggedNow.Clear();
        _everFlagged.Clear();
    }
}
