namespace LivingWeapon;

/// <summary>
/// Game addresses. Each pin is its preferred-base absolute (image base 0x140000000), resolved through
/// <see cref="ImageBase.At"/> because the Denuvo-free build (2026-10-15) turned ASLR on (the 2.3.4
/// hotfix, then main; docs/research/PORT_1.5.2_NODRM_OFFSETS.md). Pins are static properties, never
/// const or static readonly (ImageLiteralContractTests checks both). Sources are
/// FFTHandsFree/docs/BATTLE_MEMORY_MAP.md and the BattleTracker the detection logic is ported from.
///
/// 1.5.1 (Steam buildid 23901820) audit 2026-07-13 (docs/research/PORT_1.5.1_OFFSETS.md): every absolute was
/// re-read at its 1.5 address except <see cref="SubmenuFlag"/>, the one mover. ArrayBase and EventId are
/// provisional until a post-deploy battle. Dev-spike constants (BodyDoubleSpike/StatusSpike, #if LWDEV) were
/// not re-checked and stay stale-flagged.
///
/// 1.5.2 (Steam buildid 24304240) audit 2026-07-24 (docs/research/PORT_1.5.2_OFFSETS.md): no address here
/// changed. Found offline by diffing the 1.5.1 and 1.5.2 exes (tools/probes/exe_reanchor_scan.py,
/// tools/probes/rip_xref_reanchor.py). Every runtime global was re-read out of the game's own RIP-relative
/// instructions in both exes and they agree, up to 33/33 referencing sites for <see cref="RosterBase"/>. That
/// settles addresses, not meaning (the 1.5.1 pause-flag change kept its address), so the post-deploy pass still
/// owns behavior. The one address moved for 1.5.2 is outside this file (PromptSwapHook.FnSetTextString, +4).
/// </summary>
internal static class Offsets
{
    // --- in-battle flags ---
    public static long Slot0 => ImageBase.At(0x140782A30);   // 1.5 confirmed +0x6000 (was 0x14077CA30): u32 battle-phase word; four edge samples 2026-07-21 + the re-entry probe (LIVE_LEDGER 1.5 slot0 battle-phases row)
    public static long Slot9 => ImageBase.At(0x140782A54);   // 1.5 confirmed +0x6000 (was 0x14077CA54): 0xFFFFFFFF at all four battle edges on the same log Slot0 and Slot9 both stay 0xFFFFFFFF from the title screen until the first battle (LIVE_LEDGER [sentinel-out-of-battle-tuple]).

    /// <summary>The slot0 in-battle marker on 1.5 (pre-1.5 it was 0xFF, sentinel_probe.py 2026-06-10). Samples
    /// on 1.5.1, 2026-07-21 (LIVE_LEDGER "1.5 slot0 battle phases" Uncertain row): 0xFFFFFFFF at both
    /// load-churn edges, 0x10 at the real enter (mode 3), 0x11 at the victory exit. That 0x10 holds through
    /// mode-1/5 mid-battle is inherited from pre-1.5 and awaiting-live.</summary>
    public const uint Slot0InBattleMarker = 0x10;
    public static long Acted => ImageBase.At(0x140782A8C);   // 1.5 confirmed (was 0x14077CA8C): rising edge = an action completed; TurnTracker/KillTracker ship on it (log 2026-07-01)
    // u16 event file number in cutscenes/dialogue; aliases the active unit's nameId during combat
    // animations, so only read it out of live battle. 0xFFFF at the menu, then 2 -> 4 -> 5 through the
    // Orbonne prologue; PlaythroughReset.Policy.OpeningEventId (2) anchors the new-game detector.
    public static long EventId => ImageBase.At(0x140782A94); // 1.5 confirmed live 2026-07-08 (was 0x14077CA94)

    /// <summary>Pointer to the acting unit's combat frame base (see <see cref="FrameReadBase"/>; frame + <see
    /// cref="BandEntry"/> = band entry). Found via FFTMultiplayer's action_record_probe; confirmed 2026-07-01
    /// by tools/probes/unitid_probe.py "watch", which named each acting wielder's seat when the <see
    /// cref="TurnQueue"/> fingerprint was ambiguous. Reads 0x0 at battle-open idle. FFTMultiplayer's doaction
    /// formula divides from 0x141853CE0, a different slot-origin convention (their 16-slot base is our seat 8);
    /// the seat math here uses <see cref="FrameReadBase"/> 0x141852CE0.</summary>
    public static long ActorPtr => ImageBase.At(0x14186AF68);

    // --- condensed active-unit struct = the unit whose turn it is (FFTHandsFree NavigationActions.Scan
    //     "AddrCondensedBase"). The acting player is found by HP+MaxHP+level against the battle array,
    //     then resolved to the roster by a level/brave/faith fingerprint, not by +0x04. TqNameId is a
    //     sequential battle index, and a Time Mage's index 1 collided with Ramza's roster nameId 1,
    //     which mis-credited everyone's kills to Ramza. ---
    public static long TurnQueue => ImageBase.At(0x1407832A0);   // 1.5 confirmed +0x6000 (was 0x14077D2A0): fingerprint team=0/nameId=1/hp=486/486
    public const int TqLevel = 0x00;   // u16
    public const int TqTeam = 0x02;   // u16  0 = player, 1 = enemy, 2 = ally/guest (KillTracker.Corpses.cs:233 + docs/research/PORT_1.5.md)
    public const int TqNameId = 0x04;  // u16  sequential battle index, not the roster nameId
    public const int TqHp = 0x0C;   // u16  active unit's current HP (fingerprint key)
    public const int TqMaxHp = 0x10;   // u16  active unit's MaxHP (fingerprint key)

    // --- static unit array ---
    public static long ArrayBase => ImageBase.At(0x140899F50);   // 1.5 confirmed +0x6350 (was 0x140893C00): captures 11 enemies (slots 4-14), excludes Ramza (slot 20)
    public const int ArrayStride = 0x200;
    public const int SlotsBack = 20;   // enemy slots, at array offsets <= 0
    public const int SlotsFwd = 10;   // player slots, at array offsets >= 1
    public const int NSlots = SlotsBack + SlotsFwd;
    // slot s sits at ArrayBase + (s, (SlotsBack, 1)) * stride; enemy slots are s <= SlotsBack-1.
    public static long ArrayReadBase => ArrayBase - (SlotsBack - 1) * ArrayStride;
    public const int EnemySlotMax = SlotsBack - 1;   // slots 0..19 are enemy-side
    public const int ALevel = 0x0D; // u8   (roster-match fingerprint)
    public const int ABrave = 0x0E; // u8   origBrave (roster-match fingerprint; re-normalizes, never displays; Wielder's locate fingerprint, never write it)
    /// <summary>u8 band-relative current brave (CBraveCurrent 0x2B, BandEntry 0x1C = 0x0F), the
    /// effective and displayed value; Kobu reads it (enemy) and holds it (wielder). Band +0x2B is
    /// the Reraise/Invisible/Float bitfield (AReraise), not brave. Layout:
    /// brave-faith-current-vs-orig-offsets (FFTMultiplayer 2026-06-20).</summary>
    public const int ABraveCurrent = 0x0F;
    public const int AFaith = 0x10; // u8   origFaith (roster-match fingerprint)
    public const int AInBattle = 0x12; // u16
    public const int AHp = 0x14; // u16  (0 == KO'd)
    public const int AMaxHp = 0x16; // u16
    public const int AMp = 0x18; // u16  MP visibly restored on screen 2026-06-10. Every MP write is gated on SpiritualFont's per-battle layout validation plus a read-back (SET/miss log).
    public const int AMaxMp = 0x1A; // u16  (same per-battle guard as AMp)
    public const int AGx = 0x33; // u8
    public const int AGy = 0x34; // u8
    /// <summary>u8 band-relative byte whose bit 7 is the terrain layer (bridge decks share (gx,gy) on different
    /// layers): combat +0x51, BandEntry (0x1C) = 0x35. Bulwark (docs/BULWARK_AC.md criterion 8) blocks only
    /// the wielder's own layer. Low 2 bits are facing (0=South, 1=West, 2=North, 3=East), tracking the Wait
    /// facing wheel 2026-07-28 (LIVE_LEDGER row "Unit FACING is band +0x35 low 2 bits"); the Y direction was
    /// corrected 2026-07-28 06:15 so North (2) looks toward +y (see BulwarkPolicy.BehindTile).
    /// Bulwark.Terrain.cs's Plant reads both in one read.</summary>
    public const int ALayerBit = 0x35;   // u8, bit 0x80 = layer index, low 2 bits = facing
    /// <summary>u8 band-relative write target (combat base+0x41): slam to 100 to inject a scheduler turn
    /// (ExtraTurn.CtOff). Reads do not tick reliably. ct_watch / finish_watch 2026-06-14. It reads cleanly for
    /// enemy turns but stale ~85 on the player's own unit during its input menu, so FeignDeath counts the
    /// wielder's turns with a wall clock. Iai no longer reads it (rebuilt on ActorPtr 2026-07-01);
    /// Maim/FeignDeath/SpiritualFont/Plague/Rapture still do.</summary>
    public const int ACtSlam = 0x25;
    /// <summary>u8 band-relative read byte (combat base+0x25) for counting completed turns. CT seen
    /// at/above 90, then below 70 = one turn (CtTurns, used by Maim for victims). On a player unit
    /// it stays flat 0 (the Rapture wall, ct_watch / finish_watch 2026-06-14), so victims only.</summary>
    public const int ACtTurn = 0x09;
    /// <summary>u8 band-relative job id (PSX "Current Job", combat base +0x03). Negative, so it reaches below
    /// BandEntry (0x1C); ACrystalHearts and AGateByte are negative too. Promoted from Puppeteer.Policy's JobOff
    /// and now an alias. Confirmed 2026-06-18 (Ramza 83 Thief, party Dark Knight 94, Goblin 99,
    /// Bonesnatch 110), but bosses read story ids like 37, so Puppeteer.IsDominatable does not filter on
    /// it.</summary>
    public const int AJob = -0x19;

    // --- roster (nameId -> equipped right hand) ---
    public static long RosterBase => ImageBase.At(0x1411A7D10);   // 1.5 confirmed +0x6440 (was 0x1411A18D0): slot0=Ramza (lvl99/rhand80/nameId1), slots +1..+7 = real party
    public const int RosterStride = 0x258;
    /// <summary>Ceiling, not a floor. tools/probes/roster_span_probe.py on a 46/50 save (2026-07-21) found rows
    /// 0..45 occupied and 50 contiguous rows at the 0x258 stride. Slots 50+ are a stale guest bank with
    /// duplicate identities (a cloned Beowulf row), so fingerprint resolves go ambiguous; raise only after a
    /// fresh probe.</summary>
    public const int RosterSlots = 50;
    /// <summary>u8 roster-relative SpriteSet id (Dicene UnitData +0x00), the battle body selector. Confirmed
    /// 2026-07-06 (docs/research/SPRITE_SWAP.md + a roster probe on a party monster): 0x82 monster; generics
    /// 0x80 male / 0x81 female; story bodies (0x02, 0x16 Mustadio, 0x1E Agrias) sit under 0x80; guests 0xA2
    /// Balthier / 0xA3 Luso / 0xA5 Argath Deathknight render as humans. AttackRow.Policy.HumanSprite gates the
    /// "Fists" treatment on it and fails closed on any other value.</summary>
    public const int RSprite = 0x00;
    public const int RAccessory = 0x12; // u16 equipped accessory item id. Probe 2026-06-12. Scholar's Ring (id 260) at RosterBase + slot*RosterStride + 0x12; sibling accessories 218/224/226/232 confirmed in adjacent slots. Empty slot reads 255 or 0, never 260.
    public const int RRHand = 0x14;   // u16 right-hand weapon id (FFTPatcher-canonical, == items.json id)
    public const int RLHand = 0x16;   // u16 left-hand weapon id; 0xFF/0xFFFF when empty (stays empty in practice)
    public const int ROffHand = 0x18;  // u16 dual-wield OFF-hand weapon. FFTHandsFree mislabels it "reserved"; a roster dump showed the 2nd weapon lands here (+0x16 stays empty, shields go to +0x1A).
    public const int RShield = 0x1A;   // u16 equipped shield id, separate from ROffHand (2026-06-21 dual-gun roster dump). GunSlinger's twin grant writes only +0x18 and never reads this; the gear-loss reconcile probes here.
    public const int RSupport = 0x0A;   // u16 picked support ability KEY (empty = 0; Dual Wield = 477 = id 221 + 256; reaction +0x08 / movement +0x0C siblings; 2026-08-12). FFTHandsFree's "u8 id" was the low byte
    public const int RLevel = 0x1D;   // u8  (0 / empty slot guard)
    public const int RBrave = 0x1E;   // u8  (fingerprint to find this unit's combat struct)
    public const int RFaith = 0x1F;   // u8
    public const int RNameId = 0x230;  // u16

    // --- combat-struct array (writable stats that drive battle damage) ---
    // Ramza's struct (0x14184F890; PA at +0x3E = 0x14184F8CE) is the anchor. Party units sit at +/- n*stride,
    // self-mapped by the weapon id at +0x20, and are written only where a full combat-struct signature checks
    // out.
    public static long CombatAnchor => ImageBase.At(0x141855CE0);   // 1.5 confirmed +0x6450 (was 0x14184F890): Ramza weapon80/lvl99/hp486/pa18, twin at +0x800
    public const int CombatStride = 0x200;
    public const int CombatSearchSlots = 24;   // scan +/- this many slots around the anchor
    /// <summary>Base of combat-frame index i=0 (i*CombatStride below CombatAnchor's slot n=24); FrameReadBase
    /// == BandReadBase, BandEntry. Matches the probe's FRAME_BASE0 0x141852CE0 (tools/probes/unitid_probe.py,
    /// rechecked 2026-07-01). Seat = (ptr, FrameReadBase) / CombatStride for an <see
    /// cref="ActorPtr"/>.</summary>
    public static long FrameReadBase => CombatAnchor - 24 * (long)CombatStride;
    public const int CWeapon = 0x20;   // u16 equipped weapon id (the self-mapping key)
    /// <summary>u8 level in the combat struct frame (== BandEntry+ALevel == 0x29); GrowthEngine.MatchesEntry
    /// uses it to reject enemy slots that share brave/faith with a player.</summary>
    public const int CLevel = 0x29;   // u8  (== BandEntry+ALevel; one byte before CBrave)
    public const int CBrave = 0x2A;   // u8
    public const int CFaith = 0x2C;   // u8
    public const int CHp = 0x30;   // u16 current HP (== the band framing's +0x14)
    public const int CPa = 0x3E;   // u8  (drives physical damage)
    public const int CMa = 0x3F;   // u8
    public const int CSpeed = 0x40;   // u8
    // Passive bitfields (write + hold confirmed mid-battle 2026-06-08): reaction +0x94 (4 bytes, base id 166),
    // support +0x98 (4 bytes, base 198), movement +0x9C (3 bytes, base 230); MSB-first. Signatures touch only
    // Support (it stacks).
    public const int CMount = 0x1B4;   // u8 Mount Info; bit 0x80 = riding (chocobo). 2026-06-26 (mount_probe.py / chocobo-mount-bytes)
    public const byte CMountRidingBit = 0x80;
    public const int CReaction = 0x94;   // 4 bytes; Maim zeroes this to suppress Counter etc.
    public const int CSupport = 0x98;
    public const int CMovement = 0x9C;   // 3 bytes, base id 230; Rapture holds its teleport image here

    // --- crystal counter (band-entry relative, found 2026-06-16) ---
    // A KO'd unit's "3 hearts" countdown steps 3->2->1->0 per its scheduled turn; 0 crystallizes it. Same as
    // combat base +0x07. Holding SanctuaryHearts (3) stops crystallization while the bearer lives.
    public const int ACrystalHearts = -0x15; // band entry -0x15 == combat base +0x07

    // --- dead / undead status bytes (band-entry relative) ---
    // Layout from the Doom research (doom-status-bytes memory): Dead +0x45/0x20, Undead +0x45/0x10. Dead
    // whatever the HP. The Phoenix-Down undead kill is a status-death the 33ms poll may not see as an HP write.
    public const int ADeadStatus = 0x45;  // u8 status bitfield byte shared by Dead and Undead flags
    public const byte ADeadBit = 0x20;  // mask. Bit 5 of ADeadStatus is the Dead flag
    public const byte AUndeadBit = 0x10;  // mask. Bit 4 of ADeadStatus is the Undead flag
    // Delayed-action bits on the same byte (tools/probes/status_probe.py decode map; actor_attrib_probe.py
    // watchweapon trace 2026-06-26). Jump 0x04 went status[45] 00->04 at jump-commit and 04->00 at landing
    // (~8.6s later). Charging 0x08 sets the same way (charging_probe.py); the untracked-arm cross-turn summon
    // no-credit needs the 1->0 edge (LIVE_LEDGER).
    public const byte AJumpBit = 0x04;
    public const byte AChargingBit = 0x08;
    public const byte ADelayedActionMask = 0x0C;  // Jump | Charging

    // --- poison status bytes (band-entry relative, 2026-06-09) ---
    public const int APoison = 0x48;  // u8 status bitfield byte containing the poison flag
    public const byte APoisonBit = 0x80;  // mask. Bit 7 of APoison is the "poisoned" flag
    public const int APoisonTimer = 0x4A;  // u8 poison countdown timer (engine inits to 36; ticks per CT unit)

    // --- reraise (auto-revive) status byte (band-entry relative, observed 2026-06-14) ---
    // Held re-applied through the death that clears it, this is the engine's own animated Reraise. A lethal hit
    // becomes a played-dead corpse raised at ~10% HP when its CT next reaches 100. Feign Death holds it on the
    // wielder (FeignDeath.cs); the death-commit clears it once, the hold re-stamps.
    public const int AReraise = 0x47;  // u8 status bitfield byte containing the Reraise flag
    public const byte AReraiseBit = 0x20;  // mask. Bit 5 of AReraise is the "reraise" flag

    // --- invisible (transparent) status byte (band-entry relative, observed 2026-06-14) ---
    // Shares +0x47 with Reraise, bit 4 (0x10). Single-target enemies skip the unit, AoE splash can reach it.
    // Feign Death sets it for the played-dead window, then drops it for the finishing blow. Corrected
    // 2026-07-22 (mod off; LIVE_LEDGER "orphan flag" Uncertain row): the bit survives the unit's own action and
    // 60s of clock, but being hit clears it, so a hold re-stamps for splash. +0x47 is the composed layer, a
    // read-out; the engine registers an applied status in the inflicted layer (+0x1D3..+0x1D7). A composed-only
    // write is an orphan flag nothing owns. No transparency, no expiry (ours read composed 0x10 / inflicted
    // 0x00; an engine-applied Stop read 0x02 / 0x02), so anything holding this bit needs a watchdog.
    public const int AInvisible = 0x47;  // u8 status bitfield byte containing the Invisible flag
    public const byte AInvisibleBit = 0x10;  // mask. Bit 4 of AInvisible is the "invisible" flag

    // --- marquee buff status bits (band-entry relative). The band status bytes equal the PSX
    //     current-status 5-byte field (byte N = +0x45+N), cross-checked against the known bits
    //     (Dead/Undead +0x45, Reraise/Transparent +0x47, Poison +0x48, Doom +0x49) and a probe
    //     2026-06-15 (Float +0x47/0x40, Regen +0x48/0x40 on real enemy units). Bit map. FFTHandsFree
    //     StatusDecoder. Larceny steals these (LarcenyPolicy.Stealable). Regen is wired;
    //     Haste/Protect/Shell/Reflect are bit-confirmed. Float set by the bit is cosmetic (icon shows,
    //     no hover; observed 2026-06-15), so Larceny skips it; AFloat stays for status_probe. ---
    public const int AFloat = 0x47; public const byte AFloatBit = 0x40;
    public const int ARegen = 0x48; public const byte ARegenBit = 0x40;
    public const int AProtect = 0x48; public const byte AProtectBit = 0x20;
    public const int AShell = 0x48; public const byte AShellBit = 0x10;
    public const int AHaste = 0x48; public const byte AHasteBit = 0x08;
    public const int AReflect = 0x49; public const byte AReflectBit = 0x02;

    // --- auth band. Live unit data (the static array freezes on battle restart; the band stays live).
    //     Entry layout matches the static-array A* offsets. BandEntry = unit-copy offset inside each
    //     slot; BandReadBase starts at n=-24. A fresh corpse read 0/539 only in the band. ---
    public const int BandEntry = 0x1C;    // unit copy offset within a combat band slot
    /// <summary>Band-relative u16 equipped weapon id (CWeapon, BandEntry = 0x04), the byte GrowthEngine reads
    /// at addr + CWeapon (n = s, 24) and SeatBand writes at e + (CWeapon, BandEntry).</summary>
    public const int AWeapon = CWeapon - BandEntry;   // == 0x04
    /// <summary>u8 band-relative Speed byte, CSpeed(0x40), BandEntry(0x1C) = 0x24. Writing 50 to
    /// entry+0x24 showed Speed 50 and the unit went first (2026-07-01; LIVE_LEDGER band
    /// +0x22/0x23/0x24 = PA/MA/Speed). Use this, not CSpeed, on a band entry.</summary>
    public const int ASpeed = CSpeed - BandEntry;   // == 0x24
    /// <summary>u16 band-relative roster-nameId back-reference. Frame +0x1FC (0x1FC, BandEntry = 0x1E0)
    /// mirrors the unit's roster nameId (<see cref="RNameId"/>, roster +0x230). Found 2026-07-01 by
    /// tools/probes/unitid_probe.py "find" scan-A across two battles. Player seats match the roster (Ramza 1,
    /// Samurai 298, Ninja 271). An engine mirror frame (band seat 28) also carries the mirrored unit's nameId
    /// (298, later 271), so nameId cannot tell an original from a mirror; the <see cref="ActorPtr"/> target
    /// (Band.ActorEntry) always names the real frame, so Iai's release matches it against the captured
    /// Wielder.RosterNameId. The locate layer uses it as tier-1 exact match and tier-2
    /// veto. Wielder.Locate/LocateAll, GrowthEngine.LocateIn/ScanEntries/MatchesEntry/ReadHp,
    /// RingGate.BandHasUnit.</summary>
    public const int ANameId = 0x1E0;
    // Band-relative reaction field. CReaction(0x94), BandEntry(0x1C) = 0x78, 4 bytes (Maim reads, holds, restores it).
    public const int AReaction = 0x78;
    // Band-relative movement field. CMovement(0x9C), BandEntry(0x1C) = 0x80, 3 bytes (Rapture saves, holds, restores it).
    public const int AMovement = 0x80;
    public const int ASupport = 0x7C;   // 4 bytes, base id 198, MSB-first; == CSupport(0x98), BandEntry(0x1C). Choir OR-sets the Non-charge bit here.

    // --- per-unit turn/moved/acted flags (band-entry relative, confirmed 2026-07-09) ---
    // docs/LIVE_LEDGER.md "Per-unit turn/moved/acted flags (the full-wait read)" row (owner pass 2026-07-09:
    // Mushin bank on a still wait, spent on the strike); FFHacktics PSX struct 0x186-0x189, mapped by
    // tools/probes/mushin_wait_probe.py (scratchpad/psxflags_watch.log). PSX offset + 0x32 = frame offset,
    // minus BandEntry(0x1C) = band offset. Promoted from Mushin.cs.
    /// <summary>1 while the unit's move/act/wait menu is open; 0-&gt;1 at turn open, 1-&gt;0 at turn
    /// end. The falling edge is Mushin's turn-end trigger and CursorGate gate B.</summary>
    public const int ATurnFlag = 0x19C;   // u8
    /// <summary>0-&gt;1 at the unit's move; the engine resets it at that unit's next turn open.</summary>
    public const int AMoved = 0x19D;   // u8
    /// <summary>0-&gt;1 at the unit's action; same reset as <see cref="AMoved"/>. PSX 0x189 (frame +0x1BB, band
    /// +0x19F, "Ability Outcome", 0x02 hit-by-ability, 0x01 turn-ended) is not promoted (LIVE_LEDGER
    /// row).</summary>
    public const int AActed = 0x19E;   // u8

    public static long BandReadBase => CombatAnchor + BandEntry - 24 * (long)CombatStride;  // n=-24 anchor
    public const int BandSlots = 49;     // n = -24..+24 around the anchor

    // --- AREC kill diagnostic (band-entry-relative) ---
    /// <summary>u8-array-relative per-unit action record, frame FR_AREC (0x1A0), BandEntry (0x1C) = 0x184.
    /// Sub-offsets +0x0 idx (engine index == seat-8), +0x2 abil u16 (<see cref="ArecAbil"/>), +0xA kind (<see
    /// cref="ArecKind"/>, 5=performing/6=receiving), +0xB xref (victim&lt;-&gt;attacker, unproven and refuted
    /// as a kill-attribution shortcut; docs/LIVE_LEDGER.md Uncertain AREC row). KillTracker.CreditKill only
    /// logs it (Tuning.VerboseEvents); the production reader is LivingPoach.ReadWasBasicAttack
    /// on the killer's record (LIVE_LEDGER "The basic-Attack discriminator" row, 2026-08-12).
    /// Guarded reads.</summary>
    public const int AArec = 0x184;
    /// <summary>u16 AArec-relative ability id 0 (<see cref="LivingWeapon.Tuning.BasicAttackAbilityId"/>) is
    /// the basic Attack command. Read with <see cref="ArecKind"/> == <see cref="ArecKindPerforming"/>
    /// (LivingPoach).</summary>
    public const int ArecAbil = 0x2;
    /// <summary>u8 AArec-relative kind tag <see cref="ArecKindPerforming"/> (5) = this unit's own pending
    /// action; 6 = receiving (a struck unit's stale last-action stamp, noise here). Probable (2026-08-12 owner
    /// probe); LivingPoach depends on it.</summary>
    public const int ArecKind = 0xA;
    /// <summary>The <see cref="ArecKind"/> value for "performing", read at the killer's entry.</summary>
    public const byte ArecKindPerforming = 5;

    // --- display scratch (equipped-weapon menu WP, Ramza context) ---
    // 1.5 confirmed 2026-06-17. MirrorWeapon, 0x1E; read 6 = Venombolt's WP with Ramza's card up.
    public static long WpScratch => ImageBase.At(0x141876E96);   // 1.5 confirmed +0x6660 (was 0x141870836)

    // --- battlefield discriminator 0 = out of battle (world map / menus, even while slot9 is still the stuck
    // 0xFFFFFFFF sentinel), 2/3/4 = on the battlefield (FFTHandsFree CommandWatcher.cs). Slot9 alone cannot
    // tell the world-map party menu from combat (the old "kills update only after restart" bug). ---
    public static long BattleMode => ImageBase.At(0x1409069A0);   // 1.5 confirmed +0x6350 (was 0x140900650): u8 3-in-battle/0-on-map, tracked across 3 transitions Also 255 before the session's first battle, 0 on the map after one (owner tape 2026-10-16, LIVE_LEDGER [sentinel-out-of-battle-tuple]).

    // --- in-battle "BattleStatus" card. Checking a unit's status mid-battle opens the equip card (with the Kills
    // line). Detected (FFTHandsFree ScreenDetectionLogic) as pauseFlag==1 && menuCursor==3 (the Status action-menu
    // slot) && submenuFlag==1. ---
    // 1.5 confirmed 2026-06-17 (display_probe consistency-sample + watch): reads 1 while a menu/Status card is
    // open, 0 on the free battlefield / enemy turns. Two synced copies at 0x140C6B1C8 / 0x140C6B307; the lower
    // is used. (was 0x140C64A5C, +0x676C) 1.5.1 semantics change (observed 2026-07-13, same address): it now
    // reads 1 only while the unit status card is open, not in the command menu or abilities list; on 1.5 it
    // held 1 for the whole player turn. BattleState.InLiveBattle's excuse clause and the #if LWDEV dev-spike
    // gates see a narrower window; docs/research/PORT_1.5.1_OFFSETS.md.
    public static long PauseFlag => ImageBase.At(0x140C6B1C8);
    public static long MenuCursor => ImageBase.At(0x1407FC620);   // 1.5 PRE-1.5/unused. StatusCardOpen does not gate on it ("the card's own cursor once open")
    // 1.5 confirmed 2026-06-17. U8 == 1 only when the Status card is open (0 on the free battlefield, enemy
    // turns and the plain command menu). Found by a 3-state solve (live/menu/card) and reconfirmed across
    // sessions. (was 0x140D3A10C, +0x6752) 1.5.1 moved it (delta -0x52, struct-local reshuffle): the old
    // 0x140D4085E reads 0 in every state. Re-found 2026-07-13 by a 3-state solve (12 samples per state at
    // 0.4s). It reads 1 in the card via both the own-turn Status command and pause-menu Units > Status, 0 in
    // the abilities list (which rejected the generic-panel decoy 0x140D40554) and 0 post-battle. Synced sibling
    // at 0x140D407BA. See docs/research/PORT_1.5.1_OFFSETS.md.
    public static long SubmenuFlag => ImageBase.At(0x140D4080C);

    // --- equip-screen "mirror": the viewed unit's equipped gear in UI row order, [Weapon, LHand, Helm, Body,
    // Accessory] as u16 (FFTHandsFree CommandWatcher.cs, 2026-04-15). Mirror[0] is the weapon whose card is on
    // screen. Two synced copies. ---
    // 1.5 confirmed 2026-06-17 (two-card differential): the u16 read 80 on Ramza's card and 56 on the Umbral
    // Rod card, the only address that tracked both. Delta +0x6660, not the predicted +0x6450 (the 0x14187
    // region slid further than the combat band).
    public static long MirrorWeapon => ImageBase.At(0x141876EB4);   // 1.5 confirmed +0x6660 (was 0x141870854)
    public static long MirrorOffHand => ImageBase.At(0x141876EB6);   // 1.5 confirmed +0x6660 (was 0x141870856). mirror[1]: viewed off-hand; read 143 (Ramza's shield)

    // --- inventory item count array ---
    // Source. Docs/DEV_TEST_RECIPES.md (inventory-give recipe, give_all_items probe). count[itemId] = u8 @
    // InventoryCountBase + itemId. Not safe mid-battle (gated by Engine.Tick !nowIn). The vanilla block; the
    // armed extended inventory relocates it (ExtendedInventory.BagCountBase).
    public static long InventoryCountBase => ImageBase.At(0x1411A7C00);   // 1.5 confirmed +0x6440 (was 0x1411A17C0): dev give-all inventory present at predicted addr
    // --- Bulwark (Sunderer +3, docs/BULWARK_AC.md): the pathfinder's terrain grid, 8 bytes/tile, idx = x +
    // y*mapWidth + layerBit*0x100. Base corrected 2026-07-28 to 0x140D8DCB0 (was 0x140D8DCC0, 2 records high,
    // so writes landed 2 tiles east); the disasm operand [rdx + idx*8 + 0xD8DCB2] (record byte +2 =
    // 0x140D8DCB2) only resolves against the 0xB0 base. The lever is byte +6 bit 0x02 (PathTerrainVetoBit), the
    // engine's own obstacle state (the map's five trees read byte+6 == 0x22): it blocks movement and enemy
    // pathing. Height (byte +2) is no walkability input and softlocked the game (contradicted 2026-07-28,
    // LIVE_LEDGER Contradicted terrain entry). Grid writes persist for the whole process and stale dirt once
    // crashed the game, so every path that ends a hold must restore. Not TerrainGrid (0x140C6B440, Treasure
    // Master's read-only fingerprint scratch).
    public static long PathTerrainGrid => ImageBase.At(0x140D8DCB0);
    public const int PathTerrainStride = 8;
    public const int PathTerrainVetoField = 6;
    public const byte PathTerrainVetoBit = 0x02;

    /// <summary>u8 pair (W, H): the live map's width then height. Matched 11x12 and 10x18 on the two probe maps
    /// 2026-07-27, and the pathfinder's width register (r14 = 11) agreed on the 11-wide map (docs/BULWARK_AC.md
    /// A3). Read from a live battle-phase struct, so consumers must carry BulwarkPolicy.DimsSane and refuse a
    /// garbage read.</summary>
    public static long MapDimsWH => ImageBase.At(0x140C6AD6A);

    // --- Provoke. The ability action table (InflictStatus repoint target) and the hand-authored
    // inflict-status table it applies. Content-anchored, image-static addresses observed 2026-07-22
    // (LIVE_LEDGER row, Uncertain; docs/PROVOKE_AC.md "How it works, plainly"), the same class as
    // Barrage.AbilityBase (a JobCommand table). Covered only by the roster landmark until the per-part work gives them
    // their own vanilla-byte landmark (the PE key is a report line now); they need a re-find on a
    // re-anchor (docs/PATCH_REANCHOR.md). The byte-identical decoy mirror of the action table
    // (ProvokePolicy.DecoyActionTable, Provoke.Policy.cs) is not pinned. Nothing writes it.
    public static long LiveActionTable => ImageBase.At(0x14078B2DC);   // 368 rows x 20 bytes; the copy the engine and UI both read
    public static long InflictTable => ImageBase.At(0x14080FBA0);      // 128 rows x 6 bytes, [mode][s0..s4], mode byte first

    // --- Provoke hold. Side membership and the ghost-seat gate, both read-only. ---
    /// <summary>u8 band-relative friend/foe byte (= combat +0x1EE). <see cref="AFriendFoeEnemyBit"/>
    /// set = enemy, clear = ally. The guardian probe (2026-07-22) read it clear on all 6 player-side
    /// seats, 5 of them guests outside the classic party seat range, so read side from this bit and
    /// never from seat position.</summary>
    public const int AFriendFoe = 0x1D2;
    public const byte AFriendFoeEnemyBit = 0x10;

    /// <summary>u8 band-relative on-field gate (= combat +0x01). Reads <see cref="AGateHiddenValue"/> on a
    /// ghost/cutscene seat that <see cref="Band.IsValid"/> alone misses (hide/reveal proven row plus the
    /// guardian probe, 2026-07-22). The hold skips such seats; Band.IsValid runs first, so a garbage seat is
    /// rejected earlier.</summary>
    public const int AGateByte = -0x1B;   // band entry, 0x1B == combat +0x01
    public const byte AGateHiddenValue = 0xFF;

    // --- Living Poach. The Poacher's Den carcass store, u8[96], one byte per poach.json carcass key
    // (addr = PoachStoreBase + (key, 1)). LIVE_LEDGER row 2026-08-12. Found by a fingerprint scan of the Den's
    // inventory-style array; the owner saw a read (the Den UI reflects the byte) and a write (a poke landed a
    // carcass in the Den). A mid-battle poach does not write the array; the engine increments it at battle end,
    // and a mid-battle mod write survives that (owner-observed 2026-08-12).
    public static long PoachStoreBase => ImageBase.At(0x1411A7A1B);

    // --- Corpse despawn. The engine's render-node removal, promoted from BodyDoubleSpike.cs's
    // Ctrl+F5 dev instrument (LivingWeapon/Dev/BodyDoubleSpike.cs, #if LWDEV) into CorpseDespawn.cs.
    // docs/LIVE_LEDGER.md "DESPAWN any unit mid-battle, sprite and all" (Proven, owner pass 2026-07-10, flip
    // 2026-07-21): writing mode 2 into the node's flag word lets the per-frame node sweeper (0x14026E20C)
    // remove unit and sprite on its next unpaused frame, the same primitive vanilla crystallization uses.
    // One-way. BodyDoubleSpike keeps its own copy of these constants (reason in CorpseDespawn.cs's class doc).
    public static long DespawnNodeListHead => ImageBase.At(0x140D3A410);   // render-node singly-linked list head
    public const int DespawnNodeIdOff = 0x08;              // node id byte (matched vs the current-actor dword)
    public const int DespawnNodeCombatOff = 0x148;         // node -> combat back-pointer (builder-written)
    public const int DespawnNodeModeOff = 0x12C;           // mode flags; all access is to the LOW byte (bits 0x30 = removal mode/done)
    public const uint DespawnNodeModeClearMask = 0x30;     // bits cleared before the mode-2 stamp
    public const uint DespawnNodeModeRemoveValue = 0x20;   // mode 2 = "remove me" (the sweeper consumes it)
    public static long DespawnCurrentActorNodeId => ImageBase.At(0x140CF873C);   // dword. The current actor's node id; never remove it
    public const int DespawnNodeWalkMax = 64;               // bounded list walk, same as BodyDoubleSpike and spawn_probe

    /// <summary>The composed-status byte holding the chest/crystal conversion bit. Status byte 1 (ADeadStatus +
    /// 1 = 0x46, == StatusApply.Composed + StatusApply.StatusByte(id)), composed ids 8-15 MSB-first. Treasure
    /// (id 15; the pop-signature evidence, docs/LIVE_LEDGER.md) is one of them and Blind/Darkness (id 13)
    /// shares the byte, so test <see cref="ACorpseChestBitMask"/>, not the byte. A Blinded unconverted corpse
    /// reads it nonzero (owner-caught 2026-08-12, CorpseDespawn.cs's staleness check false-positived on
    /// it).</summary>
    public const int ACorpseConvertMarker = 0x46;
    public const byte ACorpseChestBitMask = 0x01;  // mask. Bit 0 of ACorpseConvertMarker (id 15, the byte's last slot MSB-first) is Treasure/chest

    // --- Gun Slinger / Crossfire twin-grant consent. A "menu panel is open" flag. Reads 1
    // while a party/equip/shop/save menu is open, 0 on the free map, in travel, and through formation and
    // battle-load, so a gate on it leaves the pre-battle window open (the twin must be in the roster when a
    // battle constructs; see [twin-dualfire-construction-bound]). Found by menu_signal_probe.py (tape
    // lw193_menusig_20260817_063908.log): clean flips on every menu open-close, one 500ms double-pulse on a
    // town-exit fade (hence the 3-pass debounce). Rejected 0x140D506B4, which pulsed 50ms on every travel hop.
    // Sibling 0x140D508EC tracks it and 0x140D50916 is its inverse (neither promoted). See
    // [worldmap-menu-open-byte] (LIVE_LEDGER.md, Uncertain as of 2026-08-17, owner flip pending). An unreadable
    // byte counts as open in GunSlinger.cs, so it fails toward not writing.
    public static long MenuOpenFlag => ImageBase.At(0x140D508D0);   // u8

    // --- Gun Slinger / Crossfire twin-grant consent, owner-AC round. The party-browse stamp. Reads 1
    // on the party overview root and the Character Status page; 0 in E&A (all three tabs), the save menu, shop,
    // travel, formation and battle. Clean edges 2026-08-17 afternoon (tape lw193_menusig_20260817_110948.log,
    // verify-watch method). E&A's own sub-pickers are unprobed; the gate denies on unreadable/0, which covers
    // them. See [party-browse-screen-byte] (LIVE_LEDGER.md); [worldmap-menu-open-byte] narrows to the deny-side
    // half as of this round.
    public static long PartyBrowseFlag => ImageBase.At(0x140D408E2);   // u8

    // --- WeaponPalette runtime. The two resident weapon-palette banks a swing's sprite draws from 16
    // palettes per bank, 32 bytes (16 u16 BGR555 entries) each. Entry 0 is the transparency slot and is never
    // written (the write window starts at +2, 30 bytes for entries 1..15). Write both banks together or the
    // change flickers. A battle load refreshes both banks and reverts every write, hence the snapshot/restore
    // and re-assert. Observed 2026-08-21/22 (ledger rows [resident-weapon-palette-buffer] and
    // [per-weapon-colour-by-turn-repaint], both awaiting owner flip). Same numbers as banks/PAL_STRIDE in
    // tools/probes/lw305_bench_paint.py.
    public static long WeaponPaletteBankA => ImageBase.At(0x140D35750);
    public static long WeaponPaletteBankB => ImageBase.At(0x140D35950);
    public const int WeaponPaletteStride = 32;   // bytes per palette (16 u16 entries)

    // --- Multi-lane growth. The current-brave/current-faith holds, the u16 MaxHp hold, and the
    // turn-scoped resident item-stats WP write. sid == id (the row-index identity WpTableHold assumes) is also
    // checked at bake time by tools/gen_living_weapon_meta.py's check_wp_sid_identity for every "wp"/"wp+faith"
    // weapon.
    /// <summary>u16 combat-struct MaxHp (== AMaxHp 0x16 + BandEntry 0x1C; CHp 0x30 is current HP).
    /// HoldU16 touches only this field, except the Knight Sword top-up (GrowthEngine.Lanes.cs), which raises
    /// CHp by the delta once per real battle so a grown weapon opens at full HP. Proven 2026-08-25,
    /// ledger [maxhp-hold-attribution-safe]: a raised MaxHp hold leaves kill attribution alone.</summary>
    public const int CMaxHp = 0x32;   // u16
    /// <summary>u8 combat-struct current brave (orig is <see cref="CBrave"/>, 0x2A, never written
    /// here). Proven 2026-07-02 (Kobu ships on it), ledger [current-brave-write-sticks].</summary>
    public const int CBraveCurrent = 0x2B;   // u8
    /// <summary>u8 combat-struct current faith (orig is <see cref="CFaith"/>, 0x2C, never written
    /// here). Proven 2026-08-25, ledger [current-faith-write-scales-magic-gun]: a held write scales
    /// magic-gun damage linearly both ways and the forecast panel tracks it.</summary>
    public const int CFaithCurrent = 0x2D;   // u8
    /// <summary>Base of the resident item-stats table that WpTableHold's WP write targets. Row stride <see
    /// cref="ItemStatsStride"/>, WP at row +<see cref="ItemStatsWpOff"/>, row index = sid (== items.json id;
    /// the probe matched id 73, additional_data_ids.json every id &lt;= 127). Proven 2026-08-25, ledger
    /// [wp-table-write-live-damage]: re-read per shot, so a write moves live damage and reverts
    /// clean.</summary>
    public static long ItemStatsBase => ImageBase.At(0x14080F690L);
    public const int ItemStatsStride = 8;
    public const int ItemStatsWpOff = 4;
    /// <summary>Row count of the resident item-stats table (ItemWeaponData has 128 rows; EquipBonus starts
    /// right after it at 0x14080FEA0). An extended id (261+) has no row here (its stats live in the
    /// mod's stub page), so writers must stop at this bound or land in EquipBonus.</summary>
    public const int ItemStatsRows = 128;

    // --- Extended inventory (the 261 item-cap break ported from the FFTHandsFree rig) ---
    // Re-anchored for 1.5.2 on 2026-08-26 (the equip/display cluster slid -0x78 and the accessor cluster -0x74 from 1.5.0;
    // data bases unchanged), checked against the exe on disk 2026-08-27. The ten "Thunk*" addresses
    // were E9 thunks only on the Denuvo build; the Denuvo-free exe (buildid 24304444, read 2026-10-16) has the
    // function entries themselves, so ExtendedDonorThunks pins their real first bytes and AccessorEntryHook
    // steals them (the names keep "Thunk" for the tests and docs that cite them). Provenance per site:
    // docs/research/ITEM_CAP_261_BREAK_JOURNEY.md, 2026-08-26 "the 1.5.2 re-anchor" through 2026-08-27 03:20
    // "the port blueprint"; single-byte patch sites are a table in ExtendedSites.cs. Owner-observed evidence
    // (docs/LIVE_LEDGER.md Uncertain rows [inventory-default-order-drops-unknown-ids],
    // [capbreak-swing-art-via-accessor-clones], [weapon-sprite-pair-drives-swing-art]).
    /// <summary>The four disp32 bytes in the catalog accessor's extended branch
    /// (<c>lea rax,[rax*4 + disp32]</c> at 0x1402B8C66) that select the ids-256+ catalog block;
    /// vanilla value 0x0067F910 = <see cref="ExtCatalogBase"/> minus the image base.</summary>
    public static long ExtCatalogDisp32 => ImageBase.At(0x1402B8C6AL);
    /// <summary>Vanilla extended catalog block (ids 256-260, 12-byte ITEM_COMMON_DATA records,
    /// indexed by the full id, so record 256 sits at base + 256*12).</summary>
    public static long ExtCatalogBase => ImageBase.At(0x14067F910L);
    /// <summary>Main catalog block (ids 0-255, 12-byte records).</summary>
    public static long MainCatalogBase => ImageBase.At(0x14080EA90L);
    /// <summary>Two-byte sprite/palette pair per item id (byte 0 palette nibbles, byte 1 the
    /// drawing), read on every swing. June's probes used base +2, one item off.</summary>
    public static long WeaponSpritePairTable => ImageBase.At(0x140785CF0L);
    /// <summary>Bag count per item id (one byte each); the save stores exactly 261. The vanilla
    /// block; the armed extended inventory relocates it (ExtendedInventory.BagCountBase).</summary>
    public static long BagCountArray => ImageBase.At(0x1411A7C00L);
    /// <summary>the second per-item byte list beside the bag counts (the flag list the reset routine
    /// zeroes with <see cref="BagCountArray"/>, 0x105 entries/0x110 slots). Relocated with the bag counts when
    /// the list relocation arms (ListRelocation.cs).</summary>
    public static long SiblingListArray => ImageBase.At(0x1411A7700L);
    /// <summary>The ten accessor entries the extended ids are redirected through (E9 thunks on the Denuvo
    /// build, plain function entries on the Denuvo-free build, hence "Thunk"). Weapon stats returns a pointer
    /// to an 8-byte ITEM_WEAPON_DATA row; the rest index per-category tables by the id in rcx, and those have
    /// no row for a new id.</summary>
    public static long ThunkWeaponStat => ImageBase.At(0x1402B8C74L);
    public static long ThunkValidity => ImageBase.At(0x1402B8EBCL);
    public static long ThunkTypeProbe => ImageBase.At(0x1402B8EE8L);   // (id, dataType): the shield eviction
    public static long ThunkRangeIndex => ImageBase.At(0x1402B8BCCL);  // -1 for a new id = "not a weapon" = the punch
    public static long ThunkSpritePair => ImageBase.At(0x1402B8E60L);  // the swing art record
    public static long ThunkRangeBase => ImageBase.At(0x1402B8C0CL);
    public static long ThunkSibling1 => ImageBase.At(0x1402B8CD4L);
    public static long ThunkSibling2 => ImageBase.At(0x1402B8D3CL);
    public static long ThunkSibling3 => ImageBase.At(0x1402B8DA0L);
    public static long ThunkSibling4 => ImageBase.At(0x1402B8E04L);
    /// <summary>Plain function entries hooked with Reloaded.Hooks behind a prologue landmark.</summary>
    public static long FnCategoryGetter => ImageBase.At(0x1402890C0L);   // inventory list-build filter (called at 0x140288BF5)
    public static long FnOrderRebuild => ImageBase.At(0x140285DF0L);     // display-order rebuild (drops ids its table lacks)
    public static long FnInventoryReset => ImageBase.At(0x140284500L);   // per-item state reset. Zeroes the bag array below its widened bound

    /// <summary>the swing-prep routine 0x140281CE8 copies the standing swing-id word into
    /// the animation record here, `0F B7 05 00 E7 52 00` = movzx eax, word [rip+0x52E700]
    /// (= <see cref="SwingIdWord"/>), then `mov [rdi+0x20], ax`. Read on the 1.5.2 exe on disk
    /// 2026-08-31 (tools/probes/lw_disk_disasm.py 0x140282073). Plain .code, boot-safe.</summary>
    public static long FnSwingPrepIdCopy => ImageBase.At(0x140282073L);
    /// <summary>The render-statics word the swing art follows (cluster 0x1407B0760 + 0x1A); 0 for an
    /// extended id at swing time (docs/LIVE_LEDGER.md [empty-tile-hand-read-bounds]).</summary>
    public static long SwingIdWord => ImageBase.At(0x1407B077AL);
    /// <summary>Battle unit array the swing-prep routine indexes with `acting slot &lt;&lt; 9` in rsi
    /// (= <see cref="CombatAnchor"/>, 16 * <see cref="CombatStride"/>; slot 16 is Ramza's row).</summary>
    public static long BattleUnitsBase => CombatAnchor - 16 * (long)CombatStride;   // 0x141853CE0

    // --- shop stock for extended ids (found 2026-08-27 evening by a static read of the 1.5.2 process,
    // docs/research/ITEM_CAP_261_BREAK_JOURNEY.md section "shops"). Town-flags table (modloader ItemShopsData,
    // 256 u16 rows, loader signature "00 00 FE 01 FE 01 ...") and the BUY-list builder's two references to it.
    // The builder loops ids 0..0xFF (cmp ebx,0x100 at 0x140288FD9, imm32 at +2), reads the flags word as (low
    // byte << 8 | high byte), tests 0x8000 >> townIndex (town 0 = Lesalia ... 9 = Dorter), then the catalog
    // record's +0x0A chapter byte. The "new stock" badge scan at 0x1403453C7 bounds itself at 0x200 and is left
    // alone. ---
    /// <summary>ITEM_SHOPS_DATA. U16 ShopFlags per item id, 256 rows, .data.</summary>
    public static long ShopFlagsTable => ImageBase.At(0x14067F890L);
    /// <summary>rel32 field of <c>lea r12,[rip+0x3F6989]</c> at 0x140288F01 (next ip 0x140288F08),
    /// which starts the builder's high-byte walker at ShopFlagsTable + 1.</summary>
    public static long ShopBuilderHighByteLeaRel32 => ImageBase.At(0x140288F04L);
    public static long ShopBuilderHighByteLeaNextIp => ImageBase.At(0x140288F08L);
    /// <summary>disp32 field of <c>movzx edx, byte ptr [rcx + rbp + 0x67F890]</c> at 0x140288F3B
    /// (rbp = image base, rcx = id*2): the LOW byte read.</summary>
    public static long ShopBuilderLowByteDisp32 => ImageBase.At(0x140288F3FL);

    // --- the save edges (corrected 2026-08-27 late night after the owner's test 2 found the hooks
    // silent; the first build read the pointer from 0x141D407A0, one digit off. Re-read from the running 1.5.2
    // process and the exe, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md "save edges, corrected"). One global
    // points at the save struct being serialized or applied. The load-apply reads it as `4C 8B 05 98 56 B2 00`
    // at 0x14021B101 (next ip 0x14021B108 + 0xB25698), the serializer at 0x140218F8E;
    // tools/probes/lw346_xref_scan.py counts 46 references and none to the old address. The pointer normally
    // holds the static image buffer 0x142C81C80, so the hooks read whatever it points at after the original
    // returns. Saving runs through the wrapper 0x14021B070 (clears the +0x101 bytes, stamps +0x111, jumps to
    // the serializer at 0x14021B0E3), so hooking the serializer catches every save, autosave included. The
    // load-apply is 0x14021B0E8; a second restore routine at 0x14021DE98 owns the other bag copy. 0x14021DDF0
    // is the async file-op stepper (state dword 0x142E7FADC) and is left unhooked. The struct's +0x100..+0x1B8
    // header (play time in seconds at +0x1B4, chapter/location bytes, four party names at +0x124) round-trips
    // verbatim, so a hash of it is a per-save key that reads the same at save and at load. ---
    /// <summary>u64. Pointer to the save struct being serialized or applied.</summary>
    public static long SaveStructPtr => ImageBase.At(0x140D407A0L);
    public const int SaveHeaderKeyOff = 0x100;
    public const int SaveHeaderKeyLen = 0xB8;
    public const int SaveHeaderPlayTimeOff = 0x1B4;   // u32 seconds = h*3600 + m*60 + s from 0x141856704/708/700
    /// <summary>Offsets into the 0xB8 key window above (header +0x11A, +0x11C, +0x11D): bytes the
    /// game holds at 0xFF while a save is in flight and 0x00 at rest.
    /// <see cref="SaveEdgeTracker.KeyFromHeader"/> zeroes them before hashing so a save and its later
    /// load key identically (loads sample at rest, where the mask is a no-op, but it runs at both
    /// edges). From the owner's 2026-08-28 session. Setting them to 0xFF in the resting headers the
    /// round-trip probe captured reproduces all five save keys the mod logged that night, and zeroing
    /// them makes the save key equal the load key for every observed pair.</summary>
    public static readonly int[] SaveHeaderVolatileOffs = { 0x1A, 0x1C, 0x1D };
    /// <summary>The save serializer (fills the struct; ecx = save kind/slot, dl/r8b = header bytes
    /// +0x112/+0x113). Entry preceded by <c>ret; CC CC</c>; every save caller reaches it through the
    /// wrapper 0x14021B070's tail jump, so this entry covers them all.</summary>
    public static long FnSaveSerialize => ImageBase.At(0x140218F78L);
    /// <summary>The load-apply routine, entered right after the save wrapper 0x14021B070's jump to
    /// the serializer (no padding between): it reads the struct pointer at 0x14021B101 and copies the
    /// struct's bag into 0x1411A7C00 and its roster block into 0x1411A7D10 at 0x14021B1D5.</summary>
    public static long FnSaveApply => ImageBase.At(0x14021B0E8L);
    /// <summary>The second restore routine (its bag copy at 0x14021E1D1 zeroes count[0] first, as
    /// the load-apply's does at 0x14021B1C8; it reads the struct pointer at 0x14021DEC9 and
    /// 0x14021E1B6). One plain-code caller, 0x1402FB9EE; which game action drives it is unread, so
    /// it is hooked with the same handler and carries its own canary.</summary>
    public static long FnSaveApplyB => ImageBase.At(0x14021DE98L);
    /// <summary>The static image buffer the pointer above normally holds; diagnostic only, the hooks
    /// follow the pointer.</summary>
    public static long SaveStructStatic => ImageBase.At(0x142C81C80L);

    // --- The two weapon menu order templates (2026-08-30, disassembled from the
    // 1.5.2 exe on disk; re-derive with tools/probes/lw351_order_template_probe.py --disk). They set
    // which item ids the party inventory and the unit equip picker will list. They are save state:
    // the load-apply (FnSaveApply) restores both from the save struct and the serializer
    // (FnSaveSerialize) writes them back.
    //   load    0x14021B4BD  lea rdx,[rip+0x59708C] -> 0x1407B2550 (dest), rcx = [r8+0x8A6C] (src)
    //   save    0x1402194BC  lea rdx,[rip+0x59908D] -> 0x1407B2550 (src),  rcx = [r8+0x8A6C] (dest)
    // A save written before an extended id ever seated restores a template without it, so the mod
    // seats owned extended ids itself after the load-apply returns (Persistence/TemplateSeat.cs).
    // Both tables are u16 item ids ending in a 0x00FF marker; the menu lists the order-rebuild hook
    // works on end in 0xFFFF.
    /// <summary>The party inventory's weapon display-order template, named by the pointer table
    /// 0x14067F498 slot 0.</summary>
    public static long InventoryOrderTemplate => ImageBase.At(0x1407B2550L);
    /// <summary>Capacity of the table above in u16 words. The load-apply's copy into it moves 0x11A = 282 bytes
    /// = 141 words (the loop at 0x14021B560 runs r10 = 2 iterations of rsi = 0x80 bytes, r10 from <c>lea
    /// r10d,[r11+2]</c> at 0x14021B1C4 with r11 counted down to 0, rsi from <c>lea esi,[rbx+0x7c]</c> at
    /// 0x14021B11C with ebx = 4; the tail at 0x14021B5A9 adds 16+8+2 = 0x1A bytes). The next named object is
    /// 0x1407B266C (picker pointer table 0x14067FAD0), 0x11C up 141 words plus two padding bytes.</summary>
    public const int InventoryOrderTemplateWords = 141;
    /// <summary>The unit equip picker's weapon display-order template, named by the pointer table
    /// 0x14067FA90 slot 0. It sits in a 0x3F2-byte block the load restores from [r8+0x8C7C] (the copy
    /// at 0x14021B6F9. R9 = 7 iterations of 0x80 plus a 0x72-byte tail), which also holds the
    /// picker's three other sub-tables.</summary>
    public static long PickerOrderTemplate => ImageBase.At(0x141874540L);
    /// <summary>Capacity of the table above in u16 words. The next sub-table (pointer table slot 1,
    /// 0x14067FA98) is 0x14187465A, 0x11A = 282 bytes = 141 words up, like its twin.</summary>
    public const int PickerOrderTemplateWords = 141;

    // --- order-template relocation. Both templates are read only through a
    // pointer-table slot (11 plain-code sites read the three slots below), and the picker block
    // hides a fifth sub-table, "every owned item, any category", at the shared picker base plus 0x1E6 (finding
    // 4, reviewer RE re-check (a), reproduced in LW371_plan.md). Moving all three charts re-points ten fields
    // (three slots, five rip-relative int32s, two menu-list-cap bytes);
    // tools/probes/lw371_order_template_relocate.py --scan re-derives each.
    /// <summary>Slot A naming <see cref="InventoryOrderTemplate"/> (reviewer (b): housekeeper
    /// 0x140285F80, list build 0x140288D55, rebuild callers 0x1403382B3 / 0x14036B1D9).</summary>
    public static long InventoryOrderTableSlot => ImageBase.At(0x14067F498L);
    /// <summary>Slot B naming <see cref="InventoryOrderTemplate"/> (reviewer (b): the third
    /// housekeeper copy, 0x14039684C).</summary>
    public static long InventoryOrderTableSlotB => ImageBase.At(0x140689C38L);
    /// <summary>Slot naming <see cref="PickerOrderTemplate"/> (reviewer (b): picker housekeeper
    /// 0x14028609F, callers 0x14033694F / 0x140336CEF).</summary>
    public static long PickerOrderTableSlot => ImageBase.At(0x14067FA90L);
    /// <summary>The picker block's fifth sub-table, "every owned item, any category".
    /// No slot names it; reviewer table (a) lists its rip-relative fields (a shared rsi base at
    /// [rsi+rcx*2+0x1E6]/[...+0x1E8], four direct disp32 fields). Its ceiling is 261 owned kinds;
    /// past that it overwrites 0x141874932.</summary>
    public static long PickerAllItemsTemplate => ImageBase.At(0x141874726L);
    /// <summary>Capacity of the table above, in u16 words 0x20C bytes.</summary>
    public const int PickerAllItemsTemplateWords = 262;
    /// <summary>The shared list builder's entry-cap byte. The imm32 low byte of <c>cmp esi,0x91</c> (six bytes,
    /// 81 FE 91 00 00 00) at 0x140288CC1, widened 0x91 (145 entries) -&gt; 0x95 (149). Both stack
    /// list buffers hold <see cref="ListBuilderStackRoomBytes"/> bytes before the GS cookie (reviewer (d): fnA
    /// rsp+0x70..rsp+0x1A0 via <c>lea rbp,[rsp-0xb0]</c> then <c>mov [rbp+0xa0],rax</c>; fnB true entry
    /// 0x140336B88, rsp+0x50..rsp+0x180 via <c>lea rbp,[rax-0xa8]</c> then <c>[rbp+0x80]</c>). The builder
    /// writes cap+1 words, but fnA (entry 0x1402875A4, the picker's weapons-only per-slot list) then appends
    /// the unit's two hand items (stores at 0x14028778C and 0x1402877A9, each if validity thunk 0x1402B8EE8
    /// accepts) and its own 0xFFFF terminator (0x1402877C0), so it needs cap + 3 words (0x95+3)*2 = 0x130. Cap
    /// 0x97 (151) would put the terminator on the cookie at (151+3)*2 = 0x134 and fast-fail the process
    /// (STATUS_STACK_BUFFER_OVERRUN).</summary>
    public static long ListBuilderCapByte => ImageBase.At(0x140288CC3L);
    /// <summary>The list-insert bound byte. The imm32 low byte of <c>cmp edx,0x92</c> (six bytes,
    /// 81 FA 92 00 00 00) at 0x140286318, widened 0x92 (146) -&gt; 0x96 (150 = builder cap + 1, so the
    /// loop still reaches the terminator at index 149).</summary>
    public static long ListInsertBoundByte => ImageBase.At(0x14028631AL);
    /// <summary>Bytes between the smaller stack list buffer and its GS cookie (reviewer (d), both
    /// callers re-read 09:00. FnA frame 0x1B0, buffer rsp+0x70, cookie rsp+0x1A0; fnB entry
    /// 0x140336B88, frame 0x190, buffer rsp+0x50, cookie rsp+0x180). fnA's two hand items plus
    /// terminator make the limit cap+3 words. A cap fits when (cap+3)*2 &lt;= this.</summary>
    public const int ListBuilderStackRoomBytes = 0x130;

    // --- lifts the Items-tab draw limit from 149 to 255. The two cap sites
    // above widen further (TemplateRelocation.Sites.cs CapSites. Builder cap 0x91->0xFF, insert bound
    // 0x92000000->0x00010000), unsafe for the stack callers fnA/fnB. A hook on the builder (ListBuilderHook.cs)
    // holds them at 149 through a mod-owned pool and leaves static-buffer callers unchanged.
    /// <summary>The shared list builder's own entry (read 2026-08-31 on the 1.5.2 exe on disk; corrected
    /// by disasm): <c>int Fn(nint a, nint b, nint c, nint list, nint spare, nint mode)</c> (Microsoft
    /// x64; a/b/c are cx/dx/r8b widths read only in their low bits, list = r9 the output buffer; args 5/6 are
    /// stack arguments at the caller's [rsp+0x20]/[rsp+0x28]). Prologue. Mov rax,rsp; mov [rax+0x20],r9; two
    /// word-arg spills (see <see cref="ListBuilderHook.ExpectedPrologue"/>). 15 bytes, no rip-relative field in
    /// the stolen window. The prologue pushes 7 registers then subs 0x20, so entry rsp (rax) is
    /// rsp+0x58 and argument 6's home slot is rax+0x30. The body reads <c>byte ptr [rsp+0x88]</c> (= rax+0x30)
    /// at 0x140288BCA, 0x140288C61, 0x140288C8D and 0x140288CA1, four branches on the mode byte. It also reads
    /// <c>[rsp+0x78]</c> (= rax+0x20, argument 4, the list buffer) and <c>[rsp+0x68]</c> (= rax+0x10, argument
    /// 2), which cross-checks the frame arithmetic against two arguments of known identity. No
    /// <c>[rsp+0x80]</c> (= rax+0x28, argument 5) read exists in the body; the hook forwards argument 5 as
    /// given. All eight call sites set argument 6 right before <c>call 0x140288b94</c> at their own
    /// <c>[rsp+0x28]</c>, e.g. 0x14028774D (<c>mov byte [rsp+0x28],3</c>, call at 0x14028775E) and 0x14036C45E
    /// (<c>mov byte [rsp+0x28],r9b</c>). The mode byte, from the callee's branches. <c>== 3</c> at
    /// 0x140288BCA reads ownership from the bag count alone (0x1402847F8) instead of count plus sibling flag
    /// (0x140287530); <c>== 0</c> at 0x140288C61 jumps to 0x140288CB6, skipping the can-equip filter; <c>&gt;=
    /// 2</c> at 0x140288C8D drops the id when can-equip (0x1402886D0) answers -1; <c>== 1</c> at 0x140288CA1
    /// sets bit 14 (the grey E badge) and keeps the row. A wrong "equip picker" mode discards every id the
    /// wielder cannot equip. </summary>
    public static long FnListBuilder => ImageBase.At(0x140288B94L);
    /// <summary>The game's static 256-word menu list buffer (37 plain-code sites read/lea
    /// it, all at +0, none through a stack temp). The hook rule runs the widened (255-entry) builder unchanged
    /// only against this address; anything else passed as r9 (a stack buffer, or a caller this sweep never met)
    /// goes through <see cref="ListBuilderHook"/>'s mod-owned pool and is truncated back to <see
    /// cref="ListBuilderHook.StackCallerCap"/>.</summary>
    public static long MenuListBuffer => ImageBase.At(0x141811470L);
}
