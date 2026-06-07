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

    // --- condensed turn-queue head (the acting unit) ---
    public const long TurnQueue = 0x14077D2A0;
    public const int TqLevel = 0x00;   // u16
    public const int TqTeam  = 0x02;   // u16  0 = player, 1 = enemy
    public const int TqNameId = 0x04;  // u16  matches roster +0x230
    public const int TqMaxHp = 0x10;   // u16

    // --- static unit array ---
    public const long ArrayBase = 0x140893C00;
    public const int ArrayStride = 0x200;
    public const int SlotsBack = 20;   // enemy slots, at array offsets <= 0
    public const int SlotsFwd  = 10;   // player slots, at array offsets >= 1
    public const int NSlots = SlotsBack + SlotsFwd;
    // slot s sits at ArrayBase + (s - (SlotsBack - 1)) * stride; enemy slots are s <= SlotsBack-1.
    public const long ArrayReadBase = ArrayBase - (SlotsBack - 1) * ArrayStride;
    public const int EnemySlotMax = SlotsBack - 1;   // slots 0..19 are enemy-side
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
    public const int RNameId = 0x230;  // u16

    // --- combat struct (writable stats that actually drive battle damage) ---
    // Ramza is the verified anchor: PA byte at 0x14184F8CE, MA +1, Speed +2.
    // Growing the rest of the party needs the combat-array slot-0 base (TODO --
    // one Cheat Engine pass; see docs/UNIMPLEMENTED_MECHANICS.md).
    public const long RamzaPa = 0x14184F8CE;
    public const int MaDelta = 1;      // MA  address = PA address + 1
    public const int SpeedDelta = 2;   // Spd address = PA address + 2

    // --- display scratch (equipped-weapon menu WP, Ramza context) ---
    public const long WpScratch = 0x141870836;
}
