namespace LivingWeapon;

/// <summary>
/// The pure decisions behind the Defender's "Provoke" signature -- LW-123 arc 1, the runtime half
/// that repoints ability 189 (vanilla Embrace) to plant an inert mark and keeps that repoint alive
/// whenever a tier-3 main-hand Defender wielder exists, plus the JobCommand injection that puts
/// the renamed command in the wielder's action list. See Provoke.cs's class doc for why this arc
/// ships deliberately inert (no data/items.json signature block yet) and for the two-lifecycle
/// split (slot vs. table) this policy class backs.
///
/// GRANT RESOLUTION is REUSED from ShadowBladePolicy, not duplicated: both weapons are sword-
/// family blades whose wielders are Knight-ish jobs, so the Squire/Knight whitelist is shared BY
/// DESIGN. A future change to Shadow Blade's whitelist moves Provoke's too -- intentional, not a
/// coupling accident; if the two ever need to diverge, that is the moment to split them.
///
/// THE TWO TABLE WRITES: observed live 2026-07-22 (LIVE_LEDGER row, Uncertain) and specified in
/// docs/PROVOKE_AC.md. The ability ACTION table (<see cref="Offsets.LiveActionTable"/>) is
/// hardcoded in the exe, 368 rows of 20 bytes, in TWO byte-identical copies exactly one table-
/// length apart -- <see cref="DecoyActionTable"/> accepts writes, reads them back correctly, and
/// is ignored by everything that plays the game; only the LIVE copy is ever written. Byte +15 of a
/// row is InflictStatus, an INDEX into the inflict-status table (<see cref="Offsets.InflictTable"/>,
/// 128 rows of 6 bytes, `[mode][s0..s4]`, the MODE byte FIRST -- a one-byte-late framing of this
/// table already cost a live cycle once, which is why the byte order is pinned by its own test).
/// Row <see cref="ProvokeInflictRow"/> (29) was unused and is ours to author: mode 0x80
/// (AllOrNothing) then s0 bit 0x80 (StatusEffectData id 0, the blank slot docs/PROVOKE_AC.md
/// claims as the mark).
/// </summary>
internal static class ProvokePolicy
{
    /// <summary>Ability 189, vanilla Embrace, renamed via tools/patch_ability_names.py. TESTS
    /// ONLY -- production always reads the granted id from meta.Signature.GrantCommandAbilityId
    /// (see Provoke.cs), never this constant, so items.json stays the one source of truth.</summary>
    public const int ProvokeAbilityId = 189;

    /// <summary>True when the signature is configured (a granted ability id set) and the kill tier
    /// is earned. Same shape as Barrage/ShadowBlade's IsActive -- the tier gate lives only here.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.GrantCommandAbilityId > 0;

    /// <summary>Resolve a wielder to the JobCommand record/learned-index Provoke should inject
    /// into. Delegates to <see cref="ShadowBladePolicy.TryResolveGrant"/> -- see this class's doc
    /// for why the Squire/Knight whitelist is shared by design, not re-implemented here.</summary>
    public static bool TryResolveGrant(int job, int secondaryRec, out int recId, out int jobIdx, out bool viaSecondary)
        => ShadowBladePolicy.TryResolveGrant(job, secondaryRec, out recId, out jobIdx, out viaSecondary);

    /// <summary>The decoy mirror of <see cref="Offsets.LiveActionTable"/>, one table-length before
    /// it. NEVER written by anything in this runtime -- kept here (not Offsets.cs) because it is a
    /// policy-level safety constant proven only by a test (both copies hold identical data, so no
    /// runtime check can ever catch a mis-pin), not an address the runtime writes through.</summary>
    public const long DecoyActionTable = 0x14078961C;

    public const int ActionStride = 20;
    public const int ActionRows = 368;
    public const int InflictStride = 6;
    public const int InflictRows = 128;

    /// <summary>Byte offset of InflictStatus within an action row.</summary>
    public const int ActionInflictOffset = 15;

    /// <summary>The unused inflict-table row Provoke authors. The row NUMBER doubles as the byte
    /// value written into an action row's InflictStatus index.</summary>
    public const int ProvokeInflictRow = 29;

    /// <summary>The six bytes row 29 must hold: mode 0x80 (AllOrNothing) FIRST, then s0's bit set
    /// (StatusEffectData id 0), s1..s4 clear.</summary>
    public static readonly byte[] DesiredInflictRow = { 0x80, 0x80, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>Address of the InflictStatus byte for the given ability id, in the LIVE table
    /// only -- <see cref="Offsets.LiveActionTable"/>, never <see cref="DecoyActionTable"/>.</summary>
    public static long ActionInflictAddr(int abilityId) => Offsets.LiveActionTable + (long)abilityId * ActionStride + ActionInflictOffset;

    /// <summary>Address of the first byte of the given inflict-table row.</summary>
    public static long InflictRowAddr(int row) => Offsets.InflictTable + (long)row * InflictStride;

    /// <summary>True when <paramref name="abilityId"/> addresses a row inside the live action
    /// table's actual bounds (0..ActionRows-1). A wrong id from a future items.json/meta edit (say
    /// 1890 typo'd for 189) would otherwise let <see cref="ActionInflictAddr"/> compute an address
    /// hundreds of rows past the table and land on whatever structure happens to sit there next --
    /// same fail-closed reasoning as tools/generate.py raising when OptionsAbilityId exceeds 255,
    /// applied here as a runtime check instead of a build-time one. Provoke.WriteTable refuses
    /// (and logs) when this is false, before touching any address derived from it.</summary>
    public static bool IsValidAbilityId(int abilityId) => abilityId >= 0 && abilityId < ActionRows;

    /// <summary>True when <paramref name="row"/> addresses a row inside the inflict-status
    /// table's actual bounds (0..InflictRows-1). Provoke only ever authors
    /// <see cref="ProvokeInflictRow"/> (a compile-time constant, always in range), so this guard
    /// is currently unreachable in practice -- it exists for the same reason as
    /// <see cref="IsValidAbilityId"/>, and the two checks belong together.</summary>
    public static bool IsValidInflictRow(int row) => row >= 0 && row < InflictRows;

    /// <summary>True when the action row's InflictStatus byte does not yet point at
    /// <see cref="ProvokeInflictRow"/>.</summary>
    public static bool NeedsActionWrite(byte currentByte) => currentByte != (byte)ProvokeInflictRow;

    /// <summary>True when byte <paramref name="idx"/> (0..5) of the authored inflict row does not
    /// yet match <see cref="DesiredInflictRow"/>.</summary>
    public static bool NeedsInflictByteWrite(byte currentByte, int idx) => currentByte != DesiredInflictRow[idx];
}
