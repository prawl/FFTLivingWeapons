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
    public const long Slot0 = 0x140782A30;   // 1.5 PREDICTED +0x6000 (was 0x14077CA30) -- VERIFY (read 0x10, existence marker may differ in 1.5)
    public const long Slot9 = 0x140782A54;   // 1.5 PREDICTED +0x6000 (was 0x14077CA54); read 0xFFFFFFFF (terminator plausible)
    public const long Acted = 0x140782A8C;   // 1.5 PREDICTED +0x6000 (was 0x14077CA8C) -- VERIFY by watching 0->1 on an action
    public const long EventId = 0x140782A94; // 1.5 PREDICTED +0x6000 (was 0x14077CA94). u16 event file number during cutscenes/dialogue; ALIASES as
                                             //   the active unit's nameId during combat animations -- only
                                             //   meaningful while out of live battle (dialogue/cutscene gate)

    // --- condensed active-unit struct = the unit whose turn it is (FFTHandsFree
    //     NavigationActions.Scan "AddrCondensedBase"). The acting player is identified by
    //     HP+MaxHP+level matched against the battle array, then resolved to the roster by a
    //     level/brave/faith FINGERPRINT -- NOT by +0x04. TqNameId (+0x04) is a SEQUENTIAL
    //     battle index, not the roster nameId: a Time Mage's index 1 collides with Ramza's
    //     roster nameId 1, which mis-credited everyone's kills to Ramza. Do not resolve by it. ---
    public const long TurnQueue = 0x1407832A0;   // 1.5 CONFIRMED +0x6000 (was 0x14077D2A0): fingerprint team=0/nameId=1/hp=486/486
    public const int TqLevel = 0x00;   // u16
    public const int TqTeam  = 0x02;   // u16  0 = player, 1 = enemy
    public const int TqNameId = 0x04;  // u16  SEQUENTIAL battle index (NOT roster nameId -- a trap)
    public const int TqHp    = 0x0C;   // u16  active unit's current HP (fingerprint key)
    public const int TqMaxHp = 0x10;   // u16  active unit's MaxHP (fingerprint key)

    // --- static unit array ---
    public const long ArrayBase = 0x140899F50;   // 1.5 CONFIRMED +0x6350 (was 0x140893C00): verified captures 11 enemies (slots 4-14), excludes Ramza (slot 20)
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
    public const int AMp       = 0x18; // u16  Live-verified 2026-06-10: MP visibly restored on screen.
                                       //      The u16 pair right after HP/MaxHP in the band-entry layout.
                                       //      EVERY MP write is gated behind SpiritualFont's per-battle
                                       //      layout validation + a post-write read-back (SET/MISS log).
    public const int AMaxMp    = 0x1A; // u16  (see AMp; same per-battle guard applies)
    public const int AGx       = 0x33; // u8
    public const int AGy       = 0x34; // u8
    /// <summary>u8 band-relative WRITE TARGET: slam this to 100 to inject a scheduler turn
    /// (ExtraTurn.CtOff). Matches combat base+0x41. Do NOT read this for turn counting --
    /// a live watcher saw zero transitions; the write takes, but reads don't tick reliably.
    /// NOTE 2026-06-14 (ct_watch / finish_watch probes): for a PLAYER unit this byte CAN read and
    /// climb to 100 (+0x09 stays flat 0, the Rapture wall), and CharmLock reads it cleanly for ENEMY
    /// turns -- but on the player's OWN actively-managed unit it read INCONSISTENTLY (clean 100 in one
    /// probe, but stale/frozen ~85 during the unit's own input menu in the live DLL). So counting the
    /// player wielder's OWN turns off it proved unreliable; FeignDeath uses a wall clock instead.
    /// Treat as: write target always; clean enemy-turn read; do NOT trust for the player's own turns.</summary>
    public const int ACtSlam   = 0x25;
    /// <summary>u8 band-relative READ byte for counting a unit's completed turns: CT seen
    /// at/above 90, then falls below 70 = one turn taken. Live-proven by Maim (victim-turn
    /// counting) and CharmLock. CtTurns feeds off this offset. Equals combat base+0x25.</summary>
    public const int ACtTurn   = 0x09;

    // --- roster (nameId -> equipped right hand) ---
    public const long RosterBase = 0x1411A7D10;   // 1.5 CONFIRMED +0x6440 (was 0x1411A18D0): slot0=Ramza (lvl99/rhand80/nameId1), slots +1..+7 = real party
    public const int RosterStride = 0x258;
    public const int RosterSlots = 20;
    public const int RAccessory = 0x12; // u16 equipped accessory item id.
                                       //   Probe-confirmed 2026-06-12: Scholar's Ring (id 260) at
                                       //   RosterBase + slot*RosterStride + 0x12 for the equipping slot;
                                       //   sibling accessories 218/224/226/232 confirmed in adjacent slots.
                                       //   Empty slot reads 255 or 0, never 260.
    public const int RRHand  = 0x14;   // u16 right-hand weapon id (FFTPatcher-canonical, == items.json id)
    public const int RLHand  = 0x16;   // u16 left-hand weapon id; 0xFF/0xFFFF when empty (kept for safety; live it stays empty)
    public const int ROffHand = 0x18;  // u16 dual-wield OFF-HAND weapon. FFTHandsFree mislabels this "reserved" --
                                       // a live FFT:IC roster dump proved the 2nd weapon lands HERE (+0x16 stays empty;
                                       // shields go to +0x1A). Read alongside RRHand to credit both blades.
    public const int RSupport = 0x0A;   // u8 support ability id the player picked (FFTHandsFree RosterOffSupport)
    public const int RLevel  = 0x1D;   // u8  (0 / empty slot guard)
    public const int RBrave  = 0x1E;   // u8  (fingerprint to find this unit's combat struct)
    public const int RFaith  = 0x1F;   // u8
    public const int RNameId = 0x230;  // u16

    // --- combat-struct array (writable stats that actually drive battle damage) ---
    // Ramza's struct (0x14184F890; PA at +0x3E = 0x14184F8CE) is the verified anchor.
    // Party units sit at +/- n*stride. We self-map each via its weapon id at +0x20 --
    // no need for the exact slot-0 base -- and only WRITE where a full combat-struct
    // signature checks out, so a wrong layout guess can never corrupt memory.
    public const long CombatAnchor = 0x141855CE0;   // 1.5 CONFIRMED +0x6450 (was 0x14184F890): Ramza weapon80/lvl99/hp486/pa18, twin at +0x800
    public const int CombatStride = 0x200;
    public const int CombatSearchSlots = 24;   // scan +/- this many slots around the anchor
    public const int CWeapon = 0x20;   // u16 equipped weapon id (the self-mapping key)
    /// <summary>u8 level inside the combat struct frame (== BandEntry+ALevel == 0x1C+0x0D = 0x29).
    /// Used by GrowthEngine.MatchesEntry to reject enemy slots sharing brave/faith with a player.</summary>
    public const int CLevel  = 0x29;   // u8  (== BandEntry+ALevel; one byte before CBrave)
    public const int CBrave  = 0x2A;   // u8
    public const int CFaith  = 0x2C;   // u8
    public const int CHp     = 0x30;   // u16 current HP (== the auth-band framing's +0x14)
    public const int CPa     = 0x3E;   // u8  (drives physical damage)
    public const int CMa     = 0x3F;   // u8
    public const int CSpeed  = 0x40;   // u8
    // Passive bitfields on the LIVE combat struct (proven write+holdable mid-battle, 2026-06-08):
    // reaction +0x94 (4 bytes, base id 166), support +0x98 (4 bytes, base 198), movement +0x9C
    // (3 bytes, base 230); MSB-first. Signatures only ever touch SUPPORT (stacks, no slot hijack).
    public const int CMount         = 0x1B4;   // u8 Mount Info; bit 0x80 = this unit is riding (chocobo). Proven live 2026-06-26 (mount_probe.py / chocobo-mount-bytes).
    public const byte CMountRidingBit = 0x80;
    public const int CReaction = 0x94;   // 4 bytes; Maim zeroes this to suppress Counter etc.
    public const int CSupport = 0x98;
    public const int CMovement = 0x9C;   // 3 bytes, base id 230; Rapture holds its teleport image here

    // --- crystal counter (band-entry relative, found live 2026-06-16) ---
    // Offset from the band entry base address. A KO'd unit's "3 hearts" crystallization countdown:
    // the engine steps 3->2->1->0 once per the dead unit's scheduled turn; 0 = unit crystallizes
    // (permanent loss). Equivalent to combat base +0x07. Holding it at SanctuaryHearts (3) stops
    // crystallization while the bearer lives. Found and pin-gating confirmed live this session.
    public const int ACrystalHearts = -0x15; // band entry -0x15 == combat base +0x07

    // --- dead / undead status bytes (band-entry relative) ---
    // Proven layout from Doom research (doom-status-bytes memory): Dead +0x45/0x20, Undead +0x45/0x10.
    // A unit is structurally dead when this bit is set regardless of HP -- the Phoenix-Down undead
    // kill is a scripted status-death whose HP write the 33ms poll may never observe.
    public const int ADeadStatus  = 0x45;  // u8 status bitfield byte shared by Dead and Undead flags
    public const byte ADeadBit    = 0x20;  // mask: bit 5 of ADeadStatus is the Dead flag
    public const byte AUndeadBit  = 0x10;  // mask: bit 4 of ADeadStatus is the Undead flag
    // Delayed-action bits on the same status byte (tools/probes/status_probe.py decode map;
    // confirmed via actor_attrib_probe.py watchweapon trace 2026-06-26):
    //   Jump 0x04 -- PROVEN LIVE: status[45] 00->04 at jump-commit, 04->00 at landing (~8.6s later).
    //   Charging 0x08 -- same mechanism. PROVEN LIVE 2026-06-26: SET observed (charging_probe.py); the
    //   untracked-arm cross-turn summon no-credit fires in-game (which requires the 1->0 landing edge). See LIVE_LEDGER.
    public const byte AJumpBit          = 0x04;
    public const byte AChargingBit      = 0x08;
    public const byte ADelayedActionMask = 0x0C;  // Jump | Charging

    // --- poison status bytes (band-entry relative, proven live 2026-06-09) ---
    public const int APoison      = 0x48;  // u8 status bitfield byte containing the poison flag
    public const byte APoisonBit  = 0x80;  // mask: bit 7 of APoison is the "poisoned" flag
    public const int APoisonTimer = 0x4A;  // u8 poison countdown timer (engine inits to 36; ticks per CT unit)

    // --- reraise (auto-revive) status byte (band-entry relative, PROVEN LIVE 2026-06-14) ---
    // Held re-applied through the death that clears it == the engine's OWN animated Reraise: a
    // lethal hit becomes a played-dead corpse the engine raises back at ~10% HP when its CT next
    // reaches 100. This is the FUNCTIONAL half of an item's "Permanent: Reraise" -- the status-page
    // text is equipment-derived UI, but the auto-revive itself is this single bit. Feign Death holds
    // it on the wielder (FeignDeath.cs); the death-commit clears it once, the hold re-stamps it.
    public const int AReraise     = 0x47;  // u8 status bitfield byte containing the Reraise flag
    public const byte AReraiseBit = 0x20;  // mask: bit 5 of AReraise is the "reraise" flag

    // --- invisible (transparent) status byte (band-entry relative, PROVEN LIVE 2026-06-14) ---
    // Shares +0x47 with Reraise; bit 4 (0x10). Held re-applied (it breaks the moment the unit acts),
    // it makes the AI ignore the unit -- single-target enemies skip it; AoE splash can still reach it.
    // Feign Death sets it through the played-dead window so the prone wielder acts unmolested, then
    // drops it for the finishing blow.
    public const int AInvisible     = 0x47;  // u8 status bitfield byte containing the Invisible flag
    public const byte AInvisibleBit = 0x10;  // mask: bit 4 of AInvisible is the "invisible" flag

    // --- marquee buff status bits (band-entry relative). The band status bytes equal the PSX
    //     current-status 5-byte field (byte N = +0x45+N), confirmed by six cross-checks against the
    //     proven bits (Dead/Undead +0x45, Reraise/Transparent +0x47, Poison +0x48, Doom +0x49) and a
    //     LIVE probe 2026-06-15 (Float +0x47/0x40 and Regen +0x48/0x40 set on real enemy units; one
    //     unit Float-only). Full bit map = FFTHandsFree StatusDecoder. Larceny steals these
    //     (LarcenyPolicy.Stealable); Regen is wired, Haste/Protect/Shell/Reflect are bit-confirmed.
    //     FLOAT is COSMETIC-ONLY when set via the bit (icon shows, the unit doesn't float -- its hover
    //     state lives elsewhere; proven live 2026-06-15), so Larceny does NOT steal it; AFloat stays
    //     for reference / status_probe. ---
    public const int AFloat   = 0x47;  public const byte AFloatBit   = 0x40;
    public const int ARegen   = 0x48;  public const byte ARegenBit   = 0x40;
    public const int AProtect = 0x48;  public const byte AProtectBit = 0x20;
    public const int AShell   = 0x48;  public const byte AShellBit   = 0x10;
    public const int AHaste   = 0x48;  public const byte AHasteBit   = 0x08;
    public const int AReflect = 0x49;  public const byte AReflectBit = 0x02;

    // --- auth band: LIVE unit data (the static array freezes on battle restart; the band stays live).
    //     Entry layout matches the static-array A* offsets. BandEntry = unit-copy offset inside
    //     each slot; BandReadBase starts at n=-24 (the lowest valid scan index).
    //     Sources: live probe -- fresh corpse 0/539 only in the band; Ramza real pos only there.
    public const int BandEntry = 0x1C;    // unit copy offset within a combat band slot
    /// <summary>Band-relative u16 equipped weapon id (= CWeapon - BandEntry = 0x04). The same
    /// byte GrowthEngine reads via mem.U16(addr + CWeapon) on the combat struct (n = s - 24),
    /// and SeatBand writes at e + (CWeapon - BandEntry). Algebraic identity:
    /// Band.Entry(s) + AWeapon = BandReadBase + s*CombatStride + AWeapon
    ///                         = CombatAnchor + (s-24)*CombatStride + CWeapon.</summary>
    public const int AWeapon = CWeapon - BandEntry;   // == 0x04
    // Band-relative reaction field: CReaction(0x94) - BandEntry(0x1C) = 0x78. 4 bytes.
    // Maim reads/holds/restores this to suppress the victim's Counter/etc. abilities.
    public const int AReaction = 0x78;
    // Band-relative movement field: CMovement(0x9C) - BandEntry(0x1C) = 0x80. 3 bytes.
    // Rapture saves/holds/restores this for the Master Teleportation window.
    public const int AMovement = 0x80;
    public const int ASupport = 0x7C;   // 4 bytes, base id 198, MSB-first; == CSupport(0x98) - BandEntry(0x1C). Choir OR-sets the Non-charge bit here.
    public const long BandReadBase = CombatAnchor + BandEntry - 24 * (long)CombatStride;  // n=-24 anchor
    public const int BandSlots = 49;     // n = -24..+24 around the anchor

    // --- display scratch (equipped-weapon menu WP, Ramza context) ---
    // 1.5 CONFIRMED LIVE 2026-06-17: MirrorWeapon - 0x1E; read 6 = Venombolt's WP with Ramza's card up.
    public const long WpScratch = 0x141876E96;   // 1.5 CONFIRMED +0x6660 (was 0x141870836)

    // --- battlefield discriminator: 0 = OUT of battle (world map / menus -- even when
    //     slot9 is still the stuck 0xFFFFFFFF sentinel), 2/3/4 = on the live battlefield.
    //     Verified in FFTHandsFree (CommandWatcher.cs). slot9 alone can't tell the
    //     world-map party menu from combat; this can, so the card paints there instead
    //     of only at game boot (the old "kills update only after restart" bug). ---
    public const long BattleMode = 0x1409069A0;   // 1.5 CONFIRMED +0x6350 (was 0x140900650): u8 3-in-battle/0-on-map, tracked across 3 transitions

    // --- in-battle "BattleStatus" card: checking a unit's status mid-battle opens the
    //     equip card (with the Kills line). Detected (per FFTHandsFree ScreenDetectionLogic)
    //     as pauseFlag==1 && menuCursor==3 (the Status action-menu slot) && submenuFlag==1.
    //     Lets the counter paint there too -- safe because it's a paused, stable menu. ---
    // 1.5 CONFIRMED LIVE 2026-06-17 (display_probe consistency-sample + watch): the pause byte
    // reads 1 while a menu/Status card is open, 0 on the free battlefield / enemy turns. Found via
    // a 10Hz constant-1-while-paused / constant-0-while-running intersection (a 3-frame diff was
    // swamped by animated UI bytes), then confirmed flipping 0->1->0 on a live card open/close.
    // Two synced copies at 0x140C6B1C8 / 0x140C6B307; using the lower. (was 0x140C64A5C, +0x676C)
    public const long PauseFlag = 0x140C6B1C8;
    public const long MenuCursor = 0x1407FC620;   // 1.5 PRE-1.5/UNUSED: StatusCardOpen does not gate on it ("the card's own cursor once open")
    // 1.5 CONFIRMED LIVE 2026-06-17: u8 == 1 only when the Status card is open (0 on the free
    // battlefield, enemy turns, AND the plain command menu). Found by 3-state solve (live/menu/card)
    // and reconfirmed across sessions; isolated. (was 0x140D3A10C, +0x6752)
    public const long SubmenuFlag = 0x140D4085E;

    // --- equip-screen "mirror": the VIEWED unit's equipped gear in UI row order,
    //     [Weapon, LHand, Helm, Body, Accessory] as u16. Mirror[0] = the weapon whose
    //     card is on screen, so the in-card Kills counter knows WHICH weapon to show.
    //     Verified in FFTHandsFree (CommandWatcher.cs, 2026-04-15). Two synced copies. ---
    // 1.5 CONFIRMED LIVE 2026-06-17 (two-card differential): the u16 read 80 on Ramza's card and 56
    // on the Umbral Rod card -- the only addr that tracked both. Delta +0x6660, NOT the predicted
    // +0x6450 (the 0x14187 region slid further than the combat band -- shifts are non-monotonic).
    public const long MirrorWeapon = 0x141876EB4;   // 1.5 CONFIRMED +0x6660 (was 0x141870854)
    public const long MirrorOffHand = 0x141876EB6;   // 1.5 CONFIRMED +0x6660 (was 0x141870856). mirror[1]: viewed off-hand; read 143 (Ramza's shield)

    // --- inventory item count array ---
    // Source: docs/DEV_TEST_RECIPES.md (inventory-give recipe, give_all_items probe).
    // count[itemId] = u8 @ InventoryCountBase + itemId.  Read/write via IGameMemory so
    // the seam is testable.  Do NOT read or write mid-battle (gated by Engine.Tick !nowIn).
    public const long InventoryCountBase = 0x1411A7C00;   // 1.5 CONFIRMED +0x6440 (was 0x1411A17C0): dev give-all inventory present at predicted addr
    /// <summary>Scholar's Ring item id.  Treasure Master needs this in a deployed unit's
    /// accessory slot to enable tile-highlight; ring-grant ensures the player always has at
    /// least one available.</summary>
    public const int ScholarRingItemId = 260;

    // --- Treasure Master: map identity + terrain grid ---
    // u8, current battle's map id; valid 1..127 (FFTHandsFree LiveBattleMapId contract).
    // Ported from FFTHandsFree GameBridge/LiveBattleMapId.cs; found 2026-04-19 via
    // snapshot/diff; verified on 3 maps (Dugeura=86, Beddha=82, Araguay=80) + across restart.
    // STALE out of battle: only read when InLiveBattle is true.
    public const long LiveBattleMapId = 0x140784478;   // 1.5 RE-FOUND 2026-06-17 +0x6C3C (was 0x14077D83C): two-map differential (reads 76 on Zeklaus, 80 on Araguay, unique hit)

    // Static per-map terrain records, 7 bytes/tile; used read-only as the map-identity
    // fingerprint source (FNV-1a64 over a fixed-length prefix).
    // 1.5 RE-FOUND 2026-06-17 +0x6440 (was 0x140C65000): the live v2 hash at this start matched
    // map 80 (Araguay)'s STORED pre-1.5 fingerprint exactly -- proving the terrain DATA is unchanged
    // on 1.5, so every captured map's stored fpHash stays valid (no re-fingerprint needed).
    public const long TerrainGrid = 0x140C6B440;
}
