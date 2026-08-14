# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [LW-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash. New entries are written ELI5-first (a plain-language opening anyone
can follow, technical detail after), per the Format rules in docs/TODO.md; rows written
before 2026-07-21 keep their original prose.

## 2.3.3 cycle

- [LW-10] SHIPPED 2aee150 2026-08-14: Treasure Master is gone from the mod, not merely switched
  off. It marked the battle tiles hiding Move-Find treasure, had shipped disarmed since 2.3.0,
  and had been slated for removal for six weeks; carrying it cost a background thread, a baked
  dataset in every release, a build-pipeline gate, the mod's only player-facing config toggle,
  154 tests and a block of verified memory addresses, all for a feature nobody was running.
  Removed whole: the eight runtime files and their folder, their tests, the four generator tools
  and the treasure-db pipeline step, the tile datasets and capture probes that fed it, the shell
  helpers that drove those probes, the two tick-table rows, the Tuning knobs, and the three
  Offsets anchors nothing else read. Bulwark's PathTerrainGrid is a different table and stays,
  with its cross-reference reworded to cite the address rather than a deleted symbol, since that
  comment exists to stop someone writing to the wrong grid. Two things stayed on purpose: the
  game's OWN Treasure status (composed id 15, the unit-to-chest conversion) with its dev spike,
  which is engine knowledge unrelated to the module, and the LIVE_LEDGER rows recording what the
  module's reverse engineering proved, which are a record and not a dependency. The Scholar's
  Ring survives as an item and its description was rewritten to say what it actually does,
  double JP, instead of advertising the module; item.en.nxd was re-baked for that one cell
  (INTENDED 1130 / DRIFT 0 / UNINTENDED 0). Player-facing consequence, stated plainly: the mod
  now exposes NO settings at all, so the launcher's configuration pane is empty, and a test pins
  that rather than letting it drift. (Tech: the stage-1 work on
  feature/lw10-remove-treasure-master was 339 commits behind main and predated the LW-182 folder
  restructure, so it served as a checklist and the removal was redone on main. Suite 2947 green,
  down 154; analyze exit 0; 32,036 lines deleted. Four pinned contracts caught the loose ends and
  were updated with the change rather than around it: the LW-184 phase table, the tick-table
  non-battle allowlist, the LOGGING.md verb-table lockstep, and the config-surface reflection
  guard.)

- [LW-230] SHIPPED 495f9fc 2026-08-14: every recoloured icon looked like it was giving off
  coloured smoke, and the shields and helmets no longer do. Each sprite is drawn sitting in a
  soft see-through haze the artist left neutral, and the recolour engines painted every pixel
  down to the faintest edge, so the haze took the item's identity colour too. The owner spotted
  it on the hat previews, the fix landed there first, and he then asked for the families he had
  already approved to be re-baked so they carry it. The 16 shields and 13 helmets shipped;
  the 115 weapons deliberately did not, because pulling the smoke off them exposed an older
  defect underneath (see LW-232), and the owner scoped those to their own family passes.
  Confirmed in game 2026-08-14: shields "look fine", helmets landed correctly. Honest scale of
  the fix, since it is a small change on these two families and the owner said so on first
  look: measured as the share of a card's identity colour that was sitting in the haze rather
  than on the item, weapons are 27.5 percent, hats 12.0, helmets 8.2, shields 4.6. (Tech:
  _halo_weight ramping alpha 48 to 224, blended via _halo_int in each lane's OWN quantizer,
  called from two_zone_bright, small_bright, shield_two_tone and helm_recolor; legacy excluded
  by design with a selftest tripwire, since its 72 items are unreviewed. Proofs: 0 of 107,237
  solid weapon and 0 of 48,389 solid shield pixels move against the pre-fix engine, hats and
  crossbows and legacy byte identical, bake matches previews 58 of 58, ten mutations each turn
  a named check red. New: icon_preview.py compare, which diffs the working engine against any
  committed revision, and a bake WARN naming any item whose tint reaches under 2 percent of its
  solid art. recolor_icons.py --selftest was wired into tools/pipeline.ps1 as a real gate, with
  Pillow pip-installed in release.yml for the step.)

- [LW-231] SHIPPED fb02f80 2026-08-14: recoloured icons came out mottled with coloured speckle,
  and the hats and crossbows now carry the artist's own smooth shading. The engines expand light
  against dark one pixel at a time, which multiplies the art's own compression grain, and under
  a saturated tint that grain reads as blotchy cloud across the surface. The fix expands the
  contrast on a softened copy of the brightness and returns the fine grain at reduced strength.
  It stays on the eighteen items that needed it. Extending it to the thirteen helmets was built,
  gated, baked and REVERTED the same day (42f6c11) on sight: a blur cannot tell a drawn
  one-pixel line from compression grain, and helmet art is engraved metal whose whole subject is
  that line work, so the Sunsteel crown's scale rows and the Timeward's black seams smeared.
  Every aggregate measurement passed while that happened (blurred difference 8 to 11 of 255,
  tonal spread down under 10 percent, grain swing down 42.3 percent) precisely because those
  metrics are blind to one-pixel features, which is the lesson worth keeping: for a change about
  fine detail the picture is the instrument and the summary statistic is not. Weapons and
  shields stay out for a simpler measured reason, no contrast expansion to protect. Confirmed in
  game: crossbows "look great" 2026-08-14, hats passed with LW-216. (Tech: _value_fields plus
  DETAIL_GAIN 0.30 in zone_recolor; the selftest now renders a one-pixel engraved grid through
  both contrast engines and requires the per-pixel curve to keep at least 25 percent more line
  contrast, plus a pin that the two engines are deliberately NOT interchangeable.)

- [LW-188] SHIPPED 5f95a5e 2026-08-14: chapter 1 stopped being a hovering parade. The owner saw
  a majority of early enemies floating off equipped gear, because the rebalance had grown the
  game's Float sources from two to five and one of the new ones sat on the single most-worn
  enemy helmet in the game. Enemy auto-equip honours the vanilla level gates our sparse tables
  never touch, so every level-15+ knight wore the Float helm and every level-38+ caster the
  Float robe. The two rebalance-added armor Floats were reverted: Sunsteel Helm trades innate
  Float for Jump +1, Empyrean Robe trades it for Silence immunity, and Float now lives only on
  its vanilla sources plus the Cursed Ring. Enemy Float prevalence returns to exactly vanilla.
  Owner verified in game 2026-08-14 ("working for a while now"). (Tech: re-points only, no
  EquipBonus row redefined, descriptions re-baked into item.en.nxd via patch_names.py with the
  audit reading INTENDED 1130 / DRIFT 0 / UNINTENDED 0. Also shrinks LW-170's half-float render
  bug exposure to rare late pieces.)

- [LW-202] SHIPPED 8b80e06 2026-08-14: the six crossbows were the drab corner of the weapon
  list and now every one of them is a colour you can name. The owner's word for the family was
  dull and it was true twice over: the tints were timid, three of the six sitting at a
  saturation of 0.15 or below, and the engine was wrong for the art. A crossbow is line art, a
  thin stock and a curved limb and a string, so the weapons engine's two-cluster split found no
  second cluster and handed back the vanilla sprite with a wash over it. Each now runs one hot
  identity colour over the limb and frame with a bright metal on the stock, found by
  saturation rather than brightness, which is the only key that can see two materials in line
  art. The owner passed the gallery and then confirmed the in-game look on 2026-08-14: "look
  great", with the six reading as six distinct colours in the inventory list. (Tech: routed to
  zone_recolor via ZONE_OVERRIDES, which engine_for consults BEFORE any category rule precisely
  so a per-item opt-in can beat a family default; _material zone specs on a desat key claiming
  23 to 26 percent of the solid sprite on all six. Proofs: recolor selftest green, bake byte
  identical to the approved previews, every untouched family unchanged. Two dead item names in
  the tint table, ids 78 and 81, were corrected afterwards under LW-201.)

- [LW-216] SHIPPED fb02f80 2026-08-14: every hat in the game wore one flat colour smeared over
  every pixel, and now all twelve carry a real colour scheme. The flat stamp erased the exact
  thing each hat is named for: the Wardplume's plume, the Zephyr Beret's feather, the Arcanist
  Cap's painted star. Four of them (the Roughspun Cap, the Adept's Hood, the Martial Band and
  the Assassin's Cowl) were decided outright rather than sent back for another round of
  lettered options, on the owner's instruction to just colour them, and he passed the whole set
  in game. The hats needed a new engine to get there: the old one splits a picture into a body
  and one accent, which is all a helmet is, while a hat is cloth plus a brim or lining plus a
  crest or a painted emblem. The new one lays as many zones as the art can carry, in order,
  with the last one winning where two overlap, which is what lets a white star sit inside a
  bright crest instead of blending into it. Two long-standing flaws in the recolouring were
  fixed here too, on the hats only: sprites no longer look like they are giving off coloured
  smoke, and surfaces no longer come out mottled with speckle. Hair adornments share the equip
  slot but ship under LW-217. (Tech: engine hat_recolor in tools/recolor_icons.py routed as
  "hat-three-zone", recipes in HAT_OVERRIDES, body tints in items.json iconTint, third mask key
  "desat" on a saturation percentile because a brightness key cannot see white paint on pink
  felt. LW-230 halo ramp and LW-231 smooth-field contrast ride along for hats. Proofs: bake
  byte-identical to the approved previews on all 28 surfaces, icon_preview verify 28/28, git
  status showing exactly 28 changed .tex files so every untouched family is proven unchanged,
  selftest and analyze.py green, suite 3101/3101. Follow-up faa9af9 rebuilt four selftest pins
  that an adversarial pass proved were passing without testing anything.)

- [LW-215] SHIPPED fb02f80 2026-08-14: the helmets are finished. Eleven of the thirteen shipped
  in eabc893 last week; the last two, the Mendsteel Helm and the Timeward Helm, had been left on
  the old flat tint because their picks never landed, and both of their old colours collided
  with a sibling anyway. They were decided rather than lettered, in the same pass as the hats,
  and the owner passed them in game. The Mendsteel is the one green helmet, which is what its
  Regen and Poison immunity should look like, with gold on the inset panel the artist painted
  across its brow. The Timeward is the head slot's one bright metal, which is the honest answer
  to a shelf that has no unclaimed colour left on it. (Tech: 145 and 151 join HELM_OVERRIDES on
  style "helm". 151's accent rides shade rather than cover because its bright pixels are
  scattered horn highlights that a cover mask returns as confetti; an ice blue version was tried
  and dropped since four helmets are already blue. Same proof set as LW-216 above.)

- [LW-227] SHIPPED eabc893 2026-08-13: the check that proves "the icon we shipped is exactly
  the icon that was approved" had been passing without looking at any colour, and it is honest
  again. The comparison asked the imaging library whether two pictures differ, but the newer
  library answers that by looking only at which pixels are see-through, so a red and a blue
  picture compared as identical (proven against the shipped red Genji Helm versus its untinted
  vanilla art). The comparison now reads the actual bytes, red-versus-blue and alpha-only
  regression cases pin it in the recolor selftest, and re-running the whole catalog under the
  honest check came back 468 of 468 clean, which also retroactively confirms the shields and
  weapons shipped correctly all along. (Tech: Pillow 10 made getbbox alpha-only by default;
  tools/icon_preview.py verify now uses the shared bytes-level images_equal from
  tools/recolor_icons.py. Found and fixed inside the LW-215 helmet pass.)

- [LW-189] SHIPPED 4872ed5 2026-08-13: every weapon icon got per-surface recoloring instead of
  the one-hue stamp, and the owner gave the live verdict the same week it deployed: the weapons
  look ACCEPTABLE in game, which closes this row's remaining half. "Acceptable" is also the
  reason this row's successor exists: with the LW-190 shields setting a higher bar, the owner
  judged the first-pass weapon tints hasty and opened the full-catalog re-pass program (LW-198
  through LW-226, one row per equipment section), so the polish work continues there rather
  than by reopening this row. What this row delivered stands: the two-zone card rule, the
  BRIGHT hue-ramp smalls, the per item overrides, the owner-approved 121-weapon gallery, the
  242 of 242 pixel-identity production proof, and the preview pipeline that every later pass
  reuses.

- [LW-197] SHIPPED dbb7f1f 2026-08-13: three recolored shields had been reviewed under names
  that do not exist in the game, and the fix was corrections plus one owner call, both now
  done. The icon tint table had carried the labels "Ronin Wall", "Conduit" and "Bastion" for
  ids 140-142 since before the shields pass, while the real items keep their vanilla names on
  purpose (Genji Shield, Kaiser Shield, Venetian Shield); nothing in game ever showed a wrong
  name, since items.json is the naming authority and it was always right. The comments are
  corrected, and the one real consequence was settled by the owner the same day: the Genji
  Shield KEEPS its shipped cold steel-blue. The deciding authority is the item's own
  description, "an unusually shaped, pitch-black shield forged from iron", which the dark
  blue-black rendering already honors in game (owner screenshot on the completed save); the
  set-harmony alternative (crimson, to match Genji Helm/Armor/Gloves) was considered and
  rejected against that prose. Lesson banked in the tint table itself: a comment is not a
  naming source.

- [LW-190] SHIPPED cca4acc 2026-08-13: every shield icon now wears its own two colours instead
  of the old one-hue paint dip, and the owner confirmed the in-game look the same day the bake
  shipped ("it looks so good in game", the live half of the verify). Sixteen shields were
  settled one by one across twelve owner review rounds, including two ten-variant picker pages:
  Aegis Prime landed on bright sapphire with gold kept to the edges and gem, Wardstone on a
  thin white geometric rim over rich purple, Conduit on a copper cross over a bright blue
  field, and Emberward on burnt ember under gilt fittings, with the rest called by name along
  the way. The weapons card rule was tried first and rejected on evidence: a shield is one
  convex plate, so colour clustering follows the lighting rather than the materials, and it
  produced half-painted shields and camouflage speckle. (Tech: the shield engine in
  tools/recolor_icons.py finds the second material by saturation, with per-shield override
  modes the owner picked (gold / keep-vanilla / second-tint trims, inverted tint-on-fittings,
  strict saturation split, forced-cover brightness split, and a geometric BFS ring from the
  sprite silhouette); bright relief rides a tanh shoulder instead of the weapons' hard clip;
  the sixteen tints are respaced with a selftest tripwire against collisions. Proof: bake
  matched the approved previews 32 of 32, the 121 weapon icons re-proven byte-identical 242 of
  242 against the committed engine, suite 3101 green, analyze exit 0. Preview equals production
  by construction: tools/icon_preview.py imports the engine's route().)

- [LW-186] SHIPPED 3aaefdd 2026-08-13: the engine's tick table now defends its own shape. The
  phase list that says what runs when can be handed to tests as plain data, no engine built
  and no game memory touched, and the "after" ordering notes on its rows are enforced instead
  of decorative: a test fails if a note points at a row that runs later, if a cited name no
  longer exists (so a rename cannot quietly void a constraint), if two rows share a name, or
  if a row not on a reviewed allowlist of outside-battle rows lacks the in-battle gate. (Tech:
  the owner-directed design note: Engine.BuildPhases went internal static with a nullable
  engine, BuildPhases(null) returning the real production rows with their Run closures
  unbound, chosen over a shape-only copy that could drift and over a named-delegates bag that
  doubles the wiring surface; construction never dereferences the engine, only the deferred
  lambdas do. Three tests in EngineTickTableTests.cs; the older After walk in EngineTests was
  deleted as subsumed, with Phases_match now pinning After values on the instance table as the
  bridge. Five sabotage rounds each turned exactly the predicted single test red, including
  the rename round where the ordering test stayed vacuously green and the resolve test caught
  it. The three-reviewer adversarial round returned seam 9/10 ship and dev-define baseline
  9.5/10 ship, and the test skeptic's catches, the row-name uniqueness assert, the corrected
  scholar-ring allowlist reason, and the instance After pin, were folded in before the commit.
  Suite 3101 green in the gated config; the surfaced dev-define gate blind spot is captured as
  LW-187.)

- [LW-183] SHIPPED 6158503 2026-08-13: the live ledger is readable again. Each of its 118
  claims now opens with one or two sentences saying what is true right now (current claim,
  current status, latest date), with the complete original history preserved word for word
  inside a collapsible fold below, and every entry carries a short slug that code and docs
  can cite precisely. No status moved and nothing was lost: a mechanical check proved every
  address, date, id, filename, and word from the old rows survives, and an adversarial
  review of the fresh summary lines caught and fixed five stale-pointer defects before the
  commit. (Tech: the four sections and the owner-only PROVEN rule are unchanged; the three
  free standing Contradicted terrain paragraphs survive in place; suite 3099 green.)

- [LW-182] SHIPPED a61775c 2026-08-13: the runtime's two hundred source files no longer live
  in one giant flat directory. Everything is folded into twelve domain folders (Guard, Memory,
  Logging, Battle, Kills, Growth, Signatures, Display, Chronicle, Persistence, Dev, and the
  quarantined TreasureMaster), with only the seven spine files left at the project root, so
  finding the kill code is now opening a folder instead of scanning two hundred names. Pure
  git renames, no namespace or code changes, and every gate stayed green. (Tech: Offsets.cs
  and Tuning.cs stay root-pinned for the Python tools that parse them by path; the proof-claim
  ratchet and the AnchorScan portability test were reworked to be layout-proof; stale path
  citations in probes, items.json notes, and pipeline comments were re-pointed in the same
  commit. An adversarial verify pass confirmed the moves as 193 exact renames before commit.)

## 2.3.2 cycle

- [LW-137] SHIPPED df8b779 2026-08-12: the worry that the kill counter could hand credit to
  the wrong side because its death-edge check secretly tracked the cursor is measured and
  closed, with no code change needed. The measurement instrument (killcredit_probe.py,
  shipped df8b779) ran passively beside the owner's regression night and recorded five real
  death edges: the shipped reading called every one correctly (an enemy acting at all four
  player-unit deaths, the player acting at the owner's Defender kill) and was never caught
  disagreeing, while the proposed replacement, the per-unit turn-flags walk, had NO answer
  at four of the five edges, because deaths resolve during action resolution when those
  flags are blank. Census: four B-NO-OWNER, one AGREE-AI, zero disagreement lines. So the
  shipped check stays, the replacement is disqualified as structurally blind at death
  edges, and the cursor worry closes on a real session. Owner-directed close 2026-08-12.

- [LW-171] SHIPPED 4bd256b 2026-08-12: the Arbalest crossbow learned the twin trick. Once it
  has grown to its third tier, its wielder automatically gets a second Arbalest loaded into
  the off hand and Dual Wield granted, so the basic Attack fires twice, the same deal the
  Outrider Pistol's Gun Slinger gives guns. The signature is named Crossfire (owner picked
  the host and the name: the Arbalest is the crossbow line's rider-free clean sniper, so the
  twin IS its identity, and Eclipsebolt keeps its single Doom signature). Under the hood the
  Gun Slinger module now supports any number of flagged weapons, each wielder receiving a
  twin of their own main-hand weapon, so a future twin on a newly proven category is a pure
  data edit. Owner live pass same day: the twin rendered on the equip screen, Attack fired
  twice in a real battle, one Arbalest kill was credited cleanly, the pistol lane was
  re-verified with and without a support ability picked, and swapping the main hand between
  pistol and crossbow moved both the off hand and the support correctly. The two battle-load
  moments where the command row read plain Attack were the card's designed fail-closed
  compose (cursor resolve NoOwner or BridgeFail, zero warnings), not a regression. Built by
  the compressed pipeline: five pinned tests born red first, suite 3091 green, analyze exit
  0, and an independent adversarial verify at SHIP 9/10 that proved the load-bearing tests
  non-vacuous by sabotage (both historical failure shapes caught).

- [LW-169] SHIPPED dd36845 2026-08-12: a player deciding whether to install can now read one
  public page, linked straight from the Nexus description, naming every mod that cannot run
  beside this one and why. The page (docs/COMPATIBILITY.md, rendered by GitHub) names the
  six item rebalance mods that cannot coexist (both sides rewrite the same gear tables and
  the loader keeps only one, so no load order helps), the caveats most players could hit
  (custom job mods can cost the three weapon granted commands, a non English game hides the
  kill counter until switched to English, two text mods can cover our card cells), and its
  unknown column ended the survey honestly EMPTY, every named mod settled, including Deep
  Brave Story verified compatible payload by payload. Every mod name links to its Nexus
  page. Shipped across ee9bffc (the grid), 8f2c7a1 and 99b4b9d (the verdicts), and dd36845
  (the links); DocsContractTests owns the doc's existence and link integrity. The closing
  step landed outside the repo 2026-08-12: the owner pasted the two drafted Nexus blurbs
  carrying the grid URL into the mod description. The in game guard extension half lives on
  as LW-177.

- [LW-167] SHIPPED ac43327 2026-08-12: weapons rebuilt on the game's dormant damage formulas
  can Poach again, the real cure behind the retired "No Poaching." card notice. A unit with
  the Poach support who lands a basic Attack kill with one of the fifteen affected weapons
  now gets a carcass in the Poacher's Den at the vanilla odds, a toast saying what was
  claimed, and the corpse removed with no crystal, exactly the deal a vanilla weapon gets.
  Ability kills never fire it, because the game itself poaches weapon-strike ability kills
  and firing there would pay twice. Shipped across five stages (policy and map, seam and
  executor disarmed, corpse despawn, discriminator and arming, the LW-166 notice reversal),
  with the owner's live pass catching three real bugs the 3000-test suite could not see
  (the species arithmetic, a Blind false positive on the chest check, a crystal double
  payout), each fixed in ac43327 with its falsifying case pinned as a permanent test; the
  fix batch then survived a 14-agent adversarial verify round whose two confirmed catches
  were captured as LW-174 (shipped same day) and LW-175. The completing live pass, evening
  2026-08-12: five living poaches end to end (goblin, red panther, skeleton twice, black
  goblin), the last-enemy poach ending its battle cleanly with the count intact, and the
  Poacher's Den UI matching the store bytes exactly. The pause beat is recorded as covered
  in spirit rather than run, since the pending state it wanted to observe is not reachable
  by hand (the pause menu is blocked during kill resolution and the despawn completes in
  milliseconds). The Den store and discriminator LIVE_LEDGER rows flipped on this pass at
  the owner's direction; the twin crossbow row stays Uncertain, its flip riding LW-171.

- [LW-168] SHIPPED fd7274b 2026-08-12: the Outrider Pistol's twin gun trick now works for a
  unit with no support ability equipped; before this, such a unit silently never received
  Dual Wield and the second pistol fired once. The bug had two layers and the owner's live
  pass caught the deeper one: the first fix admitted the empty slot's real reading of 0,
  then planted the placeholder ability Toadja on screen, which exposed the true mechanism.
  The roster support slot is a u16 ability Key (empty 0, Dual Wield 477, live id 221 plus
  256); the shipped u8 write of 221 had only ever worked by borrowing a resident high byte
  from units that already had a support picked. The fix handles the slot as the full Key
  end to end (snapshot and restore verbatim, so unequipping returns a truly empty slot) and
  migrates legacy gunslinger.json snapshots that stored only the low byte, so updating
  players cannot be handed a phantom ability. Owner live pass same day: Dual Wield applied
  from nothing set, clean re-assert against the equip screen's normalize, truly empty
  restore, and the LIVE_LEDGER discovery row (the roster picked ability block: reaction
  +0x08, support +0x0A, movement +0x0C, all u16 Keys) flipped to Proven on that pass.

- [LW-166] SHIPPED 8aa4d8e 2026-08-12: fifteen weapons that can never trigger the game's Poach
  ability now say so right on their cards with a plain "No Poaching." line, so a poacher
  learns the limit in the shop instead of over a dead monster. A player report supplied the
  decisive weapon lists; the pattern landed exactly on the damage formula, and the owner's
  live matrix confirmed and tightened it the same day: only the vanilla handler formulas
  {1, 2, 3, 4, 6, 7} carry the game's poach branch, and every dormant PSX handler the
  rebalance revived (45, 46, 47, 48, 67, 69, 99) lacks it, proven across weapon classes
  (a formula 45 crossbow failed identically to the knives) and across lanes (formula 48
  failed despite its vanilla twin working). The notice is generated, never hand written: the
  description assembler appends it for every dormant formula weapon, so a future weapon
  cannot ship without it, and a new analyze gate refuses any formula that has not been
  classified poach capable or not. One flavor line was trimmed for the card ceiling
  (Sunderer, 90 to 74 chars, owner blessed); Hushblade rides the tightest card at 204 of
  205 chars and rendered clean in the owner's live pass alongside Sunderer and a clean
  Vagabond control. The baked item.en.nxd was ground truthed to carry exactly fifteen
  notices by byte count; adversarial verify shipped it at 9 of 10 with double sabotage
  non vacuity. The stakes that motivated the notice: tier 6 is the poach acquisition tier,
  so a poacher wielding an affected weapon could not farm the mod's endgame items and had
  no way to learn why. The real cure, a runtime Living Poach on the proven despawn and
  inventory give primitives, is captured as LW-167.
- [LW-163] SHIPPED 303ddf6 2026-08-12: the weapon card's painted kill counter no longer freezes
  for players who reload saves quickly. The player report that opened this said it best: kills
  mostly did not track until the weapon leveled, and then you are flying blind. Their flight
  tapes proved counting and saving were always correct and only the painted text lied: the
  painter's pool coverage latch was one way, and its only resets (battle end, new game, the
  paused battle status card) were starved by a fast reload loop, since the battle exit clock
  suspends inside menus, so dead paint sites were never rediscovered and every card rebuilt from
  them showed a frozen or reset looking number. Fixed by making the latch re check itself
  against the live site cache (while covered, sites can only shrink, so one integer compare per
  tick catches a drained cache and the next display tick re locates, even inside a stuck battle
  bracket) plus a battle enter Invalidate as defense in depth; the adversarial plan review
  proved the enter edge cannot fire in a fully starved bracket, which reversed which edit
  carries the fix. Proof chain: born red drained pool test, 2990 tests green, analyze green,
  adversarial verify SHIP at 9 of 10 with double sabotage non vacuity, and the owner live pass
  2026-08-12 reproduced the starved bracket on tape (kill 21 credited, no battle end line ever
  fired, two edge less re locates healed the card before the status page even opened, the
  counter reading correct straight away). The same report's graphical glitch (reset looking
  cards after load, the Attack row lagging behind its weapon) was this same blind painter wearing
  different costumes; the healthy Attack card machinery correcting it mid battle is what proved
  the data underneath was fine. Known residual stays named in the LIVE_LEDGER Uncertain row:
  freed but intact buffers that false verify both anchors would defeat both edits; it did not
  appear live.
- [LW-161] SHIPPED 1793e1f 2026-08-12: weapons now level up at a pace a real playthrough can
  actually reach: +1 at 5 kills, +2 at 10, and +3 (the signature ability) at 15, replacing the
  old 5/25/50 climb. The owner's first full playthrough forced the issue: playing heavily, a
  third of the way through the game, exactly one weapon reached 50 kills, meaning the mod's
  headline feature effectively did not exist in normal play. Existing saves promote instantly on
  load because tiers are recomputed from the banked tallies. The scope stayed deliberately
  small: the first threshold stays 5 and the widest meter body stays 11 chars, so the baked card
  scaffold in item.en.nxd needed no rebake (audit_nxd_bakes green, zero drift). Guarded by a
  born-red curve pin plus a widest-meter-body derivation test recomputed from the thresholds,
  both proven non-vacuous by sabotage; nine suites' literal boundary picks were re-derived to
  keep probing both sides of every threshold; six stale comments claiming the old curve were
  corrected across three adversarial verify rounds (final verdict SHIP at 9 of 10). The
  Reliquary Mark placeholder scaled down with it (25 to 10, still a placeholder). Owner live
  pass 2026-08-12 on a production deploy: staged tallies at 6 and 12 kills painted the right
  suffixes and meters, and the launch header, arming, and command grants all came up clean.
- [LW-160] SHIPPED 7745bd0 2026-08-08: the nine test suites that exercise the kill counter no
  longer hand-copy the same setup helpers; one shared fixture (KillTrackerFixtures) owns those
  bodies, every suite reads from it through using-static imports, and not one calling line or
  assertion changed meaning. The owner retired the per-file mirroring convention in session,
  which was this item's parked precondition. Proof carried the ship: two adversarial auditors
  read both full diffs (assertion counts flat per file, 312 asserts and 155 facts), hashed
  every pre-fold helper body against the fixture, and mechanically parsed all 1,328 helper
  call sites to confirm everything past the shared positional prefix is passed by name, which
  defuses the three real silent-rebind hazards the two helper lineages hid. Nine exact-set pin
  tests freeze the fixture's writes against verbatim copies of the pre-fold bodies; a sabotage
  pass in an isolated worktree proved them non-vacuous (an extra-byte write and a bit-clobber
  both go red) and exposed one hole, a boundary off-by-one in the enemy branch that all 2985
  tests missed, now covered by a born-red-proven boundary pin. The audit also REVERSED one
  rider, the row's best lesson: ChoirTests' ally-fingerprint seed, deleted as dead by analogy
  to the bearer-side twin LW-157 removed, was actually a live tripwire (the aura-era Choir
  gated grants on that oracle, so deleting the seed let a filtered-aura regression pass the
  anti-aura test vacuously); it is restored with the real contract documented in place. The
  analogy failed because the bearer seed fed a positive assertion and the ally seed arms a
  negative one; only the first kind dies safely on a "nothing reads it" proof. The other rider
  stands: LarcenyTests' fifth inline enemy-oracle seed rides BandFixtures.SeatEnemyFp,
  byte-equivalent under its LW-157 pin. Work 49732d2 + bfee1f5 + 7d381fc + 7745bd0; suite 2977
  to 2986; zero production files touched.
- [LW-157] SHIPPED ce24cc9 2026-08-08: the test suite's copy-paste tax is paid down everywhere
  no owner decision was needed. The swap-a-logger-in, run, restore-in-finally ritual, 51
  hand-rolled sites across 16 files with two competing restore conventions, is one disposable
  LogCapture scope whose Dispose restores the prior logger (proven equivalent to the
  UseNullLogger family at top level, structurally leak-proof, sabotage-proven: a scope that
  stops swapping turns exactly 52 tests red, all in converted files); twelve Display
  constructions ride CardFixtures.MakeDisplay; all eleven temp-dir try/finally rituals ride
  the TempDirs fixture; the byte-identical SeatEnemyFp enemy-oracle seeder folded into
  BandFixtures with an exact-set pin and its four copies repointed; and the residue was swept,
  including a new born-red-proven test for ExtraTurn's previously uncovered guarded-read
  success arms and KillerDead release. Assertions stayed byte-untouched throughout, verified
  by two adversarial auditors who read both commits' full diffs (143 assertion lines out, 143
  identical back in) and re-derived both dead-mark deletion proofs independently; zero
  production files changed. The suite ends at 2977. The one excluded half, the KillTracker
  suites' shared seed helpers, split honestly into LW-160: their per-file mirroring is a
  documented deliberate convention and retiring it is an owner call. Work e742def + ce24cc9;
  the row itself was briefly deleted by an over-greedy ledger edit and restored from history
  in 0351e8f, named there as the mistake it was.
- [LW-158] SHIPPED c75f793 2026-08-07: the Arcanum's buff-stealing trigger, the one user of the
  shared HP-drop detector that no test watched, has its stateful pin. A full staging (wielder
  acting, struck foe carrying Reraise) proves the steal's real observable, the bit transfer off
  the foe onto the wielder, fires across an observed HP drop; the pin was born red under the
  exact dead-detector sabotage that had previously broken Larceny silently while 41 tests
  guarded its five siblings, plus a steady-HP control proving nothing writes without a drop.
  Accepted residue, stated honestly: the row's two smaller notes partially resolved themselves
  (the charm-lock transitive-pin concern died with the module in LW-159) and the remaining two
  nits (Puppeteer's Release revert is pinned only transitively; the untracked-bury console pin
  asserts a fragment of the frozen sentence) are accepted as-is.
- [LW-155] SHIPPED d27fbe4 2026-08-07: the finished, dead, and lying code the deep dive
  verified is retired. The verb-less legacy logging lane (five ModLogger statics, five
  bare-string ILogger members, their two sink implementations) had zero production callers and
  is deleted, with nineteen LoggerTests migrated onto the typed lane, the one assertion that
  was only true on the legacy path replaced by stronger per-sink shape checks, and the spent
  migration ratchet converted into a live tripwire pair proven against the real deleted shapes
  (twenty hits on the old tree, zero now). The flight recorder's first-error arming and the
  two-sink rule are byte-unchanged. Plague.IsTurn, a zero-caller second copy of the 90/70 CT
  thresholds, is deleted, and the two comments pointing future work at retired LW-149 now
  state the deliberate bound separation guarded by the open owner ledger row. patch_names.py's
  never-False SCAFFOLD_LIVING knob is gone with the scaffold unconditional, proven by
  byte-identical dry-run and audit output. Engine.cs's font comment names the Umbral Rod. The
  LifeSap half of the old item (d) shipped inside LW-159's deletion instead, as planned.
- [LW-159] SHIPPED 9f927d5 2026-08-07: the four ability lanes no weapon can trigger anymore
  are deleted from the shipped mod, per the owner's 2026-08-07 directive. An adversarial
  dormancy audit first proved each lane inert (activation traced to a meta key no item emits)
  and, just as important, proved every OTHER module alive, catching the two traps that would
  have made a naive sweep destructive: Plague activates on a bare keyless signature block
  (Venombolt) and ExtraTurn is id-keyed with no meta gate at all, so neither could be judged
  by missing keys. Deleted whole, one commit per lane: the charm lock (0d62c81; Galewind moved
  to Puppeteer), the Wyrmblood regen splash (729c063; the Dragon Rod carries no signature and
  the design sheet agrees; HealPulse deliberately stays as Renewal's core), the LifeSap
  on-kill heal (9f927d5; the one live primitive its suite pinned, Signatures.FreshKill, moved
  to SignaturesTests first), and the forTurns timed-stat arm (9f927d5; a pure predicate
  amputation, token-compared to leave the mount lane's LW-100/109/110 machinery byte-identical,
  every timed-window test converted 1:1 onto the mount gate). Roughly 1,300 lines of
  unreachable runtime and test code are gone; meta.json regenerates byte-identical throughout;
  the meta bake's unknown-key gate now fails loud if any deleted key ever returns. Three
  independent adversarial auditors returned SHIP over the whole span; their cosmetic findings
  (a log-glossary row unmarked historic, one stale docstring) are fixed in the retiring
  commit. Suite 3051 to 2975 with coverage survival verified pin by pin; no live pass needed,
  deleting code no data can reach cannot change what players see.
- [LW-153] SHIPPED 523aa30 2026-08-07: five families of shipped weapon code that were
  maintained as hand-synced copies each live in one home now, so a fix lands once instead of
  landing twice and silently missing a sibling. The healing twins (Mending Staff's Chebyshev
  aura, Dragon Rod's Manhattan splash, ~85 token-identical lines) run one HealPulse core with
  their five real differences injected and every narration string moved byte-identical; the
  fold exposed that the whole stateful heal loop had ZERO direct tests, so nine pulse pins
  were written first, including a mirrored diagonal pair that holds the twins' one deliberate
  metric difference. The same-unit write-safety check behind every held write is one
  Band.SameUnitAtExact at its four copy sites (Rapture's level-exempt check and Plague's
  drift-tolerant SameVictim fenced out by name); its sabotage exposed that only one of four
  consumers pinned it, so CharmLock, Puppeteer, and Maim each gained a stranger-at-the-held-
  address pin born red under the break. The HP-drop and HP-rise trackers are one
  direction-parameterized HpDeltaState (a shared-core sabotage turned 41 tests red across
  five consumer suites at once); the kill tally and legends store share one
  SidecarJson.SaveAtomic (also the single doorway an LW-28 save diagnostic would tap; the gun
  store's current-generation bak stays out by design and its doc no longer describes a backup
  step its code never had); and the kill counter's owner-flagged untracked-bury ruling is one
  helper, so the wording pin covers the orphan path that previously had none. Two independent
  adversarial auditors returned SHIP with byte-level token comparison of every moved body and
  string; their one real finding, Larceny as the unpinned sixth HP-delta consumer, is filed
  as LW-158, and their doc nits are fixed in the closing commit. Stages 484cfca, 811a79d,
  2e17cbf, 523aa30; suite 3030 to 3051; no live pass needed, behavior byte-identical.
- [LW-154] SHIPPED ae2709f 2026-08-07: the code that answers "which unit is acting right now",
  the heart of kill credit, no longer spells its trickiest step three times over. The
  turn-queue sanity read and the twin filter (the discard-and-restart bookkeeping that keeps a
  frozen mirror seat from stealing or spoiling credit) each live in one named home inside
  ActorResolver (TryReadTqActor, the TwinFilter struct), with each of the three resolvers
  keeping its own accumulation and ambiguity policy; the roster walks share their fingerprint
  match rule (RosterFpMatches, the LW-39 edit point) while their bodies stay separate on
  purpose (set-equality accumulation vs a band-confirmed mid-loop return, now said plainly in
  the comments instead of promising a follow-up seam). Proven the hard way: six twin-filter
  pins written FIRST against the old code (a pre-refactor sabotage turned exactly the predicted
  one red), then post-refactor sabotages of the SHARED filter turned exactly the three restart
  pins and exactly the three skip pins red, one edit reaching all three resolvers, which is the
  refactor's whole point demonstrated. The work also exposed and closed a real coverage hole:
  dropping FAITH from the fingerprint rule left all 3036 then-tests green, so two faith pins
  were born red under that sabotage and now hold the rule honest; a ninth pin (verifier
  requested) holds the main-hand restart's ambiguity clear. Two independent adversarial
  auditors returned SHIP with an exhaustive state-case equivalence proof; their comment nits
  are fixed in the closing commit (the shared homes' docs scope their claims to this resolver
  and name the register lane's own copy of the rule). Work 5f9d95a, follow-up ae2709f; suite
  3030 to 3039; no live pass needed, behavior byte-identical.
- [LW-156] SHIPPED d4eb42b 2026-08-07: the item-bake tooling keeps each rule in ONE place now,
  so a rule change lands once and the checker can never blame the wrong file. Four stages, each
  proven to change nothing the tools produce. (a) 9a5c5d3: the bake's sort-and-cells derivation
  is one pure function (patch_names.item_intent) that the writer drives its guarded UPDATEs
  from and audit_nxd_bakes imports, ending the token-identical inline copy that turned a
  one-sided rule edit into a deploy refusal pointing at the audit; proven by byte-identical
  patch_names --dry output and a byte-identical full nxd audit, and the verifier re-executed
  the DELETED copy against the shared one over all 1442 cells including insertion order. (b)
  9f1f6b6: the meta bake's ~23 hand-written signature passthrough branches became one ordered
  table with the same truthiness gating, plus a NEW loud gate: a signature key the table and
  allowlist do not know fails the bake naming the item and the key, instead of silently
  shipping an inert mechanic; meta.json regenerates byte-identical and a planted bogus key
  turned the bake red in both my probe and the verifier's independent one. (c) ce34516:
  analyze.py's three private grid-CSV loaders became one (duplicate-id detection stays
  exclusive to the sync gate; the p3 gates keep last-write-wins), and the sigName-else-
  displayLabel card-name rule lives once in lib/flavor.py for the bake and both name gates;
  analyze stdout byte-identical in both modes, and the verifier also probed doctored CSVs
  (duplicate rows, deleted file) old-vs-new identical. (d) 73eec0f + d4eb42b: the ability
  rename tool bootstraps its vanilla cache from the game pac like its status sibling, so a
  fresh checkout no longer dies on a missing hand-placed file; proven by hiding the cache,
  regenerating it byte-identical, and restoring the original. Two independent adversarial
  auditors returned SHIP on all four stages; dotnet suite 3030 green throughout; no live pass
  needed, the shipped mod bytes are untouched.
- [LW-151] SHIPPED 659593a 2026-08-07: the tests' pretend memory is now as strict about partial
  reads as the real game guard, closing the last open row of the 2026-07-28 smell audit. Before,
  a check like "can I read these two bytes" passed when only the first byte was declared good,
  so a test could stage half a field and still watch the code succeed; now every byte of the
  range must be declared, exactly how the shipped guard treats a partially mapped region, and
  the honest mode is the default rather than an opt-in switch. The flip broke 101 tests across
  26 suites; every marking site now declares the exact span the shipped code reads, widened
  centrally through the LW-149 shared fixtures first and cited line by line elsewhere. The
  honest gate also exposed about a dozen tests that had been green for the wrong reason (a
  refused read fail-safing into the asserted outcome); all were de-vacuated, two proven by
  sabotage (Choir's ghost-row pin now catches a lenient-occupancy swap; Barrage's release
  all-or-nothing test now reaches the flag-word writable guard it exists to prove). The only
  production change is visibility (GrowthEngine.StructSpan internal so tests cite the real
  constant). Verified by three independent adversarial diff audits re-deriving every widened
  length from production, a completeness critic sweeping every multi-byte gate, and the
  default-flip sabotage (reverting the one line turns exactly the new pin red). Honest residue,
  noted not fixed: dead marks and stale comments the critic flagged (ChoirTests' unused
  static-array mark, TreasureMasterTests' gate-free band marks, AttackCardFixtures' vestigial
  TurnQueue mark, ExtraTurn's uncovered CT and HP reads). Suite 3029 to 3030; no live pass
  needed, nothing in the shipped runtime changes behavior.
- [LW-152] SHIPPED f553a1a 2026-08-07: the kill counter's four test-blind corners each have their
  first test, and each is proven to catch exactly its own break. The orphaned-corpse-with-no-
  known-killer path is pinned as going pending into the shared credit machinery, verified against
  BOTH the current dispatcher and the pre-split code (the verifier swapped the old file in and
  watched the new tests pass, then broke the old code the old way and watched exactly one test
  die), so the pin canonizes long-standing behavior. The three latch copy-backs (the console
  armed gate, the fallback-credit provenance flag, the battle log's weapon tag) each go red when
  dropped; one turned out partly compiler-guarded (deleting the actor-tag copy-back does not
  compile), banked in the verify record. Tests only, no production change; suite 3029; verify
  SHIP 9/10 with four mutations run instead of the two asked.
- [LW-150] SHIPPED 40a02b2 2026-08-05: the audit's five structural seams are taken, each its own
  green commit, runtime behavior byte-identical throughout and every claim proven by token-level
  comparison against the old code plus sabotage runs. The balance gate's twelve copied check
  stanzas became one registry loop that cannot forget the failure code, proven output-identical
  in both modes and failure-identical on a seeded duplicate (3f04327). The katana hold's 156-line
  tick became a short dispatcher with its two buried judgment calls freed into the policy file
  and truth-tabled, including a deliberate asymmetry now written down (d5d75aa). The kill
  counter's 280-line corpse loop split into named handlers with its subtle fall-through preserved
  exactly (c31e188), and its eight-field turn latch became its own tested machine behind a
  mirror-outputs carrier so every consumer reads the same fields it always did, held by a
  25-mutation campaign (c693db1). Finally the engine opened its memory seam: for the first time a
  test constructs the composition root, arms the real guard over a fake, and watches the tick
  read through the injected memory, with the two load-bearing array orderings pinned element for
  element instead of comment-enforced (40a02b2). Honest residue banked as LW-152: two
  pre-existing kill-credit coverage gaps the verify passes exposed, neither created by the
  refactors, both proven present in the old code too. Suite grew 3009 to 3025; verify scores 8
  to 9 of 10 across five stages.
- [LW-149] SHIPPED 7dbe862 2026-08-05: the audit's copy-paste families each live in ONE home now,
  shipped as eight green-gated stages over two days, every one adversarially verified with its
  load-bearing pin broken once on purpose, and runtime behavior byte-identical throughout (the
  moved bodies were diffed token against token, the log lines survived character for character,
  and the growth lanes' ledger-recording rhythms were pinned against the old code before anything
  moved). The stages and their commits: the backward job offset promoted to Offsets (2960ecc);
  the shared heal math and grid metric into neutral homes (3376f2a); the copied test seeding
  helpers into one fixture with an exact-address pin (6f30b01); sixteen activation announcements
  onto one edge detector (c150029); the seven copied unit sanity checks into Band.TryReadUnit
  (4e03e36); the roster walks split into their two honest occupancy rules with ghost-row pins
  (e9827ea); three growth lanes onto one ownership machine with the two genuinely different lanes
  measured and left alone (04dc584); and the nxd patch safety library plus the deploy script's
  preserve round-trip (e6aa847, 7dbe862). Honestly excluded and still ledgered: the frozen
  weapon-command trio (LW-125 waits on the owner drill), the static enemy-array bound (owner
  LIVE_LEDGER question), TimedStat's hold (a different machine), and the strict-range default
  (LW-151, re-measured 97 tests across 23 suites). Suite grew 2907 to 2996; verify scores ran
  8 to 9 of 10 across the eight stages.
- [LW-147] SHIPPED 070974a 2026-07-29: the test fakes the whole suite leans on can no longer let
  an assertion silently go blind, and the one gap too wide to close in a day is fenced honestly
  instead of papered over. Closed outright: a sixteen-bit write through the offset-remap adapter
  used to vanish into a default no-op; a dead dictionary advertised support nothing read; the
  log-contract scanner could be desynced by a verbatim path ending in a backslash; and seventeen
  suites leaked their temp directories (over a hundred thousand orphan folders on this box), all
  now on one shared disposable fixture with zero assertion changes. Fenced honestly: production
  validates a whole read range while the fake checks only the first byte; the honest gate now
  exists behind an opt-in StrictRangeChecks switch, pinned in BOTH directions (a strict
  half-marked-range refusal, and a pin on the default's blind spot), because defaulting it on
  fails 94 existing tests across 21 suites, measured and independently reproduced; closing that
  for real is LW-151 and rides the LW-149 helper extraction. (Tech: FakeSparseMemory ranges +
  MarkReadable/MarkWritable; OffsetRemapMem W16 forward; SkipStringLiteral verbatim semantics in
  LogContractTests with empty-scan guards; TempDirs.cs fixture; 14 new tests, suite 2907, zero
  production files touched; adversarial verify SHIP 8/10, findings folded in.)
- [LW-148] SHIPPED 357634d 2026-07-29: the pipeline's own safety gates can no longer pass falsely.
  A rename whose target row is missing fails the bake red instead of silently shipping vanilla
  text, and the built table is verified cell by cell against pristine before deploy; a log the
  scanner cannot parse reads INCONCLUSIVE, never CLEAN, and the scanner's own 41 regression cases
  now run as a build gate on every deploy; an emptied item table deletes its stale file with a
  loud unshippable-state warning instead of deploying last run's copy; the mods folder rule lives
  in one env-aware place; the flight tape reader refuses unknown flags; a crashed log scanner is
  reported as itself, not as game errors; and the save-dir and kills-slot-width mirror promises
  became real cross-language contract tests. All ten holes were desk-verified twice by the smell
  audit before building. (Tech: patch_names.py changes()==1 guards + verify_against_vanilla via
  audit_nxd_bakes' own audit; the MISSED-intent check with the allowed-cells overlap guard, false
  red proven then fixed; scan_logs tripwire + selftest in Invoke-TablePipeline;
  Resolve-SaveDir in pipeline.ps1 pinned by SaveDirMirrorTests; KillsSlotWidthContractTests;
  manifest regex anchored with comment-tail stripping. Adversarial verify SHIP 8/10; suite 2893;
  real bake reproduced byte-identical.)
- [LW-145] SHIPPED 4f681fb 2026-07-28: six small proven bugs in the code that writes into the
  running game are fixed as one batch, none ever observed live, all found by the same day's smell
  audit and confirmed by adversarial review. Plainly: a guarded bit write could zero its seven
  neighbor bits if its pre-read failed at exactly the wrong moment; five places wrote a number one
  byte at a time, so another thread could glimpse a half-written value; a katana's reaction
  suppression could mistake a failed read for "no reaction" and later erase a real one; two
  modules answered the wrong is-a-battle-on-screen question through their shared interface; the
  Sunderer's ground restore now proves it still owns a tile before writing it back; and seven
  copies of the same sanity check agree on one bound where one had drifted. Also rides: the test
  fake now applies two-byte and multi-byte writes so read-backs and no-write assertions see them,
  the first slice of LW-147. One honest exception to behavior-identical, verified unreachable in
  practice: an enemy whose max HP reads exactly 2000 was processed by Plague's walk before and is
  skipped now. (Tech: built TDD on branch fix/lw145-write-layer-correctness, 17 new tests, suite
  2890; adversarial verify SHIP 8/10 with three sabotage breaks each going red; merged to main
  2026-07-28 with this exit riding the merge commit.)
- [LW-134] SHIPPED 18211c3 2026-07-28: a test build can no longer silently stomp a real player's
  kill counts. The guard that refuses such deploys had gone blind: it looked for the player's tally
  next to the mod, but the save files moved into the Reloaded User\Mods directory when save scoping
  shipped (LW-51), so it found nothing and waved every test build through; on 2026-07-25 it nearly
  let one overwrite a production install carrying a live tally, and only a human noticed. The guard
  now checks the real save directory, the running mod stamps its own compiled flavour there at
  every launch so the guard can also trust what last RAN, and the decision fails closed: production
  evidence from any source, or player data with no flavour evidence at all, refuses a plain dev
  deploy without -Force. Verified offline both ways against a throwaway Reloaded tree with the real
  install untouched: the exact near-miss shape is refused before any pipeline stage runs, and
  -Force proceeds through a complete deploy. One rider stays open by design: the stamp file appears
  in the real save directory only after the next real game launch, a one-glance check that rides
  any future session and whose absence merely leaves the guard on its marker-and-tally behaviour.
  (Tech: Resolve-DeployedFlavor in tools/pipeline.ps1, precedence marker prod, stamp prod, marker
  dev, stamp dev, tally presence; probes kills.json in BOTH the save dir and the legacy mod dir;
  stamp = run_flavor.txt via FlavorStamp.cs from the compiled Tuning.BuildFlavor, fail-soft, one
  call in Engine; 13 new tests, DeployGuardTests executing the real PowerShell function, the
  incident-shape test proven non-vacuous by sabotage and byte-exact restore; suite 2865 green;
  adversarial verify SHIP 9/10.)
- [LW-123] SHIPPED 3565363 2026-07-28: the Defender's shout is finished and played. A player holding
  a grown Defender points at one enemy, and until that enemy has taken its turn, the enemies who act
  cannot see anyone on your side except the bearer, who carries the best parry in the game to
  survive what it just invited. The mark comes off when the shout ends, so the same enemy can be
  shouted at again, and the whole thing runs inside the mod with no helper script anywhere near it.
  The owner ran the acceptance pass in docs/PROVOKE_AC.md and signed it off 2026-07-28, the retest
  both 2026-07-27 battles earned: units actually hidden (LW-135's failure) AND the hold still up
  when the goaded enemy took its turn (LW-138's). The arc bought its polish with three live-found
  bugs along the way, each already exited on its own row (LW-135, LW-138, LW-131), plus the
  single-enemy rule (LW-127). (Tech: trigger = JobCommand injection of ability 189 plus two guarded
  idempotent table writes, the authored inflict row 29 at 0x14080FC4E and the InflictStatus byte at
  0x14078C1AF in the LIVE action table at 0x14078B2DC, never the decoy at 0x14078961C; hold = the
  composed Invisible bit, band +0x47 bit 0x10, raised in the run-up read from CT and Speed. Shipped
  by 3565363, re-armed after the 2.3.2 disarm by 43de63e. The Provoke LIVE_LEDGER rows still marked
  Uncertain await the owner's PROVEN flips separately; this exit does not flip them.)
- [LW-130] SHIPPED a067b20 2026-07-28: shouting at your own teammate no longer brands them Provoked
  for the rest of the battle. The mark never expires on its own, so the runtime scrubs it off any
  player-side seat found wearing it, the bearer included, on every live tick, and a later shout at
  that teammate lands instead of being refused at 0 percent. Closed by the owner's 2026-07-28
  sign-off on the docs/PROVOKE_AC.md pass, whose watch list carries this check as criterion 3c
  alongside LW-123. Whether a
  friendly mark could reach a save in the tick before the scrub runs stays an open, unmeasured
  question, recorded in the criterion rather than papered over. (Tech:
  ProvokeHold.ScrubPlayerSideMarks, independent of the hold's own Idle or Armed state; mask-scoped
  ClearMark on both status layers, composed +0x45 and inflicted +0x1D3; covered by
  LivingWeapon.Tests\ProvokeHoldTests.cs.)
- [LW-136] SHIPPED 19ba0d8 2026-07-28: fielding TWO Defenders no longer leaves a shouted-at enemy
  permanently unshoutable. With two deployed, the hold cannot tell which is the bearer and refuses
  to arm, so the mark used to land with nothing there to ever take it off; it is now scrubbed off
  within a moment, so the party can be sorted out and the shout used again on that same enemy.
  Exits on the owner's blanket 2026-07-28 Provoke sign-off, and the provenance is stated honestly:
  the registered pass fields exactly ONE Defender, so the two-Defender battle was not a step of it
  and has not been separately played. The bug was found by desk reading, never observed in play,
  and the scrub is pinned by tests, so the sign-off retires the ticket rather than claiming that
  battle happened. (Tech: ProvokeHold.ScrubUnarmableMark, debounced on
  Tuning.ProvokeMarkedMissTicks so a bearer read that misses for one tick cannot eat a mark the
  next tick would have armed on; docs/PROVOKE_AC.md criterion 3d; fixed in 19ba0d8, covered by
  three tests in LivingWeapon.Tests\ProvokeHoldTests.cs.)
- [LW-144] SHIPPED d4c3744 2026-07-28: the Sunderer learned Bulwark, owner live-passed the same
  morning. A knight holding a grown Sunderer who waits out a whole turn without moving or acting
  plants the blade, and the single tile directly behind the knight becomes ground nobody may step
  on, shown with the game's own red no entry cursor, until the knight's next turn opens, the
  knight falls, or the battle ends. Denying that tile denies the back attack bonus, so standing
  guard is a real defense now. The owner redesigned it mid arc from a four tile ring to the one
  back tile, "the anti Provoke": the ring locked the wielder in place, the back tile keeps him
  mobile and starves nothing. Two corrections were bought live on the way in: the first build
  raised terrain HEIGHT, which is not a wall at all (a raised tile stays walkable and stepping
  onto it softlocks), and the first shipped facing math had north and south swapped, caught by
  the owner's bait step when a north facing plant barred the tile in front. The ground always
  settles back exactly: grid writes are proven to outlive battles, so every ending restores the
  saved bytes, including battle exit, an inversion of the old never write on the battle edge
  rule. (Tech: byte +6 bit 0x02 at grid base 0x140D8DCB0, the engine's own obstacle state, trees
  read 0x22; facing from band +0x35 low 2 bits, north looks toward rising y; trigger is the
  Mushin turn flag falling edge; Bulwark.cs, Bulwark.Terrain.cs, Bulwark.Policy.cs, 50 tests
  inside the 2852 green suite, adversarial verify SHIP 8/10 plus two fix rounds.)
- [LW-141] SHIPPED d4c3744 2026-07-28: the terrain grid hunt that began as a height hack ended as
  a proven walkability lever and shipped inside Bulwark (LW-144). The grid the game consults
  lives at one fixed spot, eight bytes per tile, and setting one obstacle bit, the same state the
  map's own trees carry, makes a tile impassable for every unit on both sides while it stays
  hoverable with the game's own red no entry cursor. Three corrections along the way, each owner
  witnessed: the base first recorded was two records too high so every write landed two tiles
  east of target, the height byte is not a wall and stepping onto a raised tile softlocks, and
  grid writes persist for the whole game process so an unrestored write is a crash waiting to
  happen. (Tech: base 0x140D8DCB0, idx = x + y*mapWidth + layerBit*0x100, byte +6 bit 0x02;
  LIVE_LEDGER terrain entries 2026-07-28. Still open, recorded in the ledger: the f0 flag bits
  and the slope field semantics.)
- [LW-127] SHIPPED f9549be 2026-07-27: the Defender's shout (helper commit 43573e9) now pulls only the
  enemy it names onto the bearer, instead of every enemy that acted while it was up. The trick is
  fortune telling: the runtime projects each enemy's charge time forward by its speed, the same
  arithmetic the game uses to draw the on screen Combat Timeline, so the party is hidden BEFORE the
  goaded enemy's turn opens, which matters because the AI picks its victim on the very first tick
  of its turn. Owner verified live the same day, two clean casts plus a three cast battle on tape.
  The rule was wrong twice on the way and each failure was measured off the owner's own tapes with
  millisecond stamps: revealing during the player's turns lost the race by one turn handoff, and
  the enemy's own turn opening fooled the predictor because charge time is paid at turn start, so
  the mod uncovered the party inside the AI's commit window. The shipped rule hides by default,
  earns every reveal, and latches from "it is next" until the turn is done. Accepted cost, recorded
  in criterion 19: the enemy acting immediately before the goaded one is also redirected, normally
  one extra, because the two hide windows touch. Remaining Provoke work stays in LW-123, LW-130 and
  LW-136; the fast forward observation is parked as LW-143 at the owner's request.
- [LW-129] SHIPPED 8041e38 2026-07-27: hidden units no longer advertise themselves with an
  invisible status icon over their heads. The backlog expected a dynamic memory hunt; the answer
  was one cell in a table this mod already ships, switching the Invisibility status to the display
  category the game never renders. Two iterations, the first failed live and is recorded in the
  bake comment so the wrong lever is not retried. Owner verdict same day: icon nowhere to be found.
- [LW-131] SHIPPED 3bbcc10 2026-07-27: owner live pass the same day confirmed it, the shout releases the moment the
  goaded enemy actually finishes its own turn, instead of dragging on to its safety timer and
  pulling every enemy in that window onto the bearer. The fix stopped trusting a field that follows
  the on screen cursor and asks the per unit turn flags instead; the owner's pass showed the hold
  arming, tracking the enemy through its turn, and releasing 1.6 seconds after it finished, named
  EnemyTurnDone.
- [LW-135] SHIPPED 91f312d 2026-07-27: retested by the owner the same day inside the LW-127
  passes, the hide decision no longer reads the cursor following field that once made the shout
  hide nobody at all (the goblin battle where the tape read "0 units were ever hidden"). Both the
  release gate and the hide now share one cursor free question, and the party demonstrably stays
  hidden through the goaded enemy's turn.
- [LW-138] SHIPPED 05cc5a8 2026-07-27: retested by the owner the same day, the shout no longer
  mistakes its own cast for the enemy's turn. The engine's actor pointer parks on whatever a player
  action targets, so the cast made the hold believe the enemy had already acted and it released 28
  seconds early; only a rise that happens after arming counts now, and the watchdog rose 30 to 90
  seconds because a healthy hold measured 31 seconds of legitimate waiting.
- [LW-118] SHIPPED 1122e26 2026-07-27: the question was whether the mod can read the game's turn
  order, and the answer is yes, though not the way this row expected. The array it went looking for
  is not there any more: the address the ledger carried, written with a tilde and dated 2026-06-16,
  reads a repeating pattern with zero records matching any live unit, checked twice, once before a
  battle and once mid battle with real numbers on the board. That is the clean negative this row
  asked for and it is recorded rather than buried. The array turned out not to be needed. Every
  unit's charge time and speed are already readable through fields the mod trusts, and projecting
  them forward reproduces the game's own Combat Timeline panel exactly, which the owner verified
  position by position on screen. Measured live: the next unit to act was predicted correctly 15
  times out of 15, across two sessions and a full game restart. Two smaller facts came out of the
  same pass and are worth as much: charge time is paid at the START of a turn, so the unit currently
  acting already reads its post payment value, and a deeper forecast is NOT trustworthy, since two
  runs disagreed about how far it holds, one staying exact for six turns and the other going wrong
  at three. The reorder half of this row's ambition is not opened here, though the lever is already
  proven elsewhere: the same charge time byte is what the Zwill extra turn writes. Instruments:
  tools/probes/provoke_lookahead_probe.py (snapshot, validate, lead time; read only, no write verb)
  and the existing tools/probes/turn_queue_probe.py for the negative. Ledger rows dated 2026-07-27,
  owner flip pending. What consumes this next: LW-127 for the Defender's shout, and LW-139, which
  collects every other place in the runtime that currently guesses at turn state.

- [LW-132] SHIPPED 9959821 2026-07-25: the game updated to 1.5.2, the mod switched itself off to
  protect saves, and this taught it the new build. Almost nothing had actually moved. Every table
  and every piece of battle data the mod reads sits exactly where it sat before; one function slid
  four bytes, and it happened to be the single function the mod attaches to, so that was the only
  address that changed. What is worth remembering is how that was established: by comparing the old
  and new game programs on disk, without launching the game once. Two read-only instruments do it
  and are kept for next time (tools/probes/exe_reanchor_scan.py re-finds baked tables and hooked
  code by content; tools/probes/rip_xref_reanchor.py recovers the addresses of battle data that only
  exists while the game runs, by reading them back out of the game's own instructions in both builds
  and requiring independent references to agree, up to 33 of them for the roster). docs/
  PATCH_REANCHOR.md Step 3 now runs both FIRST, which turns the live session from a hunt into a
  verification pass. Owner live pass 2026-07-25 (01:52 launch, 01:54 battle exit): armed with no
  stand-down, two kills credited with victim identity, clean exit, scan_logs --require-battle
  --flight exit 0 with zero warnings. The moved hook is confirmed by its own canary intercepting the
  session's first prompt and reading real text, which is the direct refutation of the LW-89 failure
  shape. Damage report: docs/research/PORT_1.5.2_OFFSETS.md. One honest caveat recorded there: the
  three equip-card display mirrors rest on offline evidence plus the owner's own eyes on the card,
  not on a log line, because the card paint leaves no trace the scanner reads.
- [LW-133] SHIPPED 6f1e21a 2026-07-25: the Defender's Provoke shout is held back from this release,
  because nobody had played it yet and its acceptance pass had never been run. A compatibility
  release exists to make the mod work on the new game and to carry nothing else. Pulling it took
  three changes rather than one, and the third is the one worth knowing about. The Defender stops
  granting the command, and its equip card stops advertising a command it no longer grants. But the
  part of the mod that hides your party was written to watch for the mark left on an enemy rather
  than to ask whether the feature is switched on, and a Defender reaches its top tier on kills alone
  regardless of that data. So the first two changes would have left it awake, able to hide a
  player's whole party if anything else in the game ever set that same mark, and whether the game
  ever does is an open question nobody has answered. It is now switched off outright, ahead of every
  write. (Tech: items.json id 33 signature block removed plus a patch_names.py rebake of
  item.en.nxd; Tuning.ProvokeEnabled gates ProvokeHold.Tick and ResetBattle; the ability 189 and
  UIStatusEffect Key 1 text bakes deliberately stay, both rows being unreachable in vanilla.
  Re-arming is a deliberate three-part edit, documented on Tuning.ProvokeEnabled, and a test pins
  the switch off so it cannot ride along on an unrelated change.) LW-123 stays open and BUILDING:
  nothing about the feature was reverted, only gated. Owner live pass 2026-07-25: no Provoke entry
  in a Knight's command list, and zero provoke or hide lines in the whole session log.

## 2.3.0 cycle

- [LW-109] SHIPPED 3dccbf7 2026-07-23: a rider could permanently lose the Speed the mod lent them.
  When a timed Speed loan ended, the mod checked that the Speed byte still held the value it had
  lent before writing the original back. If the byte read anything unexpected it correctly wrote
  nothing, but it also forgot the whole arrangement, so the rider's real Speed was never restored
  and nothing would restore it for the rest of that battle. An unexpected reading now keeps the
  note and retries next evaluation, matching how the ordinary bonus path has always behaved. The
  distinction that needed a test to get right: a byte already reading the natural value is a
  SUCCESS with nothing to put back, so it still clears the note; only a genuinely unexplained
  reading retries. The per battle reset is the backstop. Found by desk review during the LW-100
  evidence pass rather than in play, and it is verified by tests rather than by a live pass, since
  contriving an unexpected reading on demand in a real battle is impractical.
- [LW-110] SHIPPED 3dccbf7 2026-07-23: the mounted Speed lane now says what it did, so the next
  question about it can be answered from the log instead of forensics. It used to log only its two
  correction paths, which meant the absence of a line proved nothing, and that is exactly the trap
  the 2026-07-21 live pass fell into: the answer had to be reconstructed from flight tapes. Capture,
  boost, re-apply and revert each write one Debug line now, and each sits inside the branch that
  performs the write, so a line appearing means a write really happened. This was recorded as
  blocking a trustworthy LW-100 retest, and that block is now lifted. The lines themselves get
  exercised the next time that retest runs; the file sink takes Debug unconditionally, so no
  console setting is needed to capture them.
- [LW-115] SHIPPED 4e3272a 2026-07-22: the Stop combo is a complete mechanic, not just a pose.
  Holding a unit's CT byte at zero denies it turns outright: the owner watched a benched unit's
  next turn never arrive and the unit vanish from the on screen turn order, then return normally
  once the hold released, with its accrued charge genuinely lost because CT re-accrues from zero.
  Paired with animation page 0x00, the camera facing freeze from the owner's own sweep, that is a
  full Stop effect assembled from two writes and no new engine work, the first mechanic composed
  entirely out of the toolbag opened this week. Two earlier attempts confirmed only the freeze
  because both sat on an open menu, which stops the clock and makes the turn order question
  unreadable; the deciding run closed the menu. Observation banked as a LIVE_LEDGER row for the
  owner to flip. The CT write side was already proven the other direction (the extra turn slam),
  so this is the same byte read as a denial rather than a gift.
- [LW-113] SHIPPED c855985 2026-07-21: the mod can now play any animation on any unit, and the
  owner proved it live. A register the engine uses to order up animations had been decoded since
  2026-07-10 but never actually poked, because every earlier attempt hit a per frame OUTPUT field
  by mistake and watched it re-stamp. This fired the real INPUT and it passed its pre registered
  bar twice: the first write was consumed before the 100ms sample and froze the unit in the
  requested pose, and the second caught the latch mid act, the request byte still holding at
  250ms and eaten by 500ms. A later real move event re-stamped the pose, which is the engine's
  own overwrite arriving exactly as decoded and doubles as the self heal, so nothing about this
  can strand a unit. Encoding: write u16 logicalId plus 1 to render node +0x10. The owner then
  swept all 128 pages on a time mage and labeled every one (tools/probes/anim_catalog.jsonl):
  teleport vanish, invisibility, the dragoon jump and landing, death and rise from death, the
  level up jump, monk punches, casting poses, a dancer twirl, three hover heights, flinches with
  displacement baked in, and a spin the owner had never seen the game use. Two corrections rode
  along and are banked as loudly as the win: the old decode's page labels were wrong nearly
  everywhere because ids are PER SPRITE CLASS (its "crouch 0x34" is the full death animation),
  and the facing theory for node +0x7C was falsified live within the hour (turning is done by
  pages, and +0x7C's meaning is unknown again). LIVE_LEDGER row flipped to Proven by the owner
  2026-07-21; remaining sprite classes tracked as LW-114. This is the theater layer only: playing
  death does not kill and playing stand does not heal, which is exactly why it is safe to build
  signature moments on.
- [LW-107] SHIPPED fdb476b 2026-07-21: the tree carried 197 sentences saying the game was
  proven to do something, nobody had ever checked them, and 61 of them turned out to be wrong.
  They now say what is true. LW-106 froze the 197 so no new claim could slip in, but freezing is
  not verifying, and this is the checkup it deferred. Method, the one that worked on the session
  notes: read every claim against docs/LIVE_LEDGER.md, the only place allowed to decide what is
  proven, then hand each alleged defect to a second reader whose default was to REFUSE the
  accusation. 128 claims came back fine, 61 did not, and 7 accusations were thrown out by the
  refuters. The 128 split two ways and the distinction is the whole game here: some are real
  mechanism claims backed by a row genuinely sitting in the Proven section, and the rest are work
  item live sign offs, the owner watching a shipped feature behave, which is this repo's normal
  language and never needed a row. Corrections are minimal and consistent: only the false proof
  label changes, never the mechanism description or the evidence, so a sentence that read PROVEN
  LIVE now reads OBSERVED LIVE. Nothing was deleted and no ledger row was touched or flipped,
  which stays owner only. Almost half the defects share ONE root cause: the 2026-07-10 unit
  manipulation night (teleport, spawn, despawn, resurrect, animation, hide) was written up
  everywhere as proven while all five of its ledger rows still sit under Uncertain, each tagged
  "Ready for the PROVEN flip" that nobody performed; docs/MECHANICS.md now says so once, loudly,
  in a LEDGER STATUS note over that block instead of leaving the next reader to infer it. Three
  others were confident and load bearing enough to name: the roster bank span cited as proven by
  a probe tape, the turn flags generalised from "menu is up" to "rises under auto battle" on one
  tape, and the Larceny status buffs called functional and proven with no row behind them. The
  LW-106 baseline drops 192 to 134 in the same commit so the ratchet stays honest. 85 agents,
  every verdict adversarially checked. Suite 2647 green.
- [LW-104] SHIPPED eb02527 2026-07-21: the automated build stopped warning that it runs on
  machinery GitHub is retiring, so the day they switch it off nothing breaks and no release cut
  is blocked at the worst moment. Nothing was broken before this; it was maintenance done while
  it was still cheap. Five actions in .github/workflows/release.yml still targeted Node.js 20
  and the runner was already force upgrading them, so each run ended with the deprecation
  notice: checkout v4 to v7, setup-python v5 to v7, setup-dotnet v4 to v6, setup-node v4 to v7,
  upload-artifact v4 to v7. Versions came from the live release feeds rather than memory, and
  each jump was read for breaking changes against how this workflow actually uses the action:
  checkout v7 blocks fork PR checkouts under pull_request_target and workflow_run (this
  workflow triggers on push, pull_request and workflow_dispatch), setup-python v7 dropped the
  unused pip-install input, setup-node v7 dropped an unused NODE_AUTH_TOKEN export,
  upload-artifact v6 needs runner 2.327.1 or newer (GitHub hosted windows-latest satisfies it)
  and v7 adds an opt-in archive parameter this workflow does not set. Verified end to end
  rather than assumed: the pre bump run carried a warning naming those exact five actions, and
  the post bump run (29878091352) finished green with no annotations at all. Left alone on
  purpose: the node-version '20' input, which is the toolchain auto-changelog runs on rather
  than an action runtime, and softprops/action-gh-release@v2, which the notice never named.
- [LW-106] SHIPPED e616be5 2026-07-21: writing that the game was PROVEN to do something now
  fails the build unless someone deliberately signs off on it. The rule that only the live
  ledger decides what is proven had no machine behind it and got broken nine times before
  anyone noticed (LW-105 found nine such sentences across code, tests and this file, only two
  of them known going in). The check counts the proof claim phrases per file and compares
  against a frozen baseline, so any addition fails and the author has to open the ledger,
  confirm the row really sits in the Proven section, and bump a number on purpose; the failure
  message prints the paste ready row and names the option that is usually right, which is
  rewording the claim rather than bumping the count. The design written into the ticket did not
  survive its own seeding grep: an allow list naming the ledger row behind every claim would
  have been a 197 row hand mapping across 65 files that churns on every reword, so the count
  ratchet replaced it at roughly a fiftieth of the cost while still catching every addition
  (LW-90 added four in one commit). Two limits are stated in the test rather than left
  implicit: the baseline records that those 197 claims EXISTED on this date and does NOT assert
  each traces to a Proven row (auditing them is LW-107), and deleting one claim while adding
  another in the SAME file keeps the count equal and passes, which is the accepted price of not
  freezing line text. Scope covers production code, tests and the top level contract docs;
  excluded are the ledger itself, the JOURNAL and ARCHIVED doc tiers, and the test file, which
  necessarily contains every phrase it hunts. Proven to bite: a planted claim in Plague.cs
  turned it red naming that file, and a byte restore plus forced rebuild turned it green.
  Suite 2647 green.
- [LW-102] SHIPPED 3dc0852 2026-07-21: a work row pasted into the wrong part of docs/TODO.md
  used to stop being checked without anyone noticing; now it fails the build. The contract
  tests read entries only out of Now, Backlog, and the changelog, so a row parked under Walled
  or Format escaped every rule: its id could collide with a live one and its format could be
  broken while the build stayed green. Nothing was actually wrong in the file; the hole was
  that nothing could tell. Check I sweeps an entry-shape regex over every non entry section,
  deliberately looser than the strict per section grammars so a malformed stray is flagged
  rather than excused, and keyed on a real digit id so the Format section's own placeholder
  examples keep passing. Ported from the TreasureMaster sibling's TM-6 (its commit 5569e8e),
  which found the hole as CC-17 in ColorCustomizer. Proven to bite rather than pass vacuously:
  a planted stray under Walled turned this one test red with the other 37 green, and removing
  it turned it green again. Suite 2636 green.
- [LW-105] SHIPPED 4331ba1 2026-07-21: the notes read back at the start of every work session
  had never been fact checked, and the repo itself was repeating one of their false claims;
  both halves are now closed. The notes half came first: all 139 saved notes were checked one
  at a time against this repo, each verdict then attacked by a second reader whose only job was
  to break it, leaving 8 deleted as actively wrong and 131 surviving, most of those reworded to
  state their real certainty, with the index rebuilt to match the surviving files. The repo
  half is the shipping commit: nine sentences across Iai.cs, IaiTests.cs and this file
  announced the LW-90 normalize premise as confirmed in the live game, when its LIVE_LEDGER row
  sits under Uncertain and only the owner promotes a row to Proven. All nine now state what is
  true (observed live 2026-07-21, working theory, row still Uncertain) and no behaviour changed.
  The ticket named two of the nine; a five surface sweep found the other seven, including two
  "in EVERY Iai battle" universals that one session's sample cannot carry and a test comment
  claiming a log line proved the premise. Lessons banked: a note is not corrected until its one
  line summary is, because the summary is the half that loads every session; and a guard proves
  nothing until you check its wiring, since a stray core.hooksPath had silently disabled every
  git hook here, which is also how an innocent "regenerated with" survived in this file long
  enough to block the first commit once the hook came back (fixed with a word boundary, since
  the real attribution string is caught by another token anyway). Suite 2630 green.
- [LW-87] SHIPPED 0b1da09 2026-07-21: the battle Attack row no longer forgets the acting
  unit's weapon when the player views another unit's status with T and backs out; it keeps
  the name for that unit's whole turn. The row had been anchored to the game's condensed
  cursor struct, which follows whichever unit is being LOOKED at rather than the one whose
  turn is open, so the LW-55 gates were handed another unit's dossier and correctly refused
  it, and a refusal composes vanilla. The resolve now rides Band.FlagOwner (the per unit turn
  flag walk that already carries kill credit, LW-63 and LW-94) plus the same roster bridge,
  and the TurnQueue fingerprint walk is deleted from this surface rather than kept as a cross
  check, which would have reintroduced the same failure class. Dropping that read also drops
  its team gate: the roster bridge is the player filter in its place (enemies and guests
  bridge to zero roster rows), which removes two more blackout classes for free, the garbage
  team field on save load entries and the flicker to enemy during action resolution while the
  player's turn is still open. CursorGate.Decide and the CursorRefusal enum are code
  identical (doc comments only), and resolve level refusals now name their stage (NoOwner,
  NameIdZero, BridgeFail) and tape one flight record per stage per battle, closing the
  diagnosability hole that made the original blackout cost a probe session. The opening
  mirror seat theory was disproven before code: the revolving duplicate was benign in every
  sample (frozen at 0,0 with its flag down), zero ambiguous verdicts across 8879 probe ticks.
  Owner live pass 2026-07-21 14:02 to 14:06 on the deployed DEV build: the name held through
  repeated detours on both ally and enemy status views, whole enemy turns stayed plain
  vanilla (the fail closed bait step), and the session logged zero warnings; the two column
  probe counted 581 ticks the old resolve refused and the new one answered, with zero
  regressions the other way. Owner flip 2026-07-21. Rigor trail: a live probe killed the
  planned fix direction before it was built, an adversarial plan review blocked
  implementation until the turn flag hold premise was measured live and forced the refusal
  observability into scope, and an independent verifier sabotaged the code to prove the load
  bearing test bites, restoring byte identically and rebuilding clean. Bounded residual
  banked as LW-103 (a post battle roster versus frozen band disagreement, harmless because
  nothing composes out of battle). Suite 2630 green, analyze exit 0.
- [LW-90] SHIPPED d780d13 2026-07-21: a battle restart no longer bakes a held stat boost in
  as a unit's natural, and the Iai opening Speed boost now truly ends after the wielder's
  first turn (that second half shipped as the follow-up prong, 4ca396d). The mod remembers
  every boost value it writes (NaturalLedger: per unit and stat lane, across battles,
  level-keyed so an earned level-up point is never eaten by a value collision) and refuses
  to adopt its own leftovers at a fresh battle's first sight; and because the game appears
  to re-paint its boosted baseline every turn (the working premise, observed live this day,
  filed UNCERTAIN in LIVE_LEDGER and never promoted to that file's Proven section),
  released holds keep re-correcting their own written values for the rest of the battle.
  All six capture-natural holds are guarded (Iai plus the five growth lanes, which
  compounded multiplicatively across restarts); the backlog's roster-clamp candidate died
  in recon (no mapped roster or raw Speed byte exists). Owner live pass 2026-07-21 11:46:
  Ramza went first against faster enemies while his card read natural 11 throughout, the
  release fired at his turn open, and the corrective caught the boost returning (14 to 11)
  on tape; the restart leg was watched the same morning on the prior build ("restart
  residue corrected at capture", the 11:03 battle), whose correction machinery the prong
  did not touch. Owner flip 2026-07-21. The roster stayed untouched throughout (the owner's
  out-of-battle card read natural 11 after the residue battles). Residuals banked as
  LW-100 (the mounted lane); rigor trail: a 3-critic adversarial plan review killed the
  naive design, an implementation review found the level-key collision, worktree sabotage
  bit exactly the predicted tests three times, and the owner's first live pass caught the
  fresh-battle gap every desk round missed.
- [LW-42] SHIPPED ef747d1 2026-07-21: the mod can no longer believe a battle ended in the
  middle of a long spell cast or enemy turn (which would silently reset its kill
  bookkeeping mid-fight). Two checks still asked the old game version's question: the
  pre-1.5 battle marker 0xFF never appears on 1.5, so the battle-enter sentinel pair and
  the in-battle excuse covering cast-targeting and enemy-turn frames were both silently
  dead, and the morning log caught the consequence live (a false battle-exit during a
  battle intro, 7.6 seconds after the enter). Both checks now read the 1.5 marker
  (Offsets.Slot0InBattleMarker 0x10, tripwire-pinned; red-first TDD, worktree sabotage
  reproduced exactly the predicted red set). Owner live pass 2026-07-21 (09:04 battle on
  the deployed build): zero mid-battle exits across 138 mode flips, credits normal, no
  warnings, and the victory exit proved the stuck-marker guard live (slot0 held 0x10 six
  seconds past battle end, the debounced exit fired on schedule; banked 1edc124). Owner
  signed off 2026-07-21. The greater-than-4s cast stretch and the post-QUIT value stay
  tracked on the LIVE_LEDGER 1.5 slot0 battle-phases row; both fail safe (a wrong premise
  leaves the excuse dead, the old behavior, never wrongly live).
- [LW-97] WONTFIX 2026-07-21: the player seeing Squires able to learn Equip Axes again on
  2.3.0 is intended behavior, not a broken install (owner call, 2026-07-21). 2.3.0
  deliberately stopped suppressing that ability: the off switch lived in the mod's
  JobCommandData.xml, whose whole-row writeback also erased other job mods' changes (the
  LW-77 collision prune), so the file was deleted and the vanilla learnable Equip Axes
  returned with it. It is harmless dead JP under this mod: every axe is reforged into
  another weapon type, so there is nothing left to equip, and the ability's in-game
  description says exactly that (the ability.en.nxd Description cell on key 460, LW-77,
  2a4c325).
- [LW-80] RETRACTED 2026-07-21: the modloader bug report reached its author another way, so
  the plan to file it as a public GitHub issue is withdrawn. The report itself (one mod's
  table file can silently erase another mod's runtime changes because the loader writes back
  every field of a row, not just the edited ones, with dirty-field writeback as the proposed
  fix) was delivered by the owner to the modloader author through direct contact on
  2026-07-21; no public issue URL exists (the repo's issue list was checked the same day).
  The durable technical record of the mechanism lives in docs/DESIGN.md (the whole-row
  writeback section, LW-79) and this file's LW-77 row.
- [LW-91] SHIPPED 10320b2 2026-07-21: battle menus no longer wear the previous unit's
  weapon name or an old kill count. The mod paints weapon names and kill tallies over the
  game's own menu text; when a routine recheck of a painted spot failed even once, the mod
  instantly forgot the spot and spent up to half a minute re-finding it, and during that
  blind window the old paint stayed on screen (the reported wrong-name menus and frozen
  counts; the 2026-07-21 04:35 log caught a battle ending with two such orphaned spots). Now
  a spot that fails its recheck is kept under watch with nothing written while unsure: spots
  that come back (the common case, the log showed the same spot count re-found after every
  blind window) are corrected within about a second, and truly dead spots are dropped after
  a few seconds with zero writes and the replacement search already running. The equip
  card's Kills meter also updates on kills mid-battle now (this hash), instead of waiting
  for a pause-menu visit. (Tech: per-Hit FirstFailMs strike retention in
  AttackCard.RepaintAll, evict at Tuning.AttackCardEvictAfterMs, early census arm on the
  episode edge gated on !_scanning, eviction rearm unconditional, eviction taped on the
  flight "card" record with the label address; restores remain gated on a verified label
  anchor so the lost-buffer zero-writes rule stands (the plan review killed a
  restore-under-failed-label variant as a heap hazard); Display.PaintCountsIfChanged
  mirrors the count-change edge incl. RecomposeChanged and RequestRescan, wired as the else
  of Engine's ShouldPaintCard gate; retention core in 5136f2e. Adversarial verify SHIP 9/10,
  both sabotages bit the predicted tests. Owner live pass 2026-07-21 07:24 battle: renamed
  row, Fists compose, clean turn-change reverts, mid-battle count update, zero stale
  sightings; the stochastic eviction trigger did not fire in that battle, so the retention
  path itself is sabotage-proven logic that fails safe to the old behavior and
  self-documents in normal play via the "retained pending recovery" line and the taped
  eviction addresses.)
- [LW-98] SHIPPED 5136f2e 2026-07-21: a bare-handed unit's Attack menu no longer shows a
  weapon name left over from another unit's turn. Same root and same fix as LW-91: the
  leftover name lived on a painted menu spot the mod had lost track of; keeping lost spots
  under watch means the unarmed menu's own text (Fists for a human, plain vanilla
  otherwise) reaches every spot again. (Tech: rides the LW-91 strike retention; the Fists
  compose and the fail-closed vanilla doctrine are unchanged. Owner live pass 2026-07-21:
  the unarmed human composed Fists correctly right after tracked units' turns.)
- [LW-88] SHIPPED 5136f2e 2026-07-21: the attack-card kill count no longer freezes
  mid-battle. Subsumed by LW-91 (same root): the count was composed live all along; it
  froze only on painted spots the mod had lost, which the strike retention now keeps
  reachable. Kept its own row for the evidence trail (the owner-witnessed 19 held across a
  battle that credited 7 kills live on the 07-14 tapes).
- [LW-96] SHIPPED 18b983f 2026-07-21: soldiers past number 20 on the party list now earn
  kills, growth, and card text like everyone else. The mod only ever read the first 20
  roster rows while the game allows 50, so a full roster's late recruits (usually the story
  characters) silently got nothing: the exact shape of the 2.3.0 player report "only Ramza
  and generics benefit". The window now covers all 50 rows and deliberately stops there,
  because the rows past 50 hold stale guest copies of real units that would confuse unit
  matching (a cloned Beowulf with identical stats sat there on the owner's save). (Tech:
  Offsets.RosterSlots 20 to 50; bank observed live by tools/probes/roster_span_probe.py;
  boundary tests pin slot 49 seen, slot 50 never scanned, plus a constant tripwire; built
  via build-lite, adversarial verify SHIP 9/10 with both sabotages biting the predicted
  tests. Owner verified live 2026-07-21: a roster slot 46 unit's Sanguine Sword kill
  credited on tape flight_20260721_044145 while veterans credited normally.)
- [LW-99] WONTFIX 2026-07-21: a player reported Nagrarok missing from Beowulf, "turned into
  another sword". Not a bug: this mod deliberately turned that sword into a new one
  (Lightbringer, the sword line's only Holy blade), so Beowulf's famous frog-sword really is
  a different weapon now; the player just could not know. (Tech: item id 31, data/items.json,
  "Repurposed from the retired Living Blade base".) The real gap is communication: renames
  are invisible to players, so "my item vanished" reports will recur; the candidate follow-up
  if they do is a rename table in the Nexus description.
- [LW-60] SHIPPED 19000b1 2026-07-16: the 2.3.0 pre-ship smoke pass, authored 2026-07-11 and
  run to completion by the owner across 2026-07-14 to 2026-07-16. Every row resolved: the pass
  caught four live regressions and shipped their fixes before release (toast delivery, the
  Plague drift hold, the turn-credit lane, the Eagle Eye scope, each exited above), flipped
  the pending LIVE_LEDGER rows to PROVEN on owner evidence, and closed with scan_logs
  --require-battle --flight exit 0 on the final session, the real saves restored over the dev
  lane, the PROD 2.3.0 deploy verified, and the release package cut and content-verified
  (FFTLivingWeapons-2.3.0.zip). The one deliberately open item rides the handoff action pack:
  the backlog LW-80 upstream-issue filing, an owner-account action.
- [LW-95] SHIPPED 8c67ca5 2026-07-16: Eagle Eye no longer hastens Dooms it did not inflict.
  Smoke row 7.10 caught it live the same night (a Mortal Coil Doom proc snapped 3 to 1, tape
  flight_20260716_001721): the tier-3 aura armed on fielded-at-tier alone and the hasten rule
  never saw the inflictor, while the design (living_weapon_grid.csv id 78) scopes the
  shortening to the bow's own May-inflict-Doom procs. The fix tracks a per-enemy doomed
  baseline and hastens only a rising edge observed while the acting main hand is the
  Eclipsebolt during its acted period (the Larceny/Benediction last-actor lane); an
  unattributed edge logs once and is left alone, and the guarded write-down path is unchanged.
  Built via build-lite on the cited proven rows (Doom bytes, the LastPlayerMainHand latch),
  adversarially verified 9/10 SHIP with both sabotages biting the predicted tests
  (hash-verified restores). Owner live re-tested the same night on the 01:06:57 tape: the
  foreign Doom was left alone with exactly one skip line and the bow's own proc forced to 1.
  Accepted residuals, documented in the class doc: attribution is action-level (a non-wielder
  Doom landing during the wielder's own acted period would attribute falsely, charm-tier
  obscurity), and a mid-battle tier-up over a pre-existing foreign Doom could false-hasten
  once (impossible on DEV builds, which seed every tally past tier).
- [LW-94] SHIPPED 5dd5003 2026-07-16: turn credits now resolve flags-first. TurnTracker rode the
  parked actor pointer, which sits on the action TARGET at a caster's acted edge, so a healer's
  own turns never counted (smoke rows 7.14 and 7.27 caught it live: the Mending wielder's three
  acting turns all credited other fingerprints including the heal target on the 17:10:41 tape,
  and the Swiftedge wielder took zero credits on the 17:47:22 tape, so Renewal's turn-edge aura
  and the Afterimage ramp both starved silently); a mid-battle level-up also re-keyed the
  fingerprint and restarted the count. The fix resolves the acting unit from the per-unit turn
  flags (the LW-63/LW-71 Band.FlagOwner pattern), demotes the pointer to fall-through, and
  settles credits into the pre-level-up roster key. Built via build-lite, adversarially
  verified 9/10 SHIP with both sabotages biting as predicted. Owner live re-tested 2026-07-16
  across three post-fix battles: every credit on the 23:57:49, 00:01:35, and 00:06:55 tapes
  rode src=turn-flags, the Mending wielder accrued its own counts with Renewal mending at its
  exact turn ends (row 7.14), the Afterimage ramp stepped +1 per acted turn to its cap (row
  7.27), and Larceny's steal landed with its counter lane advancing (row 7.11). The backlog
  LW-7 auto-battle collapse is this same counting lane and stays open, un-retested under
  auto-battle.
- [LW-92] SHIPPED d75b39f 2026-07-14: the Plague hold now survives mid-battle level drift. Smoke
  row 7.5 failed live (the latch dropped when the victim leveled 95/449 to 96/453 while the pin
  defeated three cures on tape, so the loss was identity-only), and the same-day fix replaced
  the exact-match victim fingerprint with drift-tolerant identity (exact orig brave/faith,
  Band.LevelMatchesRoster up-only level drift, bounded maxHp growth) re-anchored on every
  accepted step; the first adversarial verify caught the re-anchor as test-vacuous and the fix
  round pinned it with a two-step drift-chain test before the 9/10 SHIP. Owner live-verified
  22:10:51 on the identical drift shape: the re-anchor line printed exactly once, the hold
  survived the level-up, and a cure was still defeated after it. Friendly-fire poison stays
  vanilla and curable by design.
- [LW-89] SHIPPED be7e989 2026-07-14: tier-up toast delivery restored (dead the whole post-1.5.1
  era). Smoke row 7.25's positive control caught it: the Chaos Blade tier-2 toast enqueued at
  the exact 25th-kill credit with zero deliver records in any retained tape. Diagnosis in three
  builds the same day: a bounded prompt-head sampler (ed3ce16) showed the hooked entry's rdx was
  garbage; a struct sampler (22611a4) plus live disassembly proved the 1.5.1 re-anchor had
  landed on a dispatch wrapper whose rdx is a flag byte, with the text resolved from a string
  object at holder+0x20 and every branch converging on the true setter at 0x1403F1098 with the
  resolved char* in rdx; the fix (be7e989) re-anchors the hook there, landmark-guarded, with
  the proven pre-1.5 swap semantics and the unchanged "Select a facing" prefix (the 1.5.1
  wording "Select a facing direction and press F to confirm." still matches). Owner
  live-verified 15:55:36: kill number 5 credited to Kiku-ichimonji at 15:55:34 and the banner
  "Kiku-ichimonji has gained its 5th kill and has grown to Kiku-ichimonji+" delivered on the
  facing prompt two seconds later, the first banner since the game patched. The samplers stay
  shipped as permanent bounded observability.
- [LW-77] SHIPPED 2a4c325 2026-07-14: the job-mod collision surface prune. The loader applies
  every listed table-XML row as a whole-row writeback at OnAllModsLoaded (proven live by the
  owner's row-57 ladder with Blue And Red Mages 2.0.2; LIVE_LEDGER Proven row added), so
  JobData.xml now lists only the 28 live-payload rows (contract-test pinned) and
  JobCommandData.xml is deleted outright, its dead-JP Equip Axes protection replaced by one
  ability.en.nxd Description cell on key 460. Owner live-verified 2026-07-14 (smoke row 7.29):
  Red and Blue Mage compose intact on the pruned PROD deploy with zero hand edits, Archer keeps
  its widened Gun access, and the reforged-note description renders in the learn screen
  (screenshot). The Nexus riders (known-issues pin, Old Files supersede, the delete-old-folder
  upgrade note) travel with the ship notes as owner-at-ship work.
- [LW-57] SHIPPED 9d347c9 2026-07-14: the Attack command's first-open readiness after a session
  load. Cause was census cold-start latency, not actor resolution: the sweep could arm and never
  complete across a whole battle while starving RepaintDriver; the fix alternates repaint/scan
  ticks and re-arms aborted sweeps with hit preservation. Owner live-verified 2026-07-14 (smoke
  row 5.3): the weapon name rendered on the very first turn of the session's first battle, and a
  mid-battle weapon swap re-resolved; the LIVE_LEDGER Attack-row rename row flipped PROVEN on
  the same evidence.
- [LW-86] SHIPPED fe30e1f 2026-07-14: the production Scholar's Ring auto-grant killed (finding
  F5 from the LW-10 recon). ScholarRing.Grant compiles to a no-op outside LWDEV (the Tuning
  compile-out pattern), so shipped builds never write items into player saves for the disarmed,
  removal-slated Treasure Master module; dev builds keep the disarm-oracle convenience. Owner
  live-verified 2026-07-14 (smoke row 7.22): ring equipped on a deployed unit produced no marks,
  the disarm line logged, zero grant lines all session, id 260 inventory untouched.
- [LW-69] SHIPPED 9ae454f 2026-07-14: the census-evict log flood silenced. The two attack-card
  "evicting the cached copy" DBG lines were 98.4% of the 2026-07-11 session log (9,024 lines in
  2.5s), caused by per-candidate census rejection logging, not evict thrash; SyncHit/SyncHitEnc2
  return a SyncOutcome and rejections aggregate into the census-finished line's rejected count.
  Owner live-verified 2026-07-14 on the first PROD session (smoke row 4.4): census-finished line
  carried "8191 candidates rejected", zero evicting lines, 224-line session log with no
  dominating line class.
- [LW-75] SHIPPED f91e0d2 2026-07-14: the demoted coverage line now reaches the console once on
  the armed rise. Owner live-verified 2026-07-14 on the first PROD session (smoke row 4.3): the
  battle-1 "All 4 enemies are accounted for" line went out at INFO tier exactly once and matched
  the field, while the no-tracked-weapon battle's line correctly stayed file-only (the designed
  demotion).
- [LW-79] SHIPPED 2a4c325 2026-07-14: the stale DESIGN.md compose claim. Section 3 claimed clean
  compose with Blue/Red Mages ("no interaction", written 2026-05-30, two days before JobData.xml
  existed); three player reports and the loader's whole-row writeback (pinned from source, proven
  live by the 2026-07-14 owner ladder) contradicted it. The claim now states the mechanism and
  that jobs-mod compose holds only since the LW-77 minimal-table prune, which shipped in the same
  commit (2a4c325); LW-77 itself stays in Now awaiting its smoke row 7.29 compose re-check.
- [LW-78] SHIPPED b9777d6 2026-07-14: the stale-nxd re-diff and rebase. The loader applies an
  nxd override per-cell against the RUNNING game's vanilla, so the pre-1.5 full-table bakes
  silently reverted every text cell the 1.5.x patches changed: 111 cells measured (61 ability,
  the whole 1.5.x ability-text delta including the Mighty Guard to Thunder Breath dragon fix;
  50 item: the shield and armor menu re-sorts, the Leather Helm hat-to-helmet
  recategorization, the deleted Moonblade dupe row 254 resurrected). Premise proven live by
  the owner BEFORE the rebake: the stale Padded Coif equip card read Hat where 1.5.1 vanilla
  reads Helmet (shop screenshot 2026-07-14). Shipped as tools/audit_nxd_bakes.py (91b230b: an
  intent-classified audit against a fresh pac extract, red on any UNINTENDED or DRIFT cell,
  reruns after every future game patch; now cited in PATCH_REANCHOR.md) plus
  tools/rebase_nxd_pristine.py and the rebaked bakes (b9777d6): all 111 cells adopt current
  vanilla, designed cells survive byte-for-byte (DRIFT 0), the ability bake is vanilla plus
  exactly the three Barrage cells, and the closure was independently re-derived twice from
  primary sources. Deliberate hand edits now live in tools/lib/bake_intent.py with cited
  reasons (the Sanguine Gauche 1001 badge, Warbrand non-random, three known-good display
  flags on repurposed rows, the cap-break row 261). The one-time working/nxd_th snapshot
  proved to be an old bake of this very mod, not vanilla, so intent is derived from
  items.json, never snapshot-compared. In-game text eyeball folds into the
  SMOKE_TEST_2.3.0.md text rows. Suite 2524 green; analyze exit 0.
- [LW-84] SHIPPED 008dd35 2026-07-14: the ReleaseScopeContractTests gate. docs/RELEASE_SCOPE.md
  and docs/archive/SMOKE_TEST_2.3.0.md go under contract test (the TodoContractTests enforcer pattern):
  an IN box whose cited ids all shipped (none still open) must be ticked; a ticked box in
  either doc citing only still-open work goes red; every tick cites a commit hash or ISO date;
  every cited LW-id must exist in TODO.md or CHANGELOG.md; checkbox lines are shape-checked
  (exactly one well-formed box per line); parser sanity floors keep the rules non-vacuous.
  A "backlog LW-n" citation is a deferral pointer exempt from tick logic (the ticked Murasame
  deferral box stays truthful) and WONTFIX/RETRACTED ids never force a tick; the must-tick rule
  deliberately skips the smoke doc, whose boxes are owner live re-verifications. Landed with
  the one-time annotation pass ticking 14 already-shipped scope boxes with git-verified hashes
  (sections 3-5 and 7-9; section 2 stays for the owner's 8.8 sweep per its own prose), so the
  gate was born green on a truthful file; from now on the commit that ships a scope item ticks
  its box in that same commit and smoke row 8.8 becomes re-verification. Build-lite pipeline:
  red-first drift inventory (4 shipped-but-unticked boxes, 3 provenance-free ticks, 1 merged
  checkbox line), independent verifier SHIP 9/10 with two hash-proven doc-sabotage non-vacuity
  checks. Suite 2524 green; docs-only gate, no runtime surface, live pass skipped by design.
- [LW-82] SHIPPED e77b9d7 2026-07-14: the AnchorScan verifier scout (the v1 slice; merge f701795).
  A dependency-free single-file AnchorScan core (chunked pin-neighborhood signature scan,
  overlap-safe boundary math, alignment-before-Confirm filtering, fail-closed verdicts: found at
  pin / found elsewhere / ambiguous / not found) plus the AnchorScout adapter: after any
  LaunchGuard stand-down the mod re-finds the JobCommand table (rec8/rec9 pair signature,
  file-baked image data, needs no save) and the roster base (nameId shape + 0x258 stride
  structure + %8 alignment; calibrated live 2026-07-14: 11,869 raw hits, 766 shape candidates,
  exactly the pin survives) and logs a re-find inventory plus the inventory-count sibling
  prediction: the starting map for docs/PATCH_REANCHOR.md Phase B. Verifier scout only
  (owner-locked): zero writes, no arming, no self-heal; consumers keep the Offsets pins. Premise
  probe tools/probes/anchorscan_feasibility_probe.py; two LIVE_LEDGER rows await the PROVEN
  flip on the drill evidence. Full /build pipeline: 4-reviewer plan panel, TDD implementer,
  independent verifier SHIP 9/10 with a SHA256-verified sabotage proof and forced rebuild;
  suite 2489 green (+20 tests incl. the source-scan portability contract). Owner live drill
  2026-07-14: drill-tagged title-screen stand-down, jobcommand found at pin pre-save, honest
  roster not-found, post-save upgrade with the sibling line and the summary (2 at pin, 0
  elsewhere, 0 ambiguous, 0 not found), then a marker-free relaunch armed with zero scout lines
  and scan_logs CLEAN. Later tiers exit to backlog LW-85.
- [LW-83] SHIPPED 656a832 2026-07-14: guard stand-down artifacts self-diagnose. Landmark probes
  return a LandmarkReading (verdict plus mismatch detail), so the flight "guard" record and the
  startup Error line carry observed-vs-expected values for every landmark mismatching on the
  deciding tick (PE build key: both u32 fields in hex; byte signatures: both hex windows;
  roster row: the observed nameId/sprite/brave/faith; JobCommand: only the mismatching recs).
  A drill-forced stand-down self-identifies by naming LW_FORCE_FINGERPRINT_MISMATCH in both
  artifacts; production compiles the trigger to const false. The drill gained a marker-file
  lane (DrillTrigger.cs, always compiled): env variables set at launch never reach
  fft_enhanced through this box's launch chain, so a file named after the flag in the mod dir
  also triggers, and BuildLinked's clean step wipes it on every deploy. The stand-down notice
  carries owner-authored copy. FingerprintGuard.cs stays a one-file zero-dependency portable
  core; LaunchGuard split into lifecycle plus Landmarks partials. Owner live pass 2026-07-14:
  drill A stood down with the drill tag and both value pairs in the log line and the standdown
  flight archive (parse_flight verified, message box clean); drill B happy path armed with
  zero drill traces and scan_logs CLEAN exit 0. Build-lite pipeline, independent verifier SHIP
  9/10 with a sabotage non-vacuity proof; suite 2469 green.
- [LW-81] SHIPPED 1289fa1 2026-07-14: the mod is re-anchored for game 1.5.1 (with 0cd2f11, the
  companion data-layer commit; Steam
  buildid 23901820, exe 2026-07-13; the fingerprint guard's first real-world catch fired
  pe-build-key on the owner's first post-patch launch, save untouched). The live layout audit
  (docs/research/PORT_1.5.1_OFFSETS.md, method docs/PATCH_REANCHOR.md, banked as a living
  contract during this arc) found the entire 1.5 address layout survived except TWO movers:
  SubmenuFlag (data, moved -0x52, found by a consistency-sampled 3-state solve) and the
  FnSetTextString prompt-hook entry (code region slid -0x4C, leaving the 1.5 entry a
  mid-function branch target; the detour corrupted the function and crashed the game twice on
  engaging auto-battle). Fixes: SubmenuFlag and the LaunchGuard PE key flipped in one commit
  (0cd2f11); the hook entry corrected to 0x14028F750 and every detour now landmark-guarded
  (HookLandmark.cs, dependency-free portable core; PromptSwapHook.ShouldArm fail-closed,
  sabotage-proven) so a future code shift refuses with one Warn instead of crashing (1289fa1).
  PauseFlag kept its address but narrowed to card-only semantics on 1.5.1. Owner live pass
  2026-07-14: forced-mismatch drill stood down cleanly, then a normal launch armed, the hook
  installed (landmark passed), auto-battle ran crash-free, one battle credited 3 kills with
  victim identity (ArrayBase proven), and scan_logs --require-battle --flight exited 0. The
  toast SWAP payload proof rides SMOKE_TEST_2.3.0.md row 7.25 (dev seeding leaves no tier-up
  to fire); Treasure Master stays auto-disarmed on 1.5.1 pending the LW-10 removal decision.
- [LW-41] SHIPPED 77010b0 2026-07-11: probe sentinel addresses come from Offsets.cs instead of
  hardcoded pre-1.5 copies (sentinel_probe fed garbage sentinels into the LW-40 live incident).
  tools/lib/offsets.py extracts the named constants textually (pure selftest + a shape check of
  the real file, 88 constants); sentinel_probe gained --selftest and address-annotated output;
  the six sibling probes carrying the stale sentinel set (clone, crystal_counter, feign,
  formation_diff, turnteam, roster_loss_trace) now resolve through the helper, and turnteam
  warns loudly that its remaining COND_BASE/ACTED/MENU_CURSOR anchors are still pre-1.5.
  Verified by selftest, address equality with Offsets.cs, compile checks, and a no-game run
  resolving cleanly; the next real probe use doubles as the live sanity read.
- [LW-62] SHIPPED 474d494 2026-07-11: Wielder.Roster.cs's six hand-rolled occupied-slot roster
  walks now ride one shared seam. TryOccupiedSlot centralizes the slot base arithmetic and the
  occupancy filter (level read first, sequencing preserved) so the addressing and the occupancy
  rule cannot drift apart per caller; CollectHands centralizes the sentinel-filtered hands
  collection TryResolve and HasLiveWielder duplicated verbatim (and drops its per-slot temp-array
  allocation). Pure refactor, zero signature or contract changes, no test edits; verified
  equivalent loop by loop against the prior revision, non-vacuity by double sabotage (occupancy
  inversion fails 22 WielderTests; off-hand-append neutering fails the off-hand resolve pin).
  Build-lite verify SHIP 9/10. Suite 2434 green.
- [LW-70] SHIPPED 97549cc 2026-07-11: a dev build's first post-reset kill no longer swallows its
  first-blood toast. The out-of-battle tally clear (LW-51's PlaythroughReset) left BannerToast's
  construction baselines stale, so the first crossing after a new game read as a rollback; the
  constructor's prime is now an explicit Rebaseline (pure snapshot refresh, never enqueues,
  loop-thread-only maps) called on the reset detection edge beside the LW-59 Display.Invalidate.
  Production behavior unchanged in practice (no seeding; the prod curve cannot be jumped in one
  change). Three failing-first tests incl. a kept contrast pin of the pre-fix swallow;
  build-lite verify SHIP 9/10 with a no-op-sabotage non-vacuity proof. Suite 2434 green. The
  in-game dev-smoke observation (first kill after an in-session New Game toasts) folds into the
  next dev smoke.
- [LW-46] SHIPPED a1c643b 2026-07-11: the Galewind card no longer promises "No Lucavi"
  (IsDominatable is allow-everyone by design, owner request 2026-06-18, so the card
  overpromised). Of the two open candidates the reword shipped, the path RELEASE_SCOPE
  section 2 mandated regardless; the gameplay-changing Lucavi carve-out stays unbuilt (the
  items.json note keeps the restore recipe). The "3-turn cooldown" wording is accurate
  (PuppeteerCooldownTurns=4 global turns = the dominate turn plus 3 blocked turns). p3Desc and
  the grid CSV moved in lockstep, item.en.nxd rebaked (old clause absent from the baked bytes,
  new line present), the items.json note shed its stale wielder-clock expiry and CARD/CODE
  MISMATCH claims, and RELEASE_SCOPE section 2's lock-time paragraph was corrected to the
  shipped LW-5 own-turn release. The in-game card eyeball rides SMOKE_TEST_2.3.0.md row 3.2.
  Suite 2431 green, analyze exit 0.
- [LW-22] SHIPPED c7104b9 2026-07-11: the launch header's save lines pluralize their counts (no
  more "1 Marks"; the kills/weapons counts in the same two lines got the same treatment). The
  two lines moved into a pure LaunchHeader composer riding BattleSummary.Plural so five
  failing-first tests pin the singular, plural, and zero forms, and the LOGGING.md launch-header
  example that faithfully showcased the bad grammar reads "1 Mark" now. Suite 2431 green.
- [LW-74] SHIPPED c494faa 2026-07-11: PORT_1.5.md's Appendix E inventory reconciled with the
  post-Offensive-Chemist table set: the grenade ItemData rows 246-252, the removed
  ItemConsumableData.xml, and the ability.en.nxd grenade learn-names 374-378 (all gone since
  a5ea61e) no longer appear as shipped artifacts; a dated note records the reconciliation.
- [LW-34] SHIPPED 91593d0 2026-07-11: the "All N enemies are accounted for" line counts only the
  enemies actually fielded, closing the systematic over-count (owner repro "All 8" in a 4-enemy
  battle). Root cause: encounters define conditional-spawn variant rows whose phantom seats carry
  sane stats, full hp, and real tiles, so EnemyOracle's array capture counted them; only
  scheduler participation discriminates them (tape-evidenced: phantom seats read band CT slam
  +0x25 frozen 0 and turn flag +0x19C never exactly 1, never move, never die). Fix: two additive
  evidence sets fed from the existing ScanCorpses band walk: MarkFielded (slam nonzero or turn
  flag ==1, real position, 3 consecutive ticks) and MarkDead at the dead-edge stamp (a died id
  counts as found without band visibility, the crystallize/chest case); CheckCoverage counts only
  evidenced identities, defers silently on an empty count, and latches only on two consecutive
  checks agreeing on the total (evidence comes from the same band the check reads, so a
  first-pass latch would freeze a partial count). The `_enemyIds` kill-credit gate and the
  CoverageDone/BattleCensus trigger are untouched by construction. Live pass 2026-07-11 (14:24
  battle, owner eyeball): 11 identities captured, 5 excluded as never-scheduled, "All 6" reported
  with 6 visible, zero unseen-enemy warnings; probe data agreed (exactly the 6 counted seats ever
  showed slam movement). LW-75 opened for the pre-existing facelift race that keeps the line off
  the console. Suite 2426 green (17 new EnemyOracleTests).
- [LW-72] SHIPPED ba5e0fc 2026-07-11: the three section-5 doc-and-hygiene leftovers from the
  2026-07-11 release-remainder audit are closed. The README gained a player-facing Language
  support section (non-English players get the full gameplay: rebalance, growth, signatures;
  item text, the equip-card Kills counter, and the in-battle toasts are English-only readouts,
  the toast bullet added after an adversarial review pass flagged the PromptSwap
  English-prompt dependency as a third undisclosed surface). data/items.json id67 Warbrand no
  longer carries the dead spriteIdOverride:1 (VERIFY_LIVE row 3 marks the override DEAD;
  ItemData.xml regenerated with only the SpriteID line gone, analyze exit 0). docs/LOGGING.md
  no longer calls the removed chemist grenades slated for eventual removal (they left the repo
  with the Offensive Chemist removal, a5ea61e; only Treasure Master remains slated, LW-10).
  Owner eyeball of the release note rides the ship gate. Suite 2406 green.
- [LW-71] SHIPPED c2965ce 2026-07-11: the Iai opening-turn Speed hold no longer false-releases
  when the engine actor pointer parks on the struck wielder before its opening turn (the
  ActorPtr-dwell trap: a parked arrival read as the S1 release signal, and the striker's acted
  edge as S2). Every release is corroborated against Band.FlagOwner (the LW-63 per-unit PSX
  turn-flag primitive): the flag owner being the wielder confirms the release regardless of the
  pointer (also closing the old stale-equal starvation corner), a flag owner verifiably naming
  another unit refuses it even when the legacy pointer signal fires, and an indeterminate read
  (the tape-verified zero-t battle-opening record) falls through to the legacy signal unchanged
  so release is never starved; the wall-clock cap stays the backstop. A flags-confirmed release
  restores Speed to the flag owner's entry (the old acting-entry restore would write the
  parked-on unit's Speed byte when the pointer is elsewhere), and the release log line names its
  source (turn flags / actor pointer / cap). Closes the RELEASE_SCOPE section-2 Iai harden box
  and the surviving half of the section-5 falsified pointer-presence deletion. Owner
  live-verified 2026-07-11: the opening-turn release fired "released by the turn flags" with a
  clean session log scan; the struck-pre-turn repro rides the LW-60 smoke pass. Suite 2406
  green.
- [LW-63] SHIPPED be0e4cc 2026-07-11: a kill no longer credits whichever living weapon the engine
  actor pointer happens to be parked on (the 2026-07-10 repro: Ramza killed with the Chaos Blade
  while Wilham's fielded Warbrand claimed it, the pointer parked on the wrong player). All three
  credit sources (the live latch, the death-edge stamp, and the global delayed-culprit arm) now
  key the acted-period resolve on the per-unit PSX turn flags (band +0x19C/D/E): Band.FlagOwner
  walks real-position candidates for exactly one turn-open flag reading 1 and refuses on ambiguity
  (mirror-seat twins stay harmless under the real-position guard); ActorResolver gained
  flags-first preambles (the latch keys on t==1, the stamp on t==1 && a==1, matching the live
  observation that the per-unit a byte lags the global acted edge); KillerStamp gained a
  flags-first hypothesis lane whose bury stamps read UntrackedReason.TurnFlags. The flag bytes
  are not boolean (moved reads raw 3), so every key tests ==1; battle-opening acted edges can
  read all-zero flags, so every low-confidence outcome falls through to the register/turn-queue
  chain unchanged (that fall-through is load-bearing), and the delayed-culprit arm was fixed
  transitively (test-pinned, no code change). Owner observed live 2026-07-11 on two tapes: a
  manual two-unit battle with the pointer parked on an enemy frame for 3.3 minutes credited the
  true killer (latch src=turn-flags), and an auto-battle credited all five kills correctly,
  proving the flags rise under auto-battle (direct LW-7 fuel). Merged 23429c9; suite 2394 green.
  The exit commit removes the temporary TurnTracker.EmitTurnFlags flight tap, its test pin, and
  its LOGGING.md passage.
- [LW-59] SHIPPED fbf59ce 2026-07-11: a stale +N name suffix no longer survives the in-session
  new-game tally reset on the equip card (owner read "Claymore+3" over a provably empty tally
  while the same card's Kills meter correctly read "0/1 to +"). Root cause was a coverage hole,
  not a paint hole: the kills meter has guaranteed total pool coverage (CoversAllMeta refuses
  to retire the sweep until every id has a kills site) but suffix sites were registered only
  for the two mirror targets plus an 8-id SuffixRotation slice whose covered set persists
  across Display.Invalidate, so post-reset pool rescans re-registered almost no suffix sites
  while the painted "+3" bytes persisted in the very pool text the card materializes from (the
  painter was always downgrade-capable: the tier-0 suffix is the baked vanilla two-space
  state). Fix: pool-path OnChunk searches suffixes for every tracked id in chunks that carry
  kills hits (the whole-heap sweep keeps the rotation slice, pinned by test),
  CardSites.MaxSites grew 768 to 2048 so full suffix coverage is never refused at the cap
  (~701 live kills sites pre-fix), and Engine invalidates the display on the new-game
  detection edge (a main-menu New Game fires no battle-exit edge, so it previously kept the
  stale pool text). Full plan-review-implement-verify cycle; non-vacuity by break-and-restore
  (forcing the pool path back to the rotation slice fails the three new coverage tests). Owner
  live-verified 2026-07-11: the post-reset opener card shows the plain name beside a fresh
  meter, coverage re-latched 15s after the reset with no cap refusals and no engine stall
  (4 pool regions), and the suffix climbed again at the first real battle (Vagabond+ then
  Vagabond+2). Suite 2370 green.
- [LW-56] SHIPPED b6b234f 2026-07-10: the new-game opener crediting arc. Fault 1 (the mis-credit:
  a stale identity bridging an in-session new game credited a weapon no fielded unit wields) shipped
  earlier as the forced new-game exit edge plus the no-live-wielder credit gate (a4d6e33). Fault 2
  (credit the scripted Orbonne opener kills) was found STRUCTURALLY UNBRIDGEABLE and accepted as
  uncredited: the opener fields scripted stand-in units whose live identity (canonical nameIds like
  2/23/52, pre-leveled brave/faith, ENTD weapons) diverges from the fresh level-1 roster on every
  dimension, so no nameId, fingerprint, or weapon match can connect them to a roster row (owner
  live-confirmed 2026-07-10, tape flight_20260710_201535). The canonical fingerprint-and-weapon
  rescue built for it (ActorRegister.RescueCanonical) ships anyway as SAFE: it lives strictly
  behind the zero-roster-match gate, so a real recruited unit bridges Player directly and never
  enters it, making the rescue strictly credit-additive (it can add a credit, never suppress or
  redirect a real one). A four-analyst audit confirmed only scripted stand-ins and guests reach the
  rescue, neither of which can hold a player-chosen living weapon; the one new surface is a narrow
  guest weapon-key over-credit, tape-visible and largely blocked by the live-wielder gate.
  Suite 2369 green.
- [LW-68] SHIPPED b6b234f 2026-07-10: a real player kill was silently blocked as a duplicate when
  the victim's maxHp shifted within its life (the 3-tuple swap detector does not track maxHp, so
  the alive-edge belt was stamped under the old maxHp and the death tuple read as an absent entry,
  which the block misreported as "already credited" in a battle that credited nothing; owner live,
  tape flight_20260710_064433). The alive-edge block now splits absent from false: an
  oracle-confirmed, seen-alive enemy whose edge was orphaned by a maxHp shift credits (reason
  orphan-alive-edge), while a genuinely resolved identity still blocks with honest wording
  (reason=identity-already-resolved). The absent-rescue arm is self-contained (it does not consume
  the global delayed-culprit latch, so it cannot steal a charged action's credit) and the shared
  alive-edge is cleared only on an actual credit, so a fully refused credit no longer blocks a
  later same-tuple wielder-backed kill. Full plan-review-implement-verify cycle, non-vacuity by
  break-and-restore. Suite 2369 green.
- [LW-55] SHIPPED e774405 2026-07-10: the in-battle Attack card no longer shows another weapon's
  kill count. Root cause: the cursor resolve named a roster row and read its formation main hand
  with no cross-check against battle truth, so a wrong or stale row (scripted opener loadouts,
  the hover-following turn-queue struct) keyed the shared tally with a different weapon id; the
  observed "Kills: 100" was that other weapon's real count in the then-global kills.json. The
  resolve now returns raw facts (CursorAnswer) and AttackCard applies CursorGate before
  composing: the matched band entry's PSX turn flag must read 1, then the roster main hand must
  agree with the band entry's own equipped weapon, sentinel-normalized; any refusal composes
  vanilla and writes one "card" flight record per key per battle (a weapon mismatch also warns
  once; a not-turn-owner refusal stays at Debug because cursor hover is routine). Both gates are
  narrowing-only: they can turn a composed row into vanilla, never invent a dossier. The PSX
  turn-flag trio moved to Offsets with provenance (3a8bf6d). Owner live-verified 2026-07-10:
  attack and equip cards agree on a manual turn, hover targeting never swaps the dossier, and
  the new-game opener shows the true weapon; the auto-battle premise check stays open (worst
  case is a vanilla card during auto-turns). Suite 2311 green.
- [LW-53] SHIPPED c906d60 2026-07-10: a fingerprint-guard stand-down now leaves a durable
  black-box archive instead of flushing an empty ring (the 2026-07-07 drill observation: every
  tapped subsystem is gated off pre-arm, so the FlushOnce error flush drained nothing).
  LaunchGuard records the guard lifecycle into the flight ring through recorder/requestFlush tap
  delegates: the armed edge records one guard entry (it rides the next battle flush), and
  StandDown records the failing landmark diag then requests a dedicated standdown flush as its
  last step. The dedicated trigger bypasses the error FlushOnce latch, which an earlier unrelated
  error can burn while the ring is still empty (battle-edge flushes never fire pre-arm, so the
  guard record would strand forever), and it names the archive flight_*_standdown.jsonl. No
  game-memory write path is touched; writes stay disarmed through a stand-down. Live-verified
  2026-07-10: the forced mismatch produced the loud line, the OS notice, and a one-record
  standdown archive naming pe-build-key; a clean relaunch armed normally and the battle-start
  flush carried the armed record. Suite 2284 green.
- [LW-4] SHIPPED b8f6741 2026-07-09: Kiku-ichimonji id45 ships Mushin, the one-shot stillness
  charge: a full WAIT turn (no move, no act) arms one PA-boosted hit (PA held at
  round(natural x 2.05) at tier 3, about 1.6x a normal +3 swing), spent on the wielder's next own
  action. The trigger reads the engine's own per-unit turn bookkeeping, mapped from the FFHacktics
  PSX struct and probe-confirmed live the same day (band +0x19C menu-open flag falling edge,
  +0x19D moved, +0x19E acted, both engine-reset at next turn open; tools/probes/
  mushin_wait_probe.py). Four earlier same-day designs (CT state machine, TurnTracker round clock,
  enemy-CT median, action-record-confirmed latch) each failed live on attribution noise and are
  retired; their forensics live in the Mushin.cs provenance doc and the memory ledger. Owner
  live-verified (BANK on a still wait, SPENT on the strike); card text rebaked into item.en.nxd
  (052bb12); suite 2277 green.
- [LW-51] SHIPPED bf351db 2026-07-09: kill-tally scoping and mod-update survival. The save files
  (kills.json, legends.json, gunslinger.json) moved out of the deploy mod dir into the update-safe
  Reloaded User/Mods/[ModId] folder (the directory Config.json already lives in, which a mod
  update never touches) via SaveLocation's one-time copy-only migration of each legacy file (never
  delete, never overwrite, fail-soft). A NEW GAME now resets the tally: PlaythroughReset detects
  the Orbonne opening dialogue held for a sustained tick window (a one-frame EventId dip from a
  Continue load can never trip it), archives the current kills.json, and clears the shared
  KillTally instance in place, so a fresh playthrough no longer starts pre-maxed. Owner waived the
  formal live pass (relocation proven incidentally across a dozen deploys); the reset then proved
  itself on the 2026-07-10 opener tape (kills.2.json archived, the battle's first credit logged as
  kill number 1). A real cold-launch New Game eyeball rides the LW-60 smoke test; Tier-2
  per-save-identity isolation (two ALTERNATING playthroughs still share one tally) is deliberately
  deferred to LW-61.
- [LW-29] SHIPPED bf351db 2026-07-09: the release question is answered by removal: player save
  files no longer live in the mod folder at all, so a Reloaded mod UPDATE (2.2.2 to 2.3.0)
  replacing that folder cannot wipe the tally. The relocation ships with a one-time
  non-destructive migration read of the old location, exactly this entry's ask (mechanism detail
  on the LW-51 row above).
- [LW-37] SHIPPED 7830def 2026-07-08: the equip-card Kills meter is painted by a pool-anchored
  in-place write instead of the whole-heap Display sweep. The card re-materializes its description
  from stable UE string-pool regions, so PoolLocator finds every writable region holding a baked
  entry (a "Kills:" hit with the owner weapon's name adjacent) and PoolPaint writes the live count
  in place through the existing OnChunk/CardSites path, then skips the sweep once every tracked
  weapon is covered. Each write is name-gated, foreign-refused, and Writable-checked (the
  transient render copies are excluded; painting a non-source baked copy is harmless), gated by
  Tuning.PoolPaintEnabled; CardScanner, ChunkReader, and CardSites are reused verbatim. Merged as
  4afce70; live-verified 2026-07-08 in a DEV build, and the 2026-07-10 opener tape shows the
  repaint running post-reset (701 sites). The stale-count question on this surface (a painted 3
  outliving the LW-51 reset) is tracked as LW-59.
- [LW-52] SHIPPED 50ae6b3 2026-07-07: removed the player-facing config toggles. The Reloaded
  launcher now exposes only Treasure Master Always On; BannerToasts, DevSeedKills, and VerboseLog
  were deleted from Config.cs so players cannot switch off designed behavior. Their runtime keeps
  its compiled defaults: toasts always on (Engine falls back to Tuning.BannerToasts), dev-seeding
  governed by the LWDEV compile flag, and the console pinned to Info (the log FILE still records
  every line; a dev raises ModLogger.LogLevel in Mod.cs for Debug on the console). A reflection
  guard (ConfigSurface_IsExactlyTreasureAlwaysOn_LW52) fails if any removed toggle reappears. Owner
  spared TreasureAlwaysOn per direction; live-verified 2026-07-07 (the launcher shows the single
  toggle). Suite 2213 green, both build flavors clean.
- [LW-54] SHIPPED 2d8f2b9 2026-07-07: the verify-time log scanner (tools/scan_logs.py). Reads the
  newest livingweapon.log from the deployed mod folder (resolved like BuildLinked) and exits
  nonzero on runtime trouble: any [ERROR] line, a fingerprint-guard stand-down, or a
  played-a-battle-but-never-armed state; WARN never fails it. Flags: --mod-dir, --flight,
  --require-battle, --allow, --quiet, --selftest (36 self-test cases, the repo idiom since there is
  no pytest). NOT a build gate (the build never runs the game): BuildLinked runs it before each
  deploy as a non-blocking report on the outgoing session's log, captured before the clean wipe and
  printed from the finally block so a dirty session is the last thing on screen; VERIFY_LIVE.md
  documents the manual run as a live-verify session's closing hard-fail gate. Hardened by a
  five-lens adversarial pass (empty --allow no longer blanket-suppresses, --quiet is silent on a
  clean scan, a line-one UTF-8 BOM no longer hides a first-line error).
- [LW-50] SHIPPED 0152cf9 2026-07-07: the startup fingerprint guard. Before any game-memory write
  arms, the runtime verifies three data-only landmarks (the PE build key, the JobCommand table's
  rec 8/rec 9 ability-byte signature gated on a populated roster, and Ramza's roster-row shape at
  RosterBase slot 0) with retry-until-decidable arming and a 30-tick consecutive-mismatch
  debounce. A confirmed mismatch permanently disarms every write path for the session (a volatile
  Mem.WritesEnabled gate inside the WriteBytes/W8/W16 funnel, an Engine tick gate, and a deferred
  lock-protected PromptSwapHook arm handshake), logs one loud stand-down line, and raises a
  once-per-session OS message box (StandDownNotice.cs) with plain-language guidance and the
  support email. FingerprintGuard.cs is the dependency-free portable core (copy the file to adopt
  in sibling mods). The player-facing force-mismatch config knob was removed in 81fcb79; the dev
  drill is the LW_FORCE_FINGERPRINT_MISMATCH environment variable in DEV builds. Live-verified by
  the owner 2026-07-07: a normal launch arms after save load, the forced mismatch stands down
  with zero writes through a full battle, and the box renders exactly once.
- [LW-25] SHIPPED c842ba1 2026-07-07: the DEV-only ShowSpike research instrument still armed its
  commit-tap in dev builds, spamming a "show-spike: commit-tap ..." line on every text commit when
  its F5 window was tripped (owner hit it mid-testing). Its tap mechanism already graduated into the
  shipped PromptSwap (facing-prompt toast delivery, its own independent hook), so ShowSpike was pure
  redundant noise. Unwired it from Engine (field, construction, Arm, Tick); PromptSwap's production
  delivery is untouched. ShowSpike.cs retained unreferenced (still on the LogContractTests dev-spike
  file list).
- [LW-31] SHIPPED 2b2f5b4 2026-07-07: the battle Abilities menu is the weapon funnel. In battle the
  "Attack" command row renames to the acting unit's living weapon (name + trimmed tier suffix, or
  "Fists" for an unarmed human), and its hover card becomes a mini equip card (flavor + the "+3
  ability" prose + the "Kills: N/T to +" tier meter, no Marks). Row text and hover title share one
  string, driven by a JobCommand text-catalog record: the rename never touches the "Attack" label
  bytes (kept as the race-guard anchor), it writes a split image into the desc footprint after the
  label and repoints nameOff/descOff into its two halves, restore is the mirror. A budgeted heap
  census finds the table copies; a three-way anchor (vanilla / current / previous image) leaves
  foreign records untouched. Turn-owner resolve is cursor-only (Offsets.TurnQueue, snaps at turn
  open); any resolve failure restores vanilla (a wrong dossier is worse than none). Delivered
  incrementally through cdfcc60 (dossier painter) and 2b2f5b4 (row rename) plus its already-exited
  sub-ids LW-33/LW-36/LW-38/LW-40/LW-44 (LW-27 retracted). RESIDUAL: fingerprint-twin units
  (identical level+hp+maxHp) fail closed to vanilla by design, carried as backlog LW-39; the
  row-rename LIVE_LEDGER row stays owner-flip-only.
- [LW-2] SHIPPED 10161db 2026-07-07: deploy-and-live-verify pass for the 2026-07-05 shipped batch.
  Rows 10 (desc budget trims), 11 (log facelift, full row-11 protocol), and 12 (Boco unarmed
  stale-latch fix) all verified live by the owner 2026-07-07, closing the release-verify scope. The
  Reliquary Phase 1 rows (6-9: Mark toasts, card story line, undead/Requiem classifier,
  legends.json persistence) are deferred past 2.3.0 and now ride backlog LW-6; VERIFY_LIVE.md keeps
  their revival instructions in a dedicated deferred section.
- [LW-45] SHIPPED c132edd 2026-07-07: equip-card descriptions ran off the bottom of the screen. The
  real constraint is the box's wrapped-LINE count, not char count, so the old 266-char budget was far
  too loose (a third of the catalog passed it yet clipped); living weapons with a +3 signature block
  stacked Kills line + flavor + mechanics + the +3 block past the box height. Fixed by three
  owner-eyeballed levers: compressed the generated mechanics prose across every card ("Deals X
  damage", "May cast Y on hit", "Reaches N tiles"), collapsing the fattest wrapped block and fixing
  the elemental/ranged weapons; trimmed 30 over-long flavor lines (each only as much as needed); and
  trimmed Umbral Rod's +3 prose (content-dense enough that flavor alone could not fit it) in lockstep
  with the grid CSV. DESC_MAX tightened 266 -> 205 as a rough char guard (the true constraint is
  wrapped lines; recalibrate on the card UI). Owner confirmed the cards fit.
- [LW-26] SHIPPED c132edd 2026-07-07: the Outrider Pistol's over-long card, folded into the LW-45
  catalog-wide desc-fit pass (trimmed in the same batch, and the Marks-line margin concern is moot
  now that Marks are release-hidden, LW-35).
- [LW-5] SHIPPED e882799 2026-07-07: Galewind Puppeteer releases the puppet after IT takes its own
  turn, not on the wielder's clock. The shipped wielder-clock rode TurnTracker.Turns(wielderFp),
  which LW-7 collapses onto the wielder, so the puppet released on the next turn after dominate
  regardless of whose it was (premature when the puppet was not the next actor, late when it was
  fast, per the 2026-07-07 tapes). Release now fires when the engine turn-owner queue
  (Offsets.TurnQueue, the struct TurnTracker.TryActiveFingerprint matches) names the puppet across an
  acted rising..falling edge, read directly so it is immune to the LW-7 credit collapse (the CT byte
  and actor pointer both read dead for a human-driven puppet). A GlobalTurns cap backstops the case
  where the queue signal never fires, bounding a puppet to at most N global turns, never to battle
  exit. Live-verified: held through three other units' turns, then released reason=own-turn on its own
  turn, corroborated by the queue, AREC performing stamp, and actor pointer all naming the puppet at
  once. The card's "for its full turn" is now accurate; the unimplemented "No Lucavi" clause spins out
  to LW-46. The recon instrument that cracked the signal was retired; the dominate/release flight taps
  are kept.
- [LW-35] SHIPPED 672e8f4 2026-07-07: release-hide the Marks feature on every card surface (owner
  direction; the display returns with the two-wave Chronicle build, LW-32). The equip-card story
  narration (Display legends:null, 65f7f77) and the attack-card Mark clause (AttackCard
  markLabel=null) were already dark; this closes the last surface by passing null for Reliquary's
  toast, so an earned Mark never enqueues a deed toast even when BannerToasts is enabled. Milestone
  and unlock toasts on the shared BannerToast are unaffected. Collection is untouched: the
  LegendStore still records every deed and Mark (proven inert by
  ReliquaryTests.Disabled_toasts_stay_fully_inert), so re-enabling paints over unbroken history.
- [LW-36] SHIPPED 5bf180d 2026-07-07: reworded every +3 ability card block to the locked grammar
  (header "{Name} (+{tier})", a verb-first "{Verb} {effect}. {Condition}" body for all 25
  signatures within the 90-char budget, job gates moved into the body), and added the
  check_p3_grid_lockstep gate that makes the grid CSV's "+3 ability" column the design source of
  truth and refuses any drift from items.json's p3Desc. The equip-card body meter (part 2) shipped
  earlier in cd6599e; the attack-card tail no longer carries the ability line (superseded by LW-44).
  Owner live-verified the baked cards.
- [LW-44] SHIPPED 8d145bf 2026-07-07: removed the battle Attack card's signature tease ("Unlocks
  {ability}" / "{ability} armed") for now (owner request). ComposeTail composes the Kills meter
  only; the sigLabel/sigEarned params and the caller are retained so re-enabling is a one-line
  revert. Owner live-verified: no tease on the Attack card.
- [LW-40] SHIPPED 08980f2 2026-07-07: re-entering a battle from the world map silently failed to
  register as a battle, so the Attack row (and growth, and kill-tracking) stayed dormant and the
  Abilities menu read the game's vanilla "Attack" (owner repro: leave to the world map, restart the
  battle). Root cause: the 1.5 re-enter presents battleMode=3 with the slot0 marker reading 0x10,
  but EnterSignal gated mode 3 behind the 1.0-era slot0==0xFF. EnterSignal now enters on any live
  battle mode (2/3/4), matching InLiveBattle; battleMode reads 0 on the world map so it cannot
  false-enter. Live-verified by the owner the same day.
- [LW-38] SHIPPED 3bcdadc 2026-07-07: the Attack-row rename missed the battle's first turn
  (owner gripe: the whole-heap census took dozens of ticks per battle, so the first Abilities
  menu open beat the first paint). ResetBattle now keeps the cached table copies warm across
  the battle edge; the next battle's first RepaintAll re-verifies each copy (label bytes plus
  footprint image) and evicts anything stale, re-arming a full census only when the cache is
  empty. Owner live-verified: the weapon loads in place of "Attack" on the first turn of the
  second battle, no rescan wait.
- [LW-27] RETRACTED 2026-07-06: the party-menu equip-card "Kills: N" header, superseded by the body-first-line Kills meter (cd6599e); the count lives in the card body on every surface, so no header stamp is built.
- [LW-33] SHIPPED 18d640d 2026-07-06: the residual footprint-poisoning path in the attack-card
  painter. SyncHit re-pins the footprint to the vanilla 73 chars on every known-line read
  (repairing an already-poisoned cache entry instead of only avoiding fresh poisoning), with a
  test hook proving the repair, and the two overselling test comments were corrected in the
  same commit. Ledger exit recorded late: the fix itself shipped inside 18d640d's round.
- [LW-20] SHIPPED 0bf9d65 2026-07-05: the LoggerTests millisecond-timestamp flake (two rendered
  console lines compared with embedded wall-clock stamps could straddle a boundary and fail a
  clean tree). A pure StripTimestamp helper normalizes both lines; a dedup-key sabotage run
  proved the test still bites.
- [LW-21] SHIPPED 0bf9d65 2026-07-05: TodoContractTests hardening: the changelog scan now
  inspects every top-level list line (a bracketless exit line goes red instead of invisible)
  and the Now-entry title capture excludes asterisks so a rogue second bold marker cannot be
  swallowed.
- [LW-1] SHIPPED 1a157f2 2026-07-05: the unarmed stale-latch bury branch ate armed players'
  kills (Boco/Phoenix Down; two burials taped in one battle the same day). Fixed by consulting
  the KillerStamp register at the empty-latch bury: only a strictly fresher, disagreeing,
  ARMED hypothesis converts the bury into a credit; designed no-credits and closed periods
  stay byte-identical. Owner verified crediting live on the 2026-07-05 deploy.
- [LW-3] SHIPPED 02eff93 2026-07-05: docs three-tier reorg. Living contracts stay at the docs
  top level, closed journals moved to docs/research/, shipped or dead one-shots to
  docs/archive/, every doc stamped with an opening STATUS line, references swept repo-wide
  (code comments, probes, tools, data, gitignore), history preserved via git renames.
  DocsContractTests gates the top-level allow-list, the per-tier stamps, and repo-wide
  doc-link integrity.
- [LW-16] SHIPPED 58d5c7b 2026-07-05: long item descriptions pushed the equip card off the
  screen (Sanguine Sword id 23, owner screenshot). Fixed with the analyze.py total-description
  budget (DESC_MAX=259, live-calibrated) plus three owner-approved prose trims (Sanguine Sword,
  Wrathblade, Stormarc).
- [LW-17] SHIPPED f4bf5df 2026-07-05: stale-latch kill mis-credit under auto-battle AND manual
  play (root-caused from flight archives; the Ember Rod / Claymore mis-credit adjudicated on
  tape). Fixed with the KillerStamp death-edge culprit stamp; live-verified the same day (4
  correct stamp overrides on tape, including the battle-ending Queklain credit under
  auto-battle). The residual turn-count half is tracked as LW-7.
- [LW-18] SHIPPED a3106d0 2026-07-05: BuildLinked deploys wiped the flight/ archives (PowerShell
  Remove-Item with -Exclude filtering is unreliable and erased the auto-battle attribution
  tape). Fixed with the named temp-dir preservation round-trip ($PreservedSaveFiles in
  tools/pipeline.ps1); all three manual verifications passed live.
- [LW-19] RETRACTED 2026-07-05: "battle-ENDING kills vanish" was a false alarm (the suspect
  tape was a manual RETRY of Lionel Gate, not a victory; the completed re-run credited all
  seven deaths cleanly). Kept findings live in LIVE_LEDGER and the Reliquary docs:
  per-encounter canonical boss keys, retry re-earns tally kills, and the Queklain
  battle-ending credit through its cutscene.
