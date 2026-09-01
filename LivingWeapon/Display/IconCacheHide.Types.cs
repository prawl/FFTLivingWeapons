using System.Collections.Generic;

namespace LivingWeapon;

// LW-381: the small data-model types IconCacheHide.cs and IconCacheHide.Policy.cs both share --
// split out purely to keep IconCacheHide.Policy.cs's line count within the 200-line trigger; not
// a partial class, just the file split rule applied to a real seam (data shapes vs. the logic
// that produces/consumes them). IIconCacheHider/IconCacheTarget/NoopIconCacheHider (round 3, C11-
// C13 pushed IconCacheHide.cs itself past 200 lines) moved here for the same reason: none of the
// three is a member of the IconCacheHide partial class -- they are the collaborator contract and
// its request shape, exactly the kind of shared data model this file already exists for.

/// <summary>IconGlow decides WHEN and WHICH icon; this seam decides HOW (sweep + writes). See
/// <see cref="IconCacheHide"/>'s class remarks.</summary>
internal interface IIconCacheHider
{
    IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets);
}

/// <summary>One surface this pass wants hidden, and its manifest row (needed for
/// <see cref="IconGlowEntry.Length"/>, the texture-record size gate).</summary>
internal readonly record struct IconCacheTarget(int Id, string Surface, IconGlowEntry Entry);

/// <summary>Touches no memory; every requested surface simply comes back absent from the
/// result. What every IconGlowTests.cs site that isn't exercising the sweep itself passes.</summary>
internal sealed class NoopIconCacheHider : IIconCacheHider
{
    public IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets) => new();
}

/// <summary>Which of the two cache records a needle string belongs to.</summary>
internal enum NeedleKind { PartsUri, TexPath }

/// <summary>One of the four exact strings a re-tiered icon's records can point at, for one surface.</summary>
internal readonly record struct Needle(string Text, string Surface, NeedleKind Kind);

/// <summary>Every outcome one surface's hide attempt can land on. See
/// <see cref="IconCacheHide"/>'s class remarks for the coupling rule that decides between
/// <see cref="Hidden"/> and <see cref="TextureMissing"/>.</summary>
internal enum HideOutcome { Hidden, NothingResident, TextureMissing, Postponed, Refused }

/// <summary>Per-surface result: how many of each record kind were found (<c>*Candidates</c>,
/// discovered by the pointer sweep regardless of whether they classified) versus actually hidden
/// (<c>*Hidden</c>, classified AND verified-written).</summary>
internal readonly record struct SurfaceOutcome(
    HideOutcome Outcome, int PartsCandidates, int PartsHidden, int TexCandidates, int TexHidden);

/// <summary>Every (id, surface) outcome one <see cref="IconCacheHide.Hide"/> call produced.</summary>
internal sealed class IconCacheHideResult
{
    private readonly Dictionary<(int Id, string Surface), SurfaceOutcome> _bySurface = new();

    internal void Set(int id, string surface, SurfaceOutcome outcome) => _bySurface[(id, surface)] = outcome;

    internal SurfaceOutcome? Get(int id, string surface)
        => _bySurface.TryGetValue((id, surface), out var o) ? o : (SurfaceOutcome?)null;

    internal IReadOnlyDictionary<(int Id, string Surface), SurfaceOutcome> Surfaces => _bySurface;
}
