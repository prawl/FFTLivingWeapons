# User Feedback

STATUS: CONTRACT (curated player feedback ledger)

Playtest feedback log -- raw observations from real sessions, often with other mods
loaded (Harder Story Battles, a randomizer, "True Swordsman"). The observations are the
player's; parenthetical scope/triage notes are added during capture and not yet verified.

## 2026-06-21

### Movement items (Move +1) feel overpowered
- Added Move on gear is very strong. Ramza with **Trailwarden** + **Wayfarer Boots** can
  cross half the battlefield in a single move.
- Trailwarden is an early item, so the "zoom all over the stage" power arrives far too soon.
- In the cadet fight (with Harder Story Battles), both the enemies and the starter units
  are all running it, so the entire field is hyper-mobile.
- Request: nerf added-Move on items.

### Early armor with situational / "useless" riders
- A lot of early armor carries riders that feel useless at that point in the game.
- Example: a Ch2 armor piece that absorbs and boosts Holy. The only Holy sources seen early
  are Priests and Excalibur, so the rider does nothing for most of the run.
- It reads like a "penalty" item -- as if you're meant to hold that early piece for a future
  Excalibur user rather than equip it now.
- (Hasn't tested end-game yet; this is an early-game pacing impression.)

### Damage scaling / build power
- "True Swordsman" grants Doublehand. A **Dragoon** that can equip swords hits **100+**
  easily, even into Knights with an innate defense boost. Two-handed swords on an
  off-class striker may be over-tuned.

### Mustadio shows up without a gun (likely NOT our mod)
- Mustadio joined as a guest (level 18) unable to equip any weapon at all until he becomes
  a permanent party member.
- That run's gear came from the randomizer mod, so this may be a randomizer / guest-unit
  interaction rather than ours. Needs confirmation before assuming it's our mod.

### Living Weapons + Harder Story Battles interaction
- Loves that enemies also get the Living Weapons system. Wants enemies to **hit harder** and
  to actually **use** the living-weapon benefits if possible.
  - _Scope note (verified against code 2026-07-04): enemies inherit only the STATIC item rebalance --
    the reworked weapon/armor stats + riders baked into the item tables, which every equipper gets on
    restart. They do NOT get the living-weapon RUNTIME: the kill tally, the `+`/`+2`/`+3` stat lift,
    the awakened +3 signatures, and the Kills card counter are all player-roster-only (KillTracker
    credits "the acting player's weapon(s)"; GrowthEngine holds only player combat structs). So
    "enemies actually use the benefits" is a net-new feature -- deferred (see docs/RELEASE_SCOPE.md),
    not a tuning fix._
- On **Tactician** difficulty, New Game+ makes the chapel fight an auto-fail: Ramza is locked
  at level 12 while the rest of the field is level 50+ with 50+ living-weapon items. Effectively
  impossible -- had to drop to **Squire** to get past it and continue NG+.
- Outside that, the **cadet fight** is the hardest in the game: enemies outscale your units even
  though both sides have the same gear, because Ramza starts at level 1 with basic equipment.

## 2026-07-21 (after downloading 2.3.0)

### Only Ramza and generics benefit from the LW system
- Special/story units appear excluded from the living-weapon runtime. (Triage: [LW-96] in
  docs/TODO.md; needs a repro to pin which surface skips them.)

### Equip-axe is back on Squires
- Reported as a regression. (Triage: [LW-97]; check the emitted tables vs another mod's stomp.)

### Fists show the previous unit's weapon name
- An unarmed unit's Attack row wears the weapon name of the unit that acted the turn before.
  (Triage: [LW-98]; likely the LW-91 stale-paint lifecycle with nothing to overwrite it.)

### Nagrarok missing (separate report)
- Nagrarok was equipped on Beowulf and "turned into another sword". (Triage: [LW-99].)

### Positive
- Loves the new changes: the weapon name replacing "Attack" in the menu "feels nice", and the
  new +3 for Outrider is "awesome".

## 2026-08-26 (Nexus posts sweep, all 4 pages, plus one private message)

Source: the mod page's 96 posts read end to end in the owner's browser, and the
Xeoshades PM. Standing at read time: 40 endorsements, 1,523 total downloads, 12,214
views, ZERO open bug reports, rank 4 on the game's two-week most-endorsed list (the
owner's Treasure Master spinoff holds rank 1).

### Actionable
- The kill thresholds are undiscoverable AND the mod page is stale: two users
  independently (bongchoof, eddyca) hunted for the threshold knob and failed, and the
  page prose still says 50 kills while the shipped curve has been 5/10/15 since the
  2026-08-12 softening. Captured as [LW-343].
- Faerie Harp charm regret (azavierj1218): the vanilla harp's Charm proc was the game's
  only charm weapon; our Sleep replacement erased that uniqueness and the player mourns
  it. Balance-signal for [LW-318]; the display half (procs silent on cards) is [LW-331].
- Orran escort fight spike (TankyCrobat, Tactician): every thief and chemist carrying a
  bow or gun focus-fires Orran for a near-unwinnable opener. Feeds [LW-318]/[LW-321]
  (the thief-bow buff is loved, the encounter tuning is not).
- Compat demand cluster keeps growing: Antidote/Regabond item collisions
  (Denverplays2), Red/Blue Mage and Dark Knight job mods (mediadragon, kaylin04442,
  Krice01), and the community is hand-merging tables to cope (Moonpyramid777's
  NotePad++ guide). Feeds [LW-177]/[LW-296].
- Xeoshades PM ("Jade", 2026-08-14): building a Cloud/Buster Sword mod on ids 67-70
  and 49, believing 48/50 are free; every one of those ids is a load-bearing living
  weapon, so the mods collide totally. Wants a compat patch and offered collaboration;
  also independently confirmed the ex-flail distorted swing [LW-312]. The honest
  unlock for BOTH the Cloud mod and the axes/flails complaints is the item-cap break.
- Axes/flails removal controversy (SpyderZT, Davidlangevin): variety loss felt; the
  owner's public answer (234-item hard cap, restore them when the cap breaks) stands.
- PanicCatz: wants a vanilla-names variant (mechanics only). Architectural tension,
  recorded honestly: the Kills counter is painted into the DESCRIPTIONS the mod bakes,
  so a vanilla-text variant loses the on-card counters; not ruled, owner's call.

### Handled / historical
- bongchoof's three posts (slow counter paint, day-two tally loss, threshold hunt):
  owner handled via Discord 2026-08 (his note, 2026-08-26). The slow-paint half is
  now [LW-324] (in build at capture time); the counters-lag observation is the same
  complaint the owner filed himself.
- a412045249's 1.5.2 stand-down prompt: the fingerprint guard working as designed;
  the re-anchor shipped the same week.
- TerraEpon's new-game crash (old post): matches the parked Bloodpact ability-nxd
  corruption era, fixed/parked 2026-06; not current.
- azavierj1218's "weapon animations don't match the icon colors": SHIPPED since
  (the LW-251 battle palette, owner live pass 2026-08-24); good release-notes line.

### Positive
- "Huge fan of equipment not turning into junk" (Xeoshades) is the build-diversity
  thesis quoted back verbatim; GameDadVII made a YouTube video on the mod; multiple
  players praise the thief-bow identity and the +3 signatures.

## 2026-08-30 (Discord reports)

### Actionable
- Poach does nothing with the mod on: a player's Ninja with the Poach support could
  not poach; turning the mod off on the same save made poach work again. Awaiting the
  player's livingweapon.log and details (weapons in both hands, which monster, whether
  the killing blow was the plain Attack command). Captured as [LW-358], which also owes
  the never-written poach FAQ entry. (Triage, not yet verified: mod-off-works is
  exactly the LW-166 data-layer prediction; with the mod on, the fifteen reworked
  weapons poach only through the runtime Living Poach cure, which fires only on plain
  Attack kills, needs the mod armed, and cannot see LW-174's five story-battle
  monster jobs.)
  - Outcome, same day: the player sent livingweapon.log and the flight folder, but both
    captures were taken mid-battle, before any kill, so the failed attempt was never
    recorded. The log proves the mod armed (prod 2.3.3) and the Ninja is Ramza with only
    Hushblade; the tapes hold five full battles with zero kills by dormant-formula
    weapons. The player then became unavailable, so the owner closed the chase and
    promoted [LW-358] to Now as a proactive re-verify of Living Poach on the current
    game version plus the FAQ entry.
- Twin-weapon (forced dual wield) reports (Darkrapid): three observations in one post.
  (1) A twin weapon cannot be single-wielded except by occupying the other hand; that is
  the LW-193/194 consent design working as built, but it reads as a restriction to the
  player, so the poach/mechanics FAQ should explain it. (2) Bug: with a shield in the OFF
  hand, backing out of the equip menu removed the shield and added the second weapon; only
  a MAIN-hand shield stuck. The owner replied with the intended rule (off-hand shield
  should decline the twin) and promised a look. (3) Bug: "change the weapon to daggers and
  you seem to temporarily gain dual wield", so the granted Dual Wield support appears to
  linger after a disqualifying swap. Captured as [LW-359].
- "It's good otherwise 15 weapon damage weapons show up a bit early keep one shotting
  my mages but I was also level 13 with enemies now in 20s lol" (verbatim, same Discord
  thread). Early access to WP 15 weapons echoes the owner's own "steady then boom"
  finding (shop-stock Warbrand/Ravager); feeds [LW-320]'s obtain-vs-power audit and
  [LW-318]. The player flags their own level deficit as a confound.
