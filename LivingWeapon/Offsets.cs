namespace LivingWeapon;

/// <summary>
/// Verified game addresses. The image base is fixed at 0x140000000 with no ASLR,
/// and every address below lives in the always-mapped main module, so they are
/// valid in-process pointers we can read/write directly (no AoB, no syscalls).
/// Sources: FFTHandsFree/docs/BATTLE_MEMORY_MAP.md and the BattleTracker the
/// detection logic is ported from.
///
/// 1.5.1 (Steam buildid 23901820) audit 2026-07-13 (docs/research/PORT_1.5.1_OFFSETS.md): all
/// absolutes below were re-verified live at their UNCHANGED 1.5 addresses except
/// <see cref="SubmenuFlag"/> (the one mover). ArrayBase and EventId are behaviorally provisional
/// pending a post-deploy battle; dev-spike constants (BodyDoubleSpike/StatusSpike, #if LWDEV)
/// were deliberately NOT re-verified this pass and stay stale-flagged.
/// </summary>
internal static class Offsets
{
    // --- in-battle flags ---
    public const long Slot0 = 0x140782A30;   // 1.5 CONFIRMED +0x6000 (was 0x14077CA30): u32 battle-phase word; four edge
                                             //   samples 2026-07-21 + the LW-40 probe (LIVE_LEDGER 1.5 slot0 battle-phases
                                             //   row; see Slot0InBattleMarker for the values).
    public const long Slot9 = 0x140782A54;   // 1.5 CONFIRMED +0x6000 (was 0x14077CA54): read 0xFFFFFFFF at all four
                                             //   battle enter/exit edges on the same 2026-07-21 log.

    /// <summary>The slot0 in-battle marker VALUE on 1.5 (pre-1.5 it was 0xFF; the quit-stick trap
    /// and the 0x66 victory-clear are pre-1.5 observations, sentinel_probe.py 2026-06-10).
    /// Live 1.5.1 values sampled at the four battle-edge trace lines of the 2026-07-21 log
    /// (durable record: the LIVE_LEDGER "1.5 slot0 battle phases" Uncertain row): 0xFFFFFFFF at
    /// both battle-load churn edges, 0x10 at the real enter (mode 3), 0x11 at the victory exit.
    /// Whether 0x10 persists through mode-1/5 stretches mid-battle is inherited from the
    /// pre-1.5 marker behavior and AWAITING-LIVE (LW-42, owner slow-cast eyeball): if that
    /// premise is wrong, the excuse paths anchored on this value are merely dead (the pre-fix
    /// behavior), never wrongly live.</summary>
    public const uint Slot0InBattleMarker = 0x10;
    public const long Acted = 0x140782A8C;   // 1.5 CONFIRMED (was 0x14077CA8C): rising edge = an action completed.
                                             //   Production-proven -- TurnTracker/KillTracker ship on it; live log 2026-07-01.
    public const long EventId = 0x140782A94; // 1.5 CONFIRMED live 2026-07-08 (was 0x14077CA94). u16 event file number during cutscenes/dialogue; ALIASES as
                                             //   the active unit's nameId during combat animations -- only
                                             //   meaningful while out of live battle (dialogue/cutscene gate).
                                             //   Reads 0xFFFF at the menu, then climbs 2 -> 4 -> 5 through the
                                             //   Orbonne prologue; PlaythroughReset.Policy.OpeningEventId (2)
                                             //   anchors LW-51 Tier-1's new-game reset on this value.

    /// <summary>Engine global holding a POINTER to the ACTING unit's combat FRAME base (see
    /// <see cref="FrameReadBase"/>; frame + <see cref="BandEntry"/> = that unit's band entry).
    /// Found via FFTMultiplayer's action_record_probe / doaction-target-redirect memory; live-proven
    /// 2026-07-01 by tools/probes/unitid_probe.py "watch": during a 2x id-42 repro it named each
    /// acting wielder's own seat + weapon 42 at the exact instant the turn-queue stat fingerprint
    /// (<see cref="TurnQueue"/>) was ambiguous, and named enemy seats on enemy turns. Reads 0x0 at
    /// battle-open idle (observed once); may name the REACTOR during a reaction (FFTMultiplayer
    /// caveat, unverified here). NOTE: the sibling FFTMultiplayer/doaction formula divides from base
    /// 0x141853CE0 -- a DIFFERENT slot-origin convention (their 16-slot array base == our seat 8's
    /// frame), NOT a contradiction; the seat math here uses <see cref="FrameReadBase"/> 0x141852CE0
    /// (probe-validated independently).</summary>
    public const long ActorPtr = 0x14186AF68;

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
    public const int ABrave    = 0x0E; // u8   origBrave (roster-match fingerprint; re-normalizes, never displays; the locate fingerprint for Wielder -- NEVER write this)
    /// <summary>u8 band-relative CURRENT brave (= CBraveCurrent 0x2B - BandEntry 0x1C = 0x0F).
    /// Effective + displayed brave; the band-entry byte Kobu reads (enemy) and write-holds (wielder).
    /// ABrave 0x0E is the orig/decoy: it re-normalizes, never displays, and is the locate fingerprint.
    /// DO NOT confuse with CBraveCurrent (0x2B, combat-struct-relative): band_entry+0x2B = combat+0x47
    /// = the Reraise/Invisible/Float STATUS bitfield (AReraise), NOT brave.
    /// Proven layout: brave-faith-current-vs-orig-offsets (FFTMultiplayer 2026-06-20).</summary>
    public const int ABraveCurrent = 0x0F;
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
    /// Treat as: write target always; clean enemy-turn read; do NOT trust for the player's own turns.
    /// NOTE 2026-07-01: Iai no longer reads this for its release signal (rebuilt on ActorPtr); still
    /// live for ExtraTurn's write and Maim/CharmLock/FeignDeath/SpiritualFont/Plague/Rapture reads.</summary>
    public const int ACtSlam   = 0x25;
    /// <summary>u8 band-relative READ byte for counting a unit's completed turns: CT seen
    /// at/above 90, then falls below 70 = one turn taken. Live-proven by Maim (victim-turn
    /// counting) and CharmLock. CtTurns feeds off this offset. Equals combat base+0x25.</summary>
    public const int ACtTurn   = 0x09;

    // --- roster (nameId -> equipped right hand) ---
    public const long RosterBase = 0x1411A7D10;   // 1.5 CONFIRMED +0x6440 (was 0x1411A18D0): slot0=Ramza (lvl99/rhand80/nameId1), slots +1..+7 = real party
    public const int RosterStride = 0x258;
    /// <summary>Ceiling, not a floor. Observed live 2026-07-21 by tools/probes/roster_span_probe.py
    /// on a 46/50 save: rows 0..45 occupied, 50 contiguous rows at the same 0x258 stride
    /// throughout. Slots 50+ are a STALE GUEST bank carrying duplicate unit identities (a cloned
    /// Beowulf row with matching level/brave/faith): scanning it would make fingerprint-keyed
    /// resolves ambiguous and the bridge would refuse (units going dark). Never raise this past
    /// 50 without a fresh live probe proving the new span is real, contiguous, and free of
    /// duplicate identities.</summary>
    public const int RosterSlots = 50;
    /// <summary>u8 roster-relative SpriteSet id (Dicene UnitData +0x00): the battle body/model
    /// selector. LIVE-PROVEN 2026-07-06 (docs/research/SPRITE_SWAP.md + a live roster probe against
    /// a real party monster): 0x82 confirmed MONSTER; generics 0x80 male / 0x81 female; story bodies
    /// (0x02 a Ramza/Delita chapter, 0x16 Mustadio, 0x1E Agrias, ...) all read well under 0x80. Guest
    /// bodies 0xA2 Balthier / 0xA3 Luso / 0xA5 Argath Deathknight also render as ordinary humans.
    /// AttackRow.Policy.HumanSprite is the gate built on this fact: the Attack row's "Fists"
    /// treatment (an unarmed HUMAN) must never fire for a monster's empty hand, so it fails CLOSED
    /// on any value it cannot positively place, never guessing "human".</summary>
    public const int RSprite = 0x00;
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
    /// <summary>Base address for combat-FRAME index i=0 (i.e. i*CombatStride below CombatAnchor's own
    /// slot n=24). Algebraic identity: FrameReadBase == BandReadBase - BandEntry (both anchor on the
    /// same n=-24 slot; a frame is a band entry minus the 0x1C band offset). Matches the probe's
    /// FRAME_BASE0 0x141852CE0 (tools/probes/unitid_probe.py, RE-checked 2026-07-01). Used to turn an
    /// <see cref="ActorPtr"/> read into a band seat: seat = (ptr - FrameReadBase) / CombatStride.</summary>
    public const long FrameReadBase = CombatAnchor - 24 * (long)CombatStride;
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
    //   Jump 0x04 -- OBSERVED LIVE: status[45] 00->04 at jump-commit, 04->00 at landing (~8.6s later).
    //   Charging 0x08 -- same mechanism. OBSERVED LIVE 2026-06-26: SET observed (charging_probe.py); the
    //   untracked-arm cross-turn summon no-credit fires in-game (which requires the 1->0 landing edge). See LIVE_LEDGER.
    public const byte AJumpBit          = 0x04;
    public const byte AChargingBit      = 0x08;
    public const byte ADelayedActionMask = 0x0C;  // Jump | Charging

    // --- poison status bytes (band-entry relative, proven live 2026-06-09) ---
    public const int APoison      = 0x48;  // u8 status bitfield byte containing the poison flag
    public const byte APoisonBit  = 0x80;  // mask: bit 7 of APoison is the "poisoned" flag
    public const int APoisonTimer = 0x4A;  // u8 poison countdown timer (engine inits to 36; ticks per CT unit)

    // --- reraise (auto-revive) status byte (band-entry relative, OBSERVED LIVE 2026-06-14) ---
    // Held re-applied through the death that clears it == the engine's OWN animated Reraise: a
    // lethal hit becomes a played-dead corpse the engine raises back at ~10% HP when its CT next
    // reaches 100. This is the FUNCTIONAL half of an item's "Permanent: Reraise" -- the status-page
    // text is equipment-derived UI, but the auto-revive itself is this single bit. Feign Death holds
    // it on the wielder (FeignDeath.cs); the death-commit clears it once, the hold re-stamps it.
    public const int AReraise     = 0x47;  // u8 status bitfield byte containing the Reraise flag
    public const byte AReraiseBit = 0x20;  // mask: bit 5 of AReraise is the "reraise" flag

    // --- invisible (transparent) status byte (band-entry relative, OBSERVED LIVE 2026-06-14) ---
    // Shares +0x47 with Reraise; bit 4 (0x10). It makes the AI ignore the unit -- single-target
    // enemies skip it; AoE splash can still reach it. Feign Death sets it through the played-dead
    // window so the prone wielder acts unmolested, then drops it for the finishing blow.
    //
    // CORRECTED 2026-07-22 (measured live, mod off; LIVE_LEDGER "orphan flag" Uncertain row). This
    // block used to say the bit "breaks the moment the unit acts". IT DOES NOT. A raw write here
    // survives the unit's own action untouched and survives 60s of running clock with no re-stamp.
    // What clears it is BEING HIT, because the engine's damage-resolution path strips Transparent
    // unconditionally. So a held hide still needs re-stamping, for splash rather than for acting.
    // WHY: +0x47 is the COMPOSED layer, which is a read-out. The engine registers a status it
    // applies in the INFLICTED layer (+0x1D3..+0x1D7). A composed-only write is an ORPHAN FLAG that
    // the HUD icon renders and the AI reads, but that nothing owns, so no visible effect is
    // performed (the unit does NOT go transparent) and nothing ever expires it. Measured on one
    // unit carrying both: ours read composed 0x10 / inflicted 0x00, while an engine-applied Stop on
    // the same unit read composed 0x02 AND inflicted 0x02 with a real effect and a real expiry.
    // Corollary for anything that HOLDS this bit: it does not decay on its own, so a stuck hold
    // needs a watchdog rather than a hope.
    public const int AInvisible     = 0x47;  // u8 status bitfield byte containing the Invisible flag
    public const byte AInvisibleBit = 0x10;  // mask: bit 4 of AInvisible is the "invisible" flag

    // --- marquee buff status bits (band-entry relative). The band status bytes equal the PSX
    //     current-status 5-byte field (byte N = +0x45+N), confirmed by six cross-checks against the
    //     proven bits (Dead/Undead +0x45, Reraise/Transparent +0x47, Poison +0x48, Doom +0x49) and a
    //     LIVE probe 2026-06-15 (Float +0x47/0x40 and Regen +0x48/0x40 set on real enemy units; one
    //     unit Float-only). Full bit map = FFTHandsFree StatusDecoder. Larceny steals these
    //     (LarcenyPolicy.Stealable); Regen is wired, Haste/Protect/Shell/Reflect are bit-confirmed.
    //     FLOAT is COSMETIC-ONLY when set via the bit (icon shows, the unit doesn't float -- its hover
    //     state lives elsewhere; observed live 2026-06-15), so Larceny does NOT steal it; AFloat stays
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
    /// <summary>u8 band-relative Speed byte: CSpeed(0x40) - BandEntry(0x1C) = 0x24.
    /// LIVE-VERIFIED 2026-07-01: write-50 to entry+0x24 -> displayed Speed 50 + unit goes first.
    /// LIVE_LEDGER band +0x22/0x23/0x24 = PA/MA/Speed. Use this (not CSpeed) on a band entry.</summary>
    public const int ASpeed = CSpeed - BandEntry;   // == 0x24
    /// <summary>u16 band-entry-relative roster-nameId back-reference: the frame +0x1FC field
    /// (0x1FC - BandEntry(0x1C) = 0x1E0) mirrors that unit's roster nameId (<see cref="RNameId"/>,
    /// roster +0x230). Found + live-proven 2026-07-01 by tools/probes/unitid_probe.py "find"
    /// SCAN-A across TWO separately-loaded battles: player seats exact-match roster nameId
    /// (Ramza 1, Samurai 298, Ninja 271, both battles); enemy seats read DISTINCT sane values
    /// (918, 992, 1008, 830, 874, 966, 838, 1014, 747, 516, 366, 1003). A revolving engine MIRROR
    /// frame (band seat 28, observed cloning different real units over time) carries the mirrored
    /// unit's nameId too (298, later 271) -- nameId does NOT distinguish the original seat from a
    /// mirror copy. But the ACTOR POINTER (<see cref="ActorPtr"/>, via Band.ActorEntry) always
    /// names the REAL frame, so identity-matching the pointer target's nameId against a captured
    /// roster nameId is unambiguous for Iai's release even when Wielder.Locate ambiguity-bails on
    /// a churning mirror. Arm-time capture source: Wielder.RosterNameId (roster +0x230).
    /// Consumed by the LOCATE LAYER (Plan v2 D1/D2/D5/D7): Wielder.Locate/LocateAll,
    /// GrowthEngine.LocateIn/ScanEntries/MatchesEntry/ReadHp, and RingGate.BandHasUnit all read
    /// this field as their tier-1 exact-match predicate and their tier-2 veto (a foreign nonzero
    /// value excludes an fp-colliding entry; 0/unreadable never blocks a match).</summary>
    public const int ANameId = 0x1E0;
    // Band-relative reaction field: CReaction(0x94) - BandEntry(0x1C) = 0x78. 4 bytes.
    // Maim reads/holds/restores this to suppress the victim's Counter/etc. abilities.
    public const int AReaction = 0x78;
    // Band-relative movement field: CMovement(0x9C) - BandEntry(0x1C) = 0x80. 3 bytes.
    // Rapture saves/holds/restores this for the Master Teleportation window.
    public const int AMovement = 0x80;
    public const int ASupport = 0x7C;   // 4 bytes, base id 198, MSB-first; == CSupport(0x98) - BandEntry(0x1C). Choir OR-sets the Non-charge bit here.

    // --- per-unit turn/moved/acted flags (band-entry-relative; PROVEN LIVE 2026-07-09) ---
    // Source: docs/LIVE_LEDGER.md's "Per-unit turn/moved/acted flags (the full-wait read)" row
    // (owner live-verified 2026-07-09: Mushin BANK on a still wait, SPENT on the strike);
    // FFHacktics PSX struct 0x186-0x189, mapped live by tools/probes/mushin_wait_probe.py
    // (scratchpad/psxflags_watch.log). PSX offset + 0x32 = frame offset; frame offset -
    // BandEntry(0x1C) = band offset, the same AArec/ANameId convention every other frame-window
    // field in this codebase already uses. Promoted from Mushin.cs's own local consts (LW-55
    // stage 1; Mushin's round-5 doc originally forbade the promotion mid-commit-staging, moot now).
    /// <summary>1 while the unit's move/act/wait menu is open; 0-&gt;1 at turn open, 1-&gt;0 at turn
    /// end. The falling edge (1-&gt;0) is the turn-end decision point Mushin's trigger reads, and
    /// LW-55's CursorGate reads it as gate B (turn-ownership) for the Attack card's cursor resolve.</summary>
    public const int ATurnFlag = 0x19C;   // u8
    /// <summary>0-&gt;1 at the unit's move. Reset to 0 by the ENGINE at that unit's NEXT turn open.</summary>
    public const int AMoved    = 0x19D;   // u8
    /// <summary>0-&gt;1 at the unit's action. Same engine reset-at-open as <see cref="AMoved"/>.
    /// PSX 0x189 (frame +0x1BB, band +0x19F, "Ability Outcome": 0x02 hit-by-ability, 0x01
    /// turn-ended) is documented on the LIVE_LEDGER row but not promoted here: nothing in this
    /// codebase consumes it.</summary>
    public const int AActed    = 0x19E;   // u8

    public const long BandReadBase = CombatAnchor + BandEntry - 24 * (long)CombatStride;  // n=-24 anchor
    public const int BandSlots = 49;     // n = -24..+24 around the anchor

    // --- AREC kill diagnostic (band-entry-relative; D4 of the kill-attribution plan) ---
    /// <summary>u8-array-relative per-unit ACTION RECORD, DIAGNOSTIC ONLY (the credit path never
    /// consults this): = frame-relative FR_AREC (0x1A0) - BandEntry (0x1C) = 0x184. Sub-offsets
    /// (relative to AArec): +0x0 idx (engine index == seat-8, SOLID), +0x2 abil u16 (ability id),
    /// +0xA kind (5=performing/6=receiving, PROBABLE), +0xB xref (candidate victim&lt;-&gt;attacker
    /// cross-reference, UNPROVEN -- one observation each direction). Provenance: the 17:57
    /// all-seat capture, tools/probes/unitid_probe.py "watch", 2026-07-01; docs/LIVE_LEDGER.md's
    /// Uncertain AREC row is the durable evidence home. Guarded read (Readable) at CreditKill time;
    /// skip silently when unreadable.</summary>
    public const int AArec = 0x184;

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
    // 1.5.1 SEMANTICS CHANGE (observed live 2026-07-13, address unchanged): this byte now reads 1
    // ONLY while the unit status card is itself open; it reads 0 during the command menu and the
    // abilities list. On 1.5 it held 1 across the whole player turn (idle + menu + card). Callers
    // that treat "paused" as a broad player-turn signal (BattleState.InLiveBattle's excuse clause,
    // the #if LWDEV dev-spike gates) now see a narrower true window; see
    // docs/research/PORT_1.5.1_OFFSETS.md.
    public const long PauseFlag = 0x140C6B1C8;
    public const long MenuCursor = 0x1407FC620;   // 1.5 PRE-1.5/UNUSED: StatusCardOpen does not gate on it ("the card's own cursor once open")
    // 1.5 CONFIRMED LIVE 2026-06-17: u8 == 1 only when the Status card is open (0 on the free
    // battlefield, enemy turns, AND the plain command menu). Found by 3-state solve (live/menu/card)
    // and reconfirmed across sessions; isolated. (was 0x140D3A10C, +0x6752)
    // 1.5.1 MOVED (delta -0x52, struct-local reshuffle): the old 0x140D4085E address reads 0 in
    // every game state on 1.5.1 (dead byte). Re-found 2026-07-13 by a consistency-sampled 3-state
    // solve (12 samples per state at 0.4s: card=1 constant, command-menu=0, field=0), plus
    // discriminators: reads 1 in the card via BOTH paths (own-turn Status command AND the
    // pause-menu Units > Status route), reads 0 in the abilities list (which rejected the
    // generic-panel decoy candidate 0x140D40554), and 0 post-battle. Synced sibling behaves
    // identically at 0x140D407BA. See docs/research/PORT_1.5.1_OFFSETS.md.
    public const long SubmenuFlag = 0x140D4080C;

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

    // --- Provoke (LW-123 arc 1): the ability ACTION table (InflictStatus repoint target) and the
    // hand-authored inflict-status table (the mark the repoint applies). Content-anchored, image-
    // static addresses -- observed live 2026-07-22 (LIVE_LEDGER row, Uncertain; docs/PROVOKE_AC.md
    // "How it works, plainly"), the same anchor class as Barrage.AbilityBase (a JobCommand table),
    // just two tables further into the exe. COVERED BY LAUNCHGUARD INDIRECTLY BUT COMPLETELY: a
    // patched executable fails the PE build-key landmark (LaunchGuard.Landmarks.cs
    // ExpectedTimeDateStamp/ExpectedSizeOfImage) and the guard stands the whole mod down
    // permanently before any write to either table happens -- these are not a separately-guarded
    // class, only addresses that (like every other one in this file) need a re-find on a re-anchor
    // (docs/PATCH_REANCHOR.md). The BYTE-IDENTICAL decoy mirror of the action table
    // (ProvokePolicy.DecoyActionTable, Provoke.Policy.cs) is deliberately NOT pinned here: nothing
    // in the runtime ever writes it, so it is a policy-level safety constant, not a write anchor.
    public const long LiveActionTable = 0x14078B2DC;   // 368 rows x 20 bytes; the copy the engine and UI both read
    public const long InflictTable = 0x14080FBA0;      // 128 rows x 6 bytes, [mode][s0..s4], mode byte FIRST
}
