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
///
/// 1.5.2 (Steam buildid 24304240) audit 2026-07-24 (docs/research/PORT_1.5.2_OFFSETS.md): NO
/// address in this file changed. The audit was done OFFLINE by diffing the 1.5.1 and 1.5.2 exes on
/// disk (tools/probes/exe_reanchor_scan.py, tools/probes/rip_xref_reanchor.py): the section VA
/// layout is identical up to the packed tail, and every runtime global here was re-read out of the
/// game's OWN RIP-relative instructions in both builds, which agree on the same address (up to
/// 33/33 referencing sites for <see cref="RosterBase"/>). That is strong evidence about ADDRESSES
/// and says nothing about SEMANTICS: the 1.5.1 pause-flag lesson (address survived, meaning
/// narrowed) applies, so the post-deploy live pass still owns behavior. The one address the mod
/// moved for 1.5.2 lives outside this file (PromptSwapHook.FnSetTextString, +4).
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
    public const int TqTeam  = 0x02;   // u16  0 = player, 1 = enemy, 2 = ally/guest (corroborated
                                       //      KillTracker.Corpses.cs:233 + docs/research/PORT_1.5.md)
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
    /// <summary>u8 band-relative byte whose bit 7 flags the unit's terrain LAYER (bridge decks
    /// above/below share (gx,gy) but occupy different pathfinder layers): combat +0x51 -
    /// BandEntry (0x1C) = 0x35. Bulwark (docs/BULWARK_AC.md criterion 8) reads this so a plant
    /// only blocks the wielder's OWN layer, never the deck above or below. The SAME byte's low 2
    /// bits are the unit's FACING (0=South, 1=West, 2=North, 3=East), live-proven to track the
    /// Wait facing wheel 2026-07-28 (LIVE_LEDGER row "Unit FACING is band +0x35 low 2 bits"). The
    /// Y-axis look direction was CORRECTED 2026-07-28 06:15 by a live pass: North (2) looks toward
    /// +y and South (0) looks toward -y -- the inherited "y+ = south" convention was backwards for
    /// this grid (see BulwarkPolicy.BehindTile). Bulwark reads facing and layer off this one byte
    /// in a single read (Bulwark.Terrain.cs's Plant).</summary>
    public const int ALayerBit = 0x35;   // u8, bit 0x80 = layer index, low 2 bits = facing
    /// <summary>u8 band-relative WRITE TARGET: slam this to 100 to inject a scheduler turn
    /// (ExtraTurn.CtOff). Matches combat base+0x41. Do NOT read this for turn counting --
    /// a live watcher saw zero transitions; the write takes, but reads don't tick reliably.
    /// NOTE 2026-06-14 (ct_watch / finish_watch probes): for a PLAYER unit this byte CAN read and
    /// climb to 100 (+0x09 stays flat 0, the Rapture wall), and it reads cleanly for ENEMY
    /// turns -- but on the player's OWN actively-managed unit it read INCONSISTENTLY (clean 100 in one
    /// probe, but stale/frozen ~85 during the unit's own input menu in the live DLL). So counting the
    /// player wielder's OWN turns off it proved unreliable; FeignDeath uses a wall clock instead.
    /// Treat as: write target always; clean enemy-turn read; do NOT trust for the player's own turns.
    /// NOTE 2026-07-01: Iai no longer reads this for its release signal (rebuilt on ActorPtr); still
    /// live for ExtraTurn's write and Maim/FeignDeath/SpiritualFont/Plague/Rapture reads.</summary>
    public const int ACtSlam   = 0x25;
    /// <summary>u8 band-relative READ byte for counting a unit's completed turns: CT seen
    /// at/above 90, then falls below 70 = one turn taken. Live-proven by Maim (victim-turn
    /// counting). CtTurns feeds off this offset. Equals combat base+0x25.</summary>
    public const int ACtTurn   = 0x09;
    /// <summary>u8 band-relative job id (PSX "Current Job", combat base +0x03): the only NEGATIVE
    /// offset in THIS section's A* fields (ACrystalHearts and AGateByte, in other sections of this
    /// file, are negative too) -- it reaches BACKWARD out of the band entry
    /// into combat-struct territory below BandEntry (0x1C), the opposite direction of every other
    /// field in this block. Do not mistake it for an in-entry field. Promoted from Puppeteer.Policy's
    /// private JobOff constant (LW-149 stage H); Puppeteer.Policy.JobOff is now a one-line alias to
    /// this constant. CONFIRMED live 2026-06-18 (Ramza 83 Thief, party Dark Knight 94, enemies
    /// Goblin 99 / Bonesnatch 110 / Wisenkin 124-127) -- but the byte is NOT a reliable unit FILTER
    /// (bosses read story/special ids like 37), so Puppeteer.IsDominatable ignores it.</summary>
    public const int AJob      = -0x19;

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
    public const int RShield = 0x1A;   // u16 equipped SHIELD id -- a SEPARATE slot from ROffHand (same 2026-06-21
                                       // dual-gun roster dump as the ROffHand note above). GunSlinger's twin grant
                                       // writes only +0x18 and never reads this slot; LW-193 (gear loss) probes here.
    public const int RSupport = 0x0A;   // u16 picked support ability KEY (empty = 0; Dual Wield = 477 = live id 221 + 256; reaction +0x08 / movement +0x0C siblings; LW-168 owner-observed 2026-08-12. FFTHandsFree's "u8 id" reading was the low byte)
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
    /// <summary>u8-array-relative per-unit ACTION RECORD: = frame-relative FR_AREC (0x1A0) -
    /// BandEntry (0x1C) = 0x184. Sub-offsets (relative to AArec): +0x0 idx (engine index ==
    /// seat-8, SOLID), +0x2 abil u16 (ability id, <see cref="ArecAbil"/>), +0xA kind (<see
    /// cref="ArecKind"/>, 5=performing/6=receiving), +0xB xref (candidate victim&lt;-&gt;attacker
    /// cross-reference, UNPROVEN and REFUTED as a kill-attribution shortcut -- see
    /// docs/LIVE_LEDGER.md's Uncertain AREC row). KillTracker.CreditKill's own attribution never
    /// consults this record (it stays a diagnostic log line there, gated on Tuning.VerboseEvents);
    /// LW-167 stage 4's Living Poach basic-Attack discriminator (LivingPoach.ReadWasBasicAttack)
    /// is the one production consumer, reading the KILLER's own record at credit time (LIVE_LEDGER
    /// "The basic-Attack discriminator (LW-167 stage 4)" row, 2026-08-12). Guarded read (Readable)
    /// at every consultation; skip silently when unreadable.</summary>
    public const int AArec = 0x184;
    /// <summary>u16 AArec-relative ability id: 0 (<see cref="LivingWeapon.Tuning.BasicAttackAbilityId"/>)
    /// means the record names the basic Attack command; any other value is an ability id. Read
    /// only alongside <see cref="ArecKind"/> == <see cref="ArecKindPerforming"/> (LivingPoach's
    /// stage-4 discriminator).</summary>
    public const int ArecAbil = 0x2;
    /// <summary>u8 AArec-relative kind tag: <see cref="ArecKindPerforming"/> (5) == the record
    /// names THIS unit's own pending action; 6 == receiving (a struck unit's stale last-action
    /// stamp, noise for the discriminator). PROBABLE per the original probe, now load-bearing for
    /// LivingPoach's basic-Attack discriminator (2026-08-12 owner probe, LIVE_LEDGER row above).</summary>
    public const int ArecKind = 0xA;
    /// <summary>The <see cref="ArecKind"/> value meaning "performing" (this unit's own pending
    /// action, read at the KILLER's entry).</summary>
    public const byte ArecKindPerforming = 5;

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
    // (the VANILLA block; the armed extended inventory relocates it, ExtendedInventory.BagCountBase)
    public const long InventoryCountBase = 0x1411A7C00;   // 1.5 CONFIRMED +0x6440 (was 0x1411A17C0): dev give-all inventory present at predicted addr
    // --- Bulwark (Sunderer +3, docs/BULWARK_AC.md): the PATHFINDER's own live terrain grid, 8
    // bytes/tile. idx = x + y*mapWidth + layerBit*0x100. BASE CORRECTED 2026-07-28: the true
    // record base is 0x140D8DCB0, NOT 0x140D8DCC0 -- the old base was 16 bytes (2 records) high,
    // so every write landed 2 tiles east of target, per the disasm operand
    // [rdx + idx*8 + 0xD8DCB2] (record byte +2 lives at 0x140D8DCB2, which only resolves against
    // the 0xB0 base). The real walkability lever is byte +6 bit 0x02 (PathTerrainVetoBit): the
    // engine's OWN obstacle state -- this map's five natural trees all read byte+6 == 0x22, bit
    // 0x02 set. OR'ing it in blocks movement AND enemy AI pathing while the tile stays
    // hoverable/selectable, rendering the game's own red circle-slash "invalid destination"
    // cursor. Bit 0x01 also blocks but additionally strips the tile from the cursor mask
    // (rejected -- the player couldn't even hover it). HEIGHT (byte +2) is NOT a walkability
    // input: raising it left a tile selectable and SOFTLOCKED the game when stepped on
    // (contradicted 2026-07-28). Grid writes PERSIST for the whole process session (across battle
    // restarts and onto the world map) -- stale dirt once crashed the game, so restore is
    // mandatory on every path that ends a hold, including battle exit. Cite: LIVE_LEDGER.md
    // Contradicted-section terrain entry, settled live 2026-07-28. NOT the same table as
    // TerrainGrid above (0x140C6B440, Treasure Master's read-only fingerprint scratch) --
    // cross-referenced so nobody writes the wrong one.
    public const long PathTerrainGrid = 0x140D8DCB0;
    public const int PathTerrainStride = 8;
    public const int PathTerrainVetoField = 6;
    public const byte PathTerrainVetoBit = 0x02;

    /// <summary>u8 pair (W, H): the live map's width then height. Matched 11x12 and 10x18 on the
    /// two live probe maps 2026-07-27, and the pathfinder's own width register (r14 = 11) agreed
    /// with the pair on the 11-wide map (docs/BULWARK_AC.md A3). Every consumer MUST carry the
    /// runtime sanity gates BulwarkPolicy.DimsSane enforces -- this pair is read out of a live
    /// battle-phase struct, not a validated table, so a garbage read must refuse rather than plant.</summary>
    public const long MapDimsWH = 0x140C6AD6A;

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

    // --- Provoke hold (LW-123 arc 2a): side membership + the ghost-seat gate, BOTH READ-ONLY -- the
    // hold never writes either byte, only reads them to decide who counts as an on-field enemy/ally. ---
    /// <summary>u8 band-relative friend/foe byte (= combat +0x1EE). Bit <see cref="AFriendFoeEnemyBit"/>
    /// set = enemy, clear = ally. GUEST-COMPLETE: the guardian probe (live 2026-07-22) read this bit
    /// clear on all 6 player-side seats including 5 guests, none of them in the "classic party" seat
    /// range -- so side membership must be read from this bit, never inferred from seat position.</summary>
    public const int AFriendFoe = 0x1D2;
    public const byte AFriendFoeEnemyBit = 0x10;

    /// <summary>u8 band-relative on-field gate (= combat +0x01). Reads <see cref="AGateHiddenValue"/>
    /// on a ghost/cutscene seat that <see cref="Band.IsValid"/> alone does not catch (the hide/reveal
    /// PROVEN row + the guardian probe, both live 2026-07-22). The hold skips any seat reading this
    /// value before ever touching it -- Band.IsValid runs first, so a fail-safe-0 gate read on an
    /// already-garbage seat is rejected before this check is even reached.</summary>
    public const int AGateByte = -0x1B;   // band entry - 0x1B == combat +0x01
    public const byte AGateHiddenValue = 0xFF;

    // --- Living Poach (LW-167): the Poacher's Den carcass store -- u8[96], one byte per
    // poach.json carcass key. LIVE_LEDGER row dated 2026-08-12: found by a fingerprint scan of
    // the Den's inventory-style array, owner-eyewitnessed both a read (the Den UI reflects the
    // byte) and a write (a manual poke landed a carcass the player then saw in the Den). A
    // mid-battle poach does NOT write this array directly -- the engine's own commit lands at
    // battle END as an increment. What was specifically owner-observed (2026-08-12) is that a mod
    // write made MID-battle survives that end-of-battle commit (the engine increments rather
    // than rebuilds, per the ledger row). Same class of access as the
    // adjacent InventoryCountBase array: guarded W8, one byte per key, addr =
    // PoachStoreBase + (key - 1).
    public const long PoachStoreBase = 0x1411A7A1B;

    // --- Corpse despawn (LW-167 stage 3): the engine's own declarative render-node removal,
    // promoted from BodyDoubleSpike.cs's Ctrl+F5 dev instrument (LivingWeapon/Dev/BodyDoubleSpike.cs,
    // #if LWDEV) into CorpseDespawn.cs's guarded production helper. docs/LIVE_LEDGER.md's
    // "DESPAWN any unit mid-battle, sprite and all" Proven row (owner live 2026-07-10, flip
    // 2026-07-21): write mode 2 into the render node's flag word and the engine's own per-frame
    // node sweeper (0x14026E20C) completes the whole removal -- unit AND sprite -- on its next
    // UNPAUSED frame; the same primitive vanilla crystallization uses. ONE-WAY. BodyDoubleSpike
    // keeps its own private copy of these constants (dev-only, unchanged by this promotion) --
    // see CorpseDespawn.cs's class doc for why duplicating the (much smaller) despawn-only logic
    // was the smaller, safer diff than refactoring the 1576-line working dev spike.
    public const long DespawnNodeListHead = 0x140D3A410;   // render-node singly-linked list head
    public const int DespawnNodeIdOff = 0x08;              // node id byte (matched vs the current-actor dword)
    public const int DespawnNodeCombatOff = 0x148;         // node -> combat back-pointer (builder-written)
    public const int DespawnNodeModeOff = 0x12C;           // mode flags; all proven access is to the LOW BYTE (bits 0x30 = removal mode/done)
    public const uint DespawnNodeModeClearMask = 0x30;     // bits cleared before the mode-2 stamp
    public const uint DespawnNodeModeRemoveValue = 0x20;   // mode 2 = "remove me" (the sweeper consumes it)
    public const long DespawnCurrentActorNodeId = 0x140CF873C;   // dword: the CURRENT ACTOR's node id; never remove it
    public const int DespawnNodeWalkMax = 64;               // bounded list walk (BodyDoubleSpike/spawn_probe precedent)

    /// <summary>The composed-status byte carrying the chest/crystal conversion bit: status byte 1
    /// (Offsets.ADeadStatus + 1 = 0x46; == StatusApply.Composed + StatusApply.StatusByte(id)),
    /// which holds composed status ids 8-15, MSB-first -- Treasure (id 15, LW-58's pop-signature
    /// evidence, docs/LIVE_LEDGER.md) is only ONE of them; Blind/Darkness (id 13) shares this same
    /// byte. Only <see cref="ACorpseChestBitMask"/> marks the engine's own crystal/chest
    /// conversion -- check the bit, never the whole byte (a Blinded, unconverted corpse reads this
    /// byte nonzero too; owner-caught live 2026-08-12, CorpseDespawn.cs's staleness check false-
    /// positived on it).</summary>
    public const int ACorpseConvertMarker = 0x46;
    public const byte ACorpseChestBitMask = 0x01;  // mask: bit 0 of ACorpseConvertMarker (id 15, the byte's last slot MSB-first) is Treasure/chest

    // --- Gun Slinger / Crossfire twin-grant consent (LW-193/LW-194): a world-map "a menu
    // panel is open" flag. Reads 1 exactly while a party/equip/shop/save menu is open, 0 on the
    // free map, during travel, and through the WHOLE formation and battle-load flow --
    // DELIBERATELY, so a gate built on this byte never suppresses the pre-battle window (the
    // twin must already be present in the roster when a battle constructs, or the wielder fires
    // only once for that whole battle; see [twin-dualfire-construction-bound]).
    // Found by menu_signal_probe.py's wide constant-intersection solve over five UI states, then
    // a torture tour (tape lw193_menusig_20260817_063908.log): flipped cleanly on every party/
    // equip/shop open-close, one 500ms double-pulse on a town-exit fade (hence the caller's
    // 3-pass debounce), silent across four world-map travel hops. Disqualified the early
    // favorite 0x140D506B4, which pulsed 50ms on every travel hop instead. Sibling
    // 0x140D508EC tracks it; 0x140D50916 is its clean inverse (neither promoted here -- nothing
    // reads them). See [worldmap-menu-open-byte] (LIVE_LEDGER.md, Uncertain as of 2026-08-17,
    // owner flip pending). UNREADABLE MEANS SUPPRESS: the caller (GunSlinger.cs) treats a failed
    // read as menuOpen == true -- fail toward not writing, never toward writing over a player's
    // own gear.
    public const long MenuOpenFlag = 0x140D508D0;   // u8

    // --- Gun Slinger / Crossfire twin-grant consent, owner-AC round (LW-193): the party-BROWSE
    // screen stamp. Reads 1 exactly on the party overview root AND the Character Status page; 0
    // in E&A (all three tabs: Inventory/Chronicle/Options), the save menu, the shop, world-map
    // travel, formation, and battle. Torture-proven 2026-08-17 afternoon (tape
    // lw193_menusig_20260817_110948.log, verify-watch method): clean edges on every root/Status
    // open-close. NOT yet probed on E&A's own sub-pickers (item/ability lists opened FROM E&A);
    // the gate's own deny-default (unreadable/0 -> not-browse) covers that gap safely either way.
    // UNREADABLE MEANS NOT-BROWSE (deny), matching [worldmap-menu-open-byte]'s own fail-toward-
    // suppression convention: a failed read must never be mistaken for "safe to write". See
    // [party-browse-screen-byte] (LIVE_LEDGER.md) for the full evidence; [worldmap-menu-open-byte]
    // narrows to the DENY-side half of the gate as of this round (see its row for the note).
    public const long PartyBrowseFlag = 0x140D408E2;   // u8

    // --- LW-251: WeaponPalette runtime -- the two resident weapon-palette banks a swing's
    // sprite draws its colours from. 16 palettes per bank, 32 bytes (16 u16 BGR555 entries) each;
    // entry 0 of every palette is the transparency slot and is structurally never written (the
    // runtime's write window starts at WeaponPaletteStride's own +2 offset, 30 bytes covering
    // entries 1..15). BOTH banks must be written together or the change flickers between draws.
    // A battle LOAD refreshes both banks from the loaded file, reverting every write -- the
    // runtime's own snapshot/restore + re-assert design exists because of that, not despite it.
    // Sources: observed live 2026-08-21/22 (ledger row [resident-weapon-palette-buffer],
    // awaiting owner flip) for the two bank addresses and the per-draw read; ledger row
    // [per-weapon-colour-by-turn-repaint] (also awaiting owner flip) for the per-draw refresh +
    // battle-load revert. tools/probes/lw305_bench_paint.py's own BANKS/PAL_STRIDE constants are
    // the same two numbers, independently re-derived here rather than shared (the probe is a
    // throwaway script; Offsets.cs is the pinned production source every other constant here
    // lives in).
    public const long WeaponPaletteBankA = 0x140D35750;
    public const long WeaponPaletteBankB = 0x140D35950;
    public const int WeaponPaletteStride = 32;   // bytes per palette (16 u16 entries)

    // --- LW-317 multi-lane growth: the CURRENT-brave/CURRENT-faith flat holds, the u16 MaxHp
    // hold, and the turn-scoped resident item-stats WP write. CMaxHp/CBraveCurrent/CFaithCurrent
    // are additive combat-struct offsets (independently re-derived from this file's own
    // algebra: CMaxHp = AMaxHp 0x16 + BandEntry 0x1C); ItemStatsBase/Stride/WpOff anchor a
    // SEPARATE resident table, not the combat struct. sid == id (the row-index identity
    // WpTableHold's write address assumes) is additionally pinned at BAKE TIME by
    // tools/gen_living_weapon_meta.py's check_wp_sid_identity for every "wp"/"wp+faith" weapon,
    // not just asserted here.
    /// <summary>u16 combat-struct MaxHp (== AMaxHp 0x16 + BandEntry 0x1C; distinct from CHp
    /// 0x30, current HP -- HoldU16 itself never touches current HP, only this MAX field, with one
    /// sanctioned exception: the LW-327 top-up (GrowthEngine.Lanes.cs) raises CHp by the delta
    /// the max rose, once per real battle -- a GENUINE capture arms a pending intent that
    /// delivers on the first tick current HP reads sane, so a grown weapon opens the battle full
    /// instead of reading hurt. PROVEN 2026-08-25, ledger
    /// [maxhp-hold-attribution-safe]: a raised MaxHp hold does not break kill attribution and
    /// current HP stays put outside that per-battle top-up.</summary>
    public const int CMaxHp = 0x32;   // u16
    /// <summary>u8 combat-struct CURRENT brave (orig is <see cref="CBrave"/>, 0x2A -- never
    /// written by this lane). PROVEN 2026-07-02 (Kobu ships on it), ledger
    /// [current-brave-write-sticks]: a one-shot write at this offset sticks and the orig byte
    /// stays untouched.</summary>
    public const int CBraveCurrent = 0x2B;   // u8
    /// <summary>u8 combat-struct CURRENT faith (orig is <see cref="CFaith"/>, 0x2C -- never
    /// written by this lane). PROVEN 2026-08-25, ledger
    /// [current-faith-write-scales-magic-gun]: a held write here scales magic-gun damage
    /// linearly both directions and the forecast panel tracks it.</summary>
    public const int CFaithCurrent = 0x2D;   // u8
    /// <summary>Base of the resident item-stats table WpTableHold's turn-scoped WP write
    /// targets: row stride <see cref="ItemStatsStride"/> bytes, WP at row +<see
    /// cref="ItemStatsWpOff"/>. Row index is the item's own sid (== items.json id, corroborated
    /// by the live probe against id 73 AND additional_data_ids.json identity for every id
    /// &lt;= 127). PROVEN 2026-08-25, ledger [wp-table-write-live-damage]: this byte is re-read
    /// PER SHOT, so writing it moves live damage and reverts clean.</summary>
    public const long ItemStatsBase = 0x14080F690L;
    public const int ItemStatsStride = 8;
    public const int ItemStatsWpOff = 4;
    /// <summary>Row count of the resident item-stats table (ItemWeaponData has 128 rows; the
    /// EquipBonus table starts right after it at 0x14080FEA0). LW-346: an extended-inventory id
    /// (261+) has NO row here (its stats live in the mod's own stub page), so any writer indexing
    /// this table by item id must stop at this bound or it lands in the EquipBonus rows.</summary>
    public const int ItemStatsRows = 128;

    // --- LW-346 extended inventory (the 261 item-cap break ported from the FFTHandsFree rig) ---
    // Every address below was re-anchored for 1.5.2 on 2026-08-26 (the equip/display cluster
    // slid -0x78 and the accessor cluster -0x74 from 1.5.0; data bases unchanged) and confirmed
    // against the 1.5.2 exe on disk on 2026-08-27 (every thunk reads E9, every function entry
    // carries its landmark prologue behind ret/CC padding). Provenance per site:
    // docs/research/ITEM_CAP_261_BREAK_JOURNEY.md, sections 2026-08-26 "the 1.5.2 re-anchor"
    // through 2026-08-27 03:20 "the port blueprint"; the single-byte patch sites live in
    // ExtendedSites.cs as a table. Live evidence is owner-observed (Uncertain rows in
    // docs/LIVE_LEDGER.md: [inventory-default-order-drops-unknown-ids],
    // [capbreak-swing-art-via-accessor-clones], [weapon-sprite-pair-drives-swing-art]).
    /// <summary>Image base; fixed (no ASLR), the origin every disp32 below is measured from.</summary>
    public const long ModuleBase = 0x140000000L;
    /// <summary>The four disp32 bytes inside the catalog accessor's extended branch
    /// (<c>lea rax,[rax*4 + disp32]</c> at 0x1402B8C66) that select the ids-256+ catalog block;
    /// vanilla value 0x0067F910 = <see cref="ExtCatalogBase"/> minus the image base.</summary>
    public const long ExtCatalogDisp32 = 0x1402B8C6AL;
    /// <summary>Vanilla extended catalog block (ids 256-260, 12-byte ITEM_COMMON_DATA records,
    /// indexed by the full id, so record 256 sits at base + 256*12).</summary>
    public const long ExtCatalogBase = 0x14067F910L;
    /// <summary>Main catalog block (ids 0-255, 12-byte records).</summary>
    public const long MainCatalogBase = 0x14080EA90L;
    /// <summary>Two-byte sprite/palette pair per item id (byte 0 palette nibbles, byte 1 the
    /// drawing); read on every swing. June's probes used base +2, one item off.</summary>
    public const long WeaponSpritePairTable = 0x140785CF0L;
    /// <summary>Bag count per item id (one byte each); the save stores exactly 261 of them.
    /// (the VANILLA block; the armed extended inventory relocates it, ExtendedInventory.BagCountBase)</summary>
    public const long BagCountArray = 0x1411A7C00L;
    /// <summary>LW-368: the second per-item byte list the game keeps beside the bag counts (the
    /// per-item flag list the reset routine zeroes alongside <see cref="BagCountArray"/>, byte
    /// for byte, at the same 0x105-entry/0x110-slot shape). Relocated together with the bag
    /// counts when the list relocation arms (ListRelocation.cs); vanilla otherwise.</summary>
    public const long SiblingListArray = 0x1411A7700L;
    /// <summary>The E9 accessor thunks (five bytes each) the extended ids are redirected through.
    /// Weapon stats returns a pointer to an 8-byte ITEM_WEAPON_DATA row; the rest take an item
    /// id in rcx and index per-category tables that have no row for a new id.</summary>
    public const long ThunkWeaponStat = 0x1402B8C74L;
    public const long ThunkValidity = 0x1402B8EBCL;
    public const long ThunkTypeProbe = 0x1402B8EE8L;   // (id, dataType): LW-347's shield eviction
    public const long ThunkRangeIndex = 0x1402B8BCCL;  // -1 for a new id = "not a weapon" = the punch
    public const long ThunkSpritePair = 0x1402B8E60L;  // the swing art record
    public const long ThunkRangeBase = 0x1402B8C0CL;
    public const long ThunkSibling1 = 0x1402B8CD4L;
    public const long ThunkSibling2 = 0x1402B8D3CL;
    public const long ThunkSibling3 = 0x1402B8DA0L;
    public const long ThunkSibling4 = 0x1402B8E04L;
    /// <summary>Plain function entries hooked with Reloaded.Hooks behind a prologue landmark.</summary>
    public const long FnCategoryGetter = 0x1402890C0L;   // inventory list-build filter (called at 0x140288BF5)
    public const long FnOrderRebuild = 0x140285DF0L;     // display-order rebuild (drops ids its table lacks)
    public const long FnInventoryReset = 0x140284500L;   // per-item state reset: zeroes the bag array below its widened bound (LW-351 R7)

    // --- LW-354: shop stock for extended ids (found 2026-08-27 evening, static read of the live
    // 1.5.2 process, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md section "shops"). The per-item
    // town-flags table (the modloader's ItemShopsData, 256 u16 rows, loader signature
    // "00 00 FE 01 FE 01 ...") and the shop BUY-list builder's two references to it. The builder
    // loops ids 0..0xFF (cmp ebx,0x100 at 0x140288FD9, imm32 at +2), reads the flags word as
    // (low byte << 8 | high byte) and tests 0x8000 >> townIndex (town 0 = Lesalia ... 9 = Dorter,
    // the loader's ShopFlags bit order), then the catalog record's +0x0A chapter byte. No other
    // plain-code reader reaches ids past 255 (the "new stock" badge scan at 0x1403453C7 bounds
    // itself at 0x200 and is left alone). ---
    /// <summary>ITEM_SHOPS_DATA: u16 ShopFlags per item id, 256 rows, .data.</summary>
    public const long ShopFlagsTable = 0x14067F890L;
    /// <summary>rel32 field of <c>lea r12,[rip+0x3F6989]</c> at 0x140288F01 (next ip 0x140288F08),
    /// which starts the builder's HIGH-byte walker at ShopFlagsTable + 1.</summary>
    public const long ShopBuilderHighByteLeaRel32 = 0x140288F04L;
    public const long ShopBuilderHighByteLeaNextIp = 0x140288F08L;
    /// <summary>disp32 field of <c>movzx edx, byte ptr [rcx + rbp + 0x67F890]</c> at 0x140288F3B
    /// (rbp = image base, rcx = id*2): the LOW byte read.</summary>
    public const long ShopBuilderLowByteDisp32 = 0x140288F3FL;

    // --- LW-353: the save edges (corrected 2026-08-27 late night after the owner's live test 2
    // found the hooks silent; the first build read the pointer from 0x141D407A0, one digit off,
    // and hooked two routines that were not the ones it named. Re-read from the running 1.5.2
    // process and the exe on disk, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md "save edges,
    // corrected"). ONE global holds the pointer to the save STRUCT the game is serializing or
    // applying: the load-apply reads it as `4C 8B 05 98 56 B2 00` at 0x14021B101 (next ip
    // 0x14021B108 + 0xB25698) and the serializer at 0x140218F8E; an image xref sweep
    // (tools/probes/lw346_xref_scan.py) counts 46 references to it and none to the old address.
    // The struct is NOT transient: the pointer normally holds the static image buffer
    // 0x142C81C80 (ten routines store an address into the global, so the earlier "null between
    // saves and loads" reading was a misread), and the hooks therefore read whatever it points at
    // AFTER the original returns, which is the struct that routine just filled or applied.
    // Saving runs through the wrapper 0x14021B070 (clears the +0x101 bytes, stamps +0x111, then
    // jumps to the serializer at 0x14021B0E3), so hooking the serializer catches every save, the
    // autosave included. The real load-apply is 0x14021B0E8, the byte right after that jump, and
    // a second restore routine at 0x14021DE98 owns the other bag copy. 0x14021DDF0 is the async
    // file-op stepper (state dword 0x142E7FADC, called twice a frame while a save OR a load is in
    // flight) and is deliberately NOT hooked: it fires on saves as well as loads. The struct's
    // +0x100..+0x1B8 header is the slot-list metadata (play time in seconds at +0x1B4,
    // chapter/location bytes, the four party names at +0x124), which the file round-trips
    // verbatim, so a hash of that header is a per-save key that reads the same at save time and
    // at load time. ---
    /// <summary>u64: pointer to the save struct being serialized or applied.</summary>
    public const long SaveStructPtr = 0x140D407A0L;
    public const int SaveHeaderKeyOff = 0x100;
    public const int SaveHeaderKeyLen = 0xB8;
    public const int SaveHeaderPlayTimeOff = 0x1B4;   // u32 seconds = h*3600 + m*60 + s from 0x141856704/708/700
    /// <summary>Offsets INTO the 0xB8 key window above (so header +0x11A, +0x11C and +0x11D):
    /// the three bytes the game holds at 0xFF while a save is in flight and leaves at 0x00 at
    /// rest. <see cref="SaveEdgeTracker.KeyFromHeader"/> zeroes them before hashing, so a save
    /// and its later load key identically. The save edge samples the in-flight state (it runs
    /// right after the serializer returns); every load observed so far sampled at rest, where the
    /// mask is a free no-op, and it is applied at both edges anyway so a load that ever does
    /// sample mid-flight still keys the same. Read from the owner's 2026-08-28 session: setting
    /// exactly these three bytes to 0xFF in the resting headers the round-trip probe captured
    /// reproduces all five save keys the mod logged that night, and zeroing them makes the save
    /// key equal the load key for every observed pair.</summary>
    public static readonly int[] SaveHeaderVolatileOffs = { 0x1A, 0x1C, 0x1D };
    /// <summary>The save serializer (fills the struct; ecx = save kind/slot, dl/r8b = header
    /// bytes +0x112/+0x113). Entry preceded by <c>ret; CC CC</c>; every save caller reaches it
    /// through the wrapper 0x14021B070's tail jump, so this one entry covers them all.</summary>
    public const long FnSaveSerialize = 0x140218F78L;
    /// <summary>The real load-apply routine, entered right after the save wrapper 0x14021B070's
    /// jump to the serializer (no padding between them): it reads the struct pointer at
    /// 0x14021B101 and copies the struct's bag into 0x1411A7C00 and its roster block into
    /// 0x1411A7D10 at 0x14021B1D5.</summary>
    public const long FnSaveApply = 0x14021B0E8L;
    /// <summary>The second restore routine (its bag copy at 0x14021E1D1 zeroes count[0] first,
    /// as the load-apply's does at 0x14021B1C8; it reads the struct pointer at 0x14021DEC9 and
    /// 0x14021E1B6). One plain-code caller, 0x1402FB9EE; which game action drives it is unread,
    /// so it is hooked with the same handler and carries its own canary.</summary>
    public const long FnSaveApplyB = 0x14021DE98L;
    /// <summary>The static image buffer the pointer above normally holds; diagnostic only, the
    /// hooks always follow the pointer rather than reading this.</summary>
    public const long SaveStructStatic = 0x142C81C80L;

    // --- LW-351 fix round 5: the two weapon menu ORDER TEMPLATES (2026-08-30, disassembled from
    // the 1.5.2 exe on disk; re-derive with tools/probes/lw351_order_template_probe.py --disk).
    // These tables decide which item ids the party inventory and the unit equip picker are willing
    // to list, and they are NOT rebuilt from the item data during a load: the load-apply routine
    // (FnSaveApply) RESTORES both out of the save struct it was handed and the serializer
    // (FnSaveSerialize) writes them back, so they are SAVE STATE. Copy direction, read off the
    // disassembly both ways:
    //   load    0x14021B4BD  lea rdx,[rip+0x59708C] -> 0x1407B2550 (dest), rcx = [r8+0x8A6C] (src)
    //   save    0x1402194BC  lea rdx,[rip+0x59908D] -> 0x1407B2550 (src),  rcx = [r8+0x8A6C] (dest)
    // A save written before an extended id ever seated therefore restores a template without it,
    // which is why the mod seats owned extended ids itself right after the load-apply returns
    // (Persistence/TemplateSeat.cs). Both tables are u16 item ids ending in a 0x00FF marker; the
    // menu LISTS the order-rebuild hook works on end in 0xFFFF instead.
    /// <summary>The party inventory's weapon display-order template: u16 item ids, 0x00FF end
    /// marker, named by the pointer table 0x14067F498 slot 0.</summary>
    public const long InventoryOrderTemplate = 0x1407B2550L;
    /// <summary>Capacity of the table above, in u16 words. Derivation: the load-apply's
    /// fixed-size copy into it moves exactly 0x11A = 282 bytes = 141 words (the loop at
    /// 0x14021B560 runs r10 = 2 iterations of rsi = 0x80 bytes, r10 from <c>lea r10d,[r11+2]</c>
    /// at 0x14021B1C4 with r11 already counted down to 0, rsi from <c>lea esi,[rbx+0x7c]</c> at
    /// 0x14021B11C with ebx = 4; the tail at 0x14021B5A9 adds 16+8+2 = 0x1A bytes). The
    /// bound agrees with the neighbors: the next object any pointer names above this base is
    /// 0x1407B266C (picker pointer table 0x14067FAD0), 0x11C above it, i.e. these 141 words plus
    /// two bytes of alignment padding.</summary>
    public const int InventoryOrderTemplateWords = 141;
    /// <summary>The unit equip picker's weapon display-order template (same word format), named
    /// by the pointer table 0x14067FA90 slot 0. It sits inside one 0x3F2-byte block the load
    /// restores from [r8+0x8C7C] (the copy at 0x14021B6F9: r9 = 7 iterations of 0x80 plus a
    /// 0x72-byte tail), which also carries the picker's three other sub-tables.</summary>
    public const long PickerOrderTemplate = 0x141874540L;
    /// <summary>Capacity of the table above, in u16 words: the next sub-table its own pointer
    /// table names (slot 1, 0x14067FA98) is 0x14187465A, exactly 0x11A = 282 bytes = 141 words
    /// above the base, the same size the inventory template's copy proves for its twin.</summary>
    public const int PickerOrderTemplateWords = 141;

    // --- LW-371 (plan v1.1, 2026-08-31): the order-template RELOCATION knowledge. Both templates
    // above are read only through a pointer-table SLOT (finding 1: 11 plain-code sites read the
    // three slots below, never the template addresses directly), and the picker block also hides a
    // FIFTH sub-table -- "every owned item, any category" -- reached at the shared picker base
    // plus 0x1E6 (finding 4, the reviewer's RE re-check (a), reproduced in LW371_plan.md). Moving
    // all three charts means re-pointing these ten fields (three slots, five rip-relative int32s,
    // two menu-list-cap bytes); tools/probes/lw371_order_template_relocate.py --scan re-derives
    // every one of them against the live process.
    /// <summary>Slot A naming <see cref="InventoryOrderTemplate"/> (reviewer (b): reads
    /// 0x1407B2550 today; housekeeper 0x140285F80, list build 0x140288D55, rebuild callers
    /// 0x1403382B3 / 0x14036B1D9).</summary>
    public const long InventoryOrderTableSlot = 0x14067F498L;
    /// <summary>Slot B naming <see cref="InventoryOrderTemplate"/> (reviewer (b): also reads
    /// 0x1407B2550; the third housekeeper copy, 0x14039684C).</summary>
    public const long InventoryOrderTableSlotB = 0x140689C38L;
    /// <summary>Slot naming <see cref="PickerOrderTemplate"/> (reviewer (b): reads 0x141874540;
    /// picker housekeeper 0x14028609F, callers 0x14033694F / 0x140336CEF).</summary>
    public const long PickerOrderTableSlot = 0x14067FA90L;
    /// <summary>The picker block's fifth sub-table (finding 4): "every owned item, any category".
    /// Not named by any pointer slot -- reached only by the rip-relative fields the reviewer's
    /// table (a) lists (a shared rsi base at [rsi+rcx*2+0x1E6]/[...+0x1E8], plus four direct
    /// disp32 fields). Its own ceiling today is 261 owned kinds of ANY category; past that it
    /// overwrites 0x141874932.</summary>
    public const long PickerAllItemsTemplate = 0x141874726L;
    /// <summary>Capacity of the table above, in u16 words: 0x20C bytes (finding 4).</summary>
    public const int PickerAllItemsTemplateWords = 262;
    /// <summary>The shared list builder's entry-cap byte: the imm32 low byte of <c>cmp esi,0x91</c>
    /// (six bytes, 81 FE 91 00 00 00) at 0x140288CC1, widened 0x91 (145 entries) -&gt; 0x95 (149,
    /// D5, v1.3). PROOF OF ROOM (v1.3 correction): both stack list buffers hold exactly
    /// <see cref="ListBuilderStackRoomBytes"/> bytes between the buffer and the GS security
    /// cookie (reviewer (d): fnA rsp+0x70..rsp+0x1A0 via <c>lea rbp,[rsp-0xb0]</c> then
    /// <c>mov [rbp+0xa0],rax</c>; fnB true entry 0x140336B88, rsp+0x50..rsp+0x180 via
    /// <c>lea rbp,[rax-0xa8]</c> then <c>[rbp+0x80]</c>), and the builder itself writes cap+1
    /// words (the entries plus a terminator) -- BUT fnA (entry 0x1402875A4, the picker's
    /// weapons-only per-slot list) does not stop there: after the capped builder list it appends
    /// the unit's two hand items (stores at 0x14028778C and 0x1402877A9, each only if the
    /// validity thunk 0x1402B8EE8 accepts the id) and THEN writes its own 0xFFFF terminator
    /// (0x1402877C0), so fnA's buffer must hold cap + 3 words, not cap + 1: (0x95+3)*2 = 0x130
    /// exactly. 0x97 (151) would have put the terminator on the cookie at (151+3)*2 = 0x134 and
    /// fast-failed the process (STATUS_STACK_BUFFER_OVERRUN) the first time 150+ weapon-class
    /// kinds sat in the bag with both hands full.</summary>
    public const long ListBuilderCapByte = 0x140288CC3L;
    /// <summary>The list-insert bound byte: the imm32 low byte of <c>cmp edx,0x92</c> (six bytes,
    /// 81 FA 92 00 00 00) at 0x140286318, widened 0x92 (146) -&gt; 0x96 (150 = builder cap + 1, so
    /// the loop still reaches the terminator at index 149).</summary>
    public const long ListInsertBoundByte = 0x14028631AL;
    /// <summary>Bytes between the smaller of the two stack list buffers and its GS cookie (reviewer
    /// (d), both callers re-read 09:00: fnA frame 0x1B0, buffer rsp+0x70, cookie rsp+0x1A0; fnB
    /// entry 0x140336B88, frame 0x190, buffer rsp+0x50, cookie rsp+0x180). The builder alone would
    /// only need cap+1 words, but fnA's weapons-only path appends two hand items and its own
    /// terminator after the builder returns (P14), so the binding constraint is cap+3 words: a
    /// cap fits only when (cap+3)*2 &lt;= this.</summary>
    public const int ListBuilderStackRoomBytes = 0x130;
}
