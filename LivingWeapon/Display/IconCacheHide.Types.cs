using System.Collections.Generic;

namespace LivingWeapon;

// Data-model types shared by IconCacheHide.cs and IconCacheHide.Policy.cs, split out to keep
// IconCacheHide.Policy.cs under the 200-line trigger. IIconCacheHider, IconCacheTarget and NoopIconCacheHider
// are not members of the IconCacheHide partial class; they are its collaborator seam and request shape.

/// <summary>IconGlow decides when and which icon; this seam does the sweep and writes. See <see cref="IconCacheHide"/>'s class remarks.</summary>
internal interface IIconCacheHider
{
    IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets);
}

/// <summary>One surface to hide, with its manifest row (<see cref="IconGlowEntry.Length"/> gates the texture-record size).</summary>
internal readonly record struct IconCacheTarget(int Id, string Surface, IconGlowEntry Entry);

/// <summary>Touches no memory; every requested surface comes back absent. Used by IconGlowTests.cs sites that do not exercise the sweep.</summary>
internal sealed class NoopIconCacheHider : IIconCacheHider
{
    public IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets) => new();
}

/// <summary>Which of the two cache records a needle string belongs to.</summary>
internal enum NeedleKind { PartsUri, TexPath }

/// <summary>One of the four strings a re-tiered icon's records can point at, for one surface.</summary>
internal readonly record struct Needle(string Text, string Surface, NeedleKind Kind);

/// <summary>Outcome of one surface's hide attempt. See <see cref="IconCacheHide"/>'s class remarks for the
/// coupling rule between <see cref="Hidden"/> and <see cref="TextureMissing"/>.</summary>
internal enum HideOutcome { Hidden, NothingResident, TextureMissing, Postponed, Refused }

/// <summary>Per-surface result. Records found by the pointer sweep (*Candidates) versus classified and
/// verified-written (*Hidden).</summary>
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
