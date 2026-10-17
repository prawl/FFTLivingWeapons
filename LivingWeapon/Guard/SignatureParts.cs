using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Which part each signature module belongs to, and the loop that ticks only the modules whose part is on.
/// An unmapped type throws, so a new signature cannot join the engine without being assigned.
/// </summary>
internal static class SignatureParts
{
    private static readonly Dictionary<Type, Part> Map = new()
    {
        [typeof(ExtraTurn)] = Part.BandSignatures,
        [typeof(EagleEye)] = Part.BandSignatures,
        [typeof(Ricochet)] = Part.BandSignatures,
        [typeof(Maim)] = Part.BandSignatures,
        [typeof(Kobu)] = Part.BandSignatures,
        [typeof(Iai)] = Part.BandSignatures,
        [typeof(Mushin)] = Part.BandSignatures,
        [typeof(Larceny)] = Part.BandSignatures,
        [typeof(Puppeteer)] = Part.BandSignatures,
        [typeof(Plague)] = Part.BandSignatures,
        [typeof(ProvokeHold)] = Part.BandSignatures,
        [typeof(Renewal)] = Part.BandSignatures,
        [typeof(Rapture)] = Part.BandSignatures,
        [typeof(SpiritualFont)] = Part.BandSignatures,
        [typeof(FeignDeath)] = Part.BandSignatures,
        [typeof(Benediction)] = Part.BandSignatures,
        [typeof(Sanctuary)] = Part.BandSignatures,
        [typeof(Choir)] = Part.BandSignatures,
        [typeof(Bulwark)] = Part.Bulwark,
        [typeof(Barrage)] = Part.WeaponCommands,
        [typeof(ShadowBlade)] = Part.WeaponCommands,
        [typeof(Provoke)] = Part.Provoke,
    };

    public static Part Of(ISignature signature)
    {
        if (Map.TryGetValue(signature.GetType(), out var part)) return part;
        throw new InvalidOperationException($"{signature.GetType().Name} has no part assigned in SignatureParts");
    }

    /// <summary>Ticks <paramref name="sigs"/>[i] only when <paramref name="on"/> says its part is on.</summary>
    public static void TickOpen(ISignature[] sigs, Part[] parts, Func<Part, bool> on, in TickContext ctx)
    {
        for (int i = 0; i < sigs.Length; i++)
            if (on(parts[i])) sigs[i].Tick(in ctx);
    }
}
