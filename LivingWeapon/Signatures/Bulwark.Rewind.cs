namespace LivingWeapon;

/// <summary>True while the Bulwark has walled a tile or is waiting to wall one.</summary>
internal sealed partial class Bulwark
{
    internal bool Holding => _restoreBook.Count > 0 || _deferred.Count > 0;

    // A rewind puts the ground back quietly, since the rewind release prints the one line.
    internal void ReleaseForRewind()
    {
        Release(null);
        _wasActive = false;
        _prevTurnFlag = null;
        _unresolvedStreak = 0;
    }
}
