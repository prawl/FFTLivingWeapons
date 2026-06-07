namespace LivingWeapon;

/// <summary>
/// Verified game addresses. The image base is fixed at 0x140000000 with no ASLR,
/// and every address below lives in the always-mapped main module, so they are
/// valid in-process pointers we can read/write directly (no AoB, no syscalls).
/// Sources: FFTHandsFree/docs/BATTLE_MEMORY_MAP.md and the BattleTracker the
/// detection logic is ported from.
/// </summary>
internal static class Offsets
{
    // --- in-battle flags ---
    public const long Slot0 = 0x14077CA30;   // u32 == 0xFF        when in battle
    public const long Slot9 = 0x14077CA54;   // u32 == 0xFFFFFFFF  when in battle (sticky indicator)
    public const long Acted = 0x14077CA8C;   // u8  acting unit has acted this turn

    // --- condensed active-unit struct = the unit whose turn it is (FFTHandsFree
    //     NavigationActions.Scan "AddrCondensedBase"). The acting player is identified by
    //     HP+MaxHP+level matched against the battle array, then resolved to the roster by a
    //     level/brave/faith FINGERPRINT -- NOT by +0x04. TqNameId (+0x04) is a SEQUENTIAL
    //     battle index, not the roster nameId: a Time Mage's index 1 collides with Ramza's
    //     roster nameId 1, which mis-credited everyone's kills to Ramza. Do not resolve by it. ---
    public const long TurnQueue = 0x14077D2A0;
    public const int TqLevel = 0x00;   // u16
    public const int TqTeam  = 0x02;   // u16  0 = player, 1 = enemy
    public const int TqNameId = 0x04;  // u16  SEQUENTIAL battle index (NOT roster nameId -- a trap)
    public const int TqHp    = 0x0C;   // u16  active unit's current HP (fingerprint key)
    public const int TqMaxHp = 0x10;   // u16  active unit's MaxHP (fingerprint key)

    // --- static unit array ---
    public const long ArrayBase = 0x140893C00;
    public const int ArrayStride = 0x200;
    public const int SlotsBack = 20;   // enemy slots, at array offsets <= 0
    public const int SlotsFwd  = 10;   // player slots, at array offsets >= 1
    public const int NSlots = SlotsBack + SlotsFwd;
    // slot s sits at ArrayBase + (s - (SlotsBack - 1)) * stride; enemy slots are s <= SlotsBack-1.
    public const long ArrayReadBase = ArrayBase - (SlotsBack - 1) * ArrayStride;
    public const int EnemySlotMax = SlotsBack - 1;   // slots 0..19 are enemy-side
    public const int ALevel    = 0x0D; // u8   (roster-match fingerprint)
    public const int ABrave    = 0x0E; // u8   origBrave (roster-match fingerprint)
    public const int AFaith    = 0x10; // u8   origFaith (roster-match fingerprint)
    public const int AInBattle = 0x12; // u16
    public const int AHp       = 0x14; // u16  (0 == KO'd)
    public const int AMaxHp    = 0x16; // u16
    public const int AGx       = 0x33; // u8
    public const int AGy       = 0x34; // u8

    // --- roster (nameId -> equipped right hand) ---
    public const long RosterBase = 0x1411A18D0;
    public const int RosterStride = 0x258;
    public const int RosterSlots = 20;
    public const int RRHand  = 0x14;   // u16 right-hand weapon id (FFTPatcher-canonical, == items.json id)
    public const int RLevel  = 0x1D;   // u8  (0 / empty slot guard)
    public const int RBrave  = 0x1E;   // u8  (fingerprint to find this unit's combat struct)
    public const int RFaith  = 0x1F;   // u8
    public const int RNameId = 0x230;  // u16

    // --- combat-struct array (writable stats that actually drive battle damage) ---
    // Ramza's struct (0x14184F890; PA at +0x3E = 0x14184F8CE) is the verified anchor.
    // Party units sit at +/- n*stride. We self-map each via its weapon id at +0x20 --
    // no need for the exact slot-0 base -- and only WRITE where a full combat-struct
    // signature checks out, so a wrong layout guess can never corrupt memory.
    public const long CombatAnchor = 0x14184F890;
    public const int CombatStride = 0x200;
    public const int CombatSearchSlots = 24;   // scan +/- this many slots around the anchor
    public const int CWeapon = 0x20;   // u16 equipped weapon id (the self-mapping key)
    public const int CBrave  = 0x2A;   // u8
    public const int CFaith  = 0x2C;   // u8
    public const int CPa     = 0x3E;   // u8  (drives physical damage)
    public const int CMa     = 0x3F;   // u8
    public const int CSpeed  = 0x40;   // u8

    // --- display scratch (equipped-weapon menu WP, Ramza context) ---
    public const long WpScratch = 0x141870836;

    // --- battlefield discriminator: 0 = OUT of battle (world map / menus -- even when
    //     slot9 is still the stuck 0xFFFFFFFF sentinel), 2/3/4 = on the live battlefield.
    //     Verified in FFTHandsFree (CommandWatcher.cs). slot9 alone can't tell the
    //     world-map party menu from combat; this can, so the card paints there instead
    //     of only at game boot (the old "kills update only after restart" bug). ---
    public const long BattleMode = 0x140900650;

    // --- in-battle "BattleStatus" card: checking a unit's status mid-battle opens the
    //     equip card (with the Kills line). Detected (per FFTHandsFree ScreenDetectionLogic)
    //     as pauseFlag==1 && menuCursor==3 (the Status action-menu slot) && submenuFlag==1.
    //     Lets the counter paint there too -- safe because it's a paused, stable menu. ---
    public const long PauseFlag = 0x140C64A5C;
    public const long MenuCursor = 0x1407FC620;
    public const long SubmenuFlag = 0x140D3A10C;

    // --- equip-screen "mirror": the VIEWED unit's equipped gear in UI row order,
    //     [Weapon, LHand, Helm, Body, Accessory] as u16. Mirror[0] = the weapon whose
    //     card is on screen, so the in-card Kills counter knows WHICH weapon to show.
    //     Verified in FFTHandsFree (CommandWatcher.cs, 2026-04-15). Two synced copies. ---
    public const long MirrorWeapon = 0x141870854;
}
