# Icon Verdicts

STATUS: CONTRACT (the verdict corpus behind LW-278; append to it, never rewrite history)

Every recorded time somebody looked at a recoloured icon and said yes or no, and the reason
they gave. Mined 2026-08-21 from 23 shipped work-ledger rows, the recolour engine's own gate
rulings, and the session memory store.

WHY THIS FILE EXISTS. The icon work kept getting rejected, and each new pass rediscovered the
same preferences by guessing, showing pictures, and being sent back. The taste was real but it
was smeared across changelog prose, override tables, and one person's head, so it could not be
read in one sitting and could not be taught to anything. This file is the read-in-one-sitting
version. Two things are built directly on it: the style bible is this corpus written up as
rules, and the judge harness is graded by asking whether it would have made the calls recorded
here. Neither can be built first, because both are made out of this.

HOW TO APPEND. One row per verdict in Part B, newest last, in the same shape as its neighbours:
what was shown, who judged it, what they said, why, and what changed. Never edit a past verdict
to match a later opinion; add the later opinion as its own row and let the pair show the
reversal. A superseded verdict is evidence about how taste moved, and deleting it is how the
corpus would start lying. Part A's taxonomy is derived from Part B, so a genuinely new failure
mode means adding a class there and citing the rows that fund it.

WHAT IS NOT IN HERE, stated so nobody reads silence as coverage:

- `docs/USER_FEEDBACK.md` was checked and holds ZERO icon verdicts. It is entirely gameplay
  feedback (Move creep, early riders, difficulty). Do not go looking there for taste.
- Most in-game sign-offs are recorded as a few words ("look great", "it looks so good in game")
  with no per-item detail. The reasons in Part B are richest for REJECTIONS, which is a real
  sampling bias: we know far more about what fails than about what passes.
- The 150 rows in `data/icon_ramp/treatments.json` are machine-emitted from a census replay, not
  hand-authored. They record WHAT was decided, never WHY. They are not verdicts.
- ~~Round-by-round picker deliberations survive only as their outcomes; the losing variants are
  gone.~~ **WRONG, corrected 2026-08-21.** They survive in full. About thirty published review
  artifacts hold the actual gallery pages the owner judged from, losing candidates included, each
  with its lettered label, its rationale text and its rendered card and list icon embedded as
  base64 PNGs. See "The picker archive" below. This was a real gap in the first draft of this
  file and it mattered: it is the difference between a corpus that records outcomes and one that
  records CHOICES, and only the second can calibrate a judge.

## The picker archive (the calibration set)

Roughly thirty published review artifacts hold the gallery pages the owner actually judged from.
They are the highest-value source in this file, because each one is a CHOICE rather than an
outcome: the losing candidates are still there beside the winner.

Shape of a picker page, confirmed by parsing one: a vanilla reference pair at the top, then a grid
of lettered candidates, each carrying a one-line rationale and its rendered 100px card plus 48px
list icon embedded as base64 PNGs. Aegis Prime offers ten (A to J); the changelog records the
winner as "bright sapphire with gold kept to the edges and gem", which is its candidate C.

How to read them without burning a session's context: fetch the artifact URL once (it saves the
whole page to a local file), then parse that file offline. Candidates come out of
`<section class="v" id="v(\w)">` with the tag and rationale, images out of
`<img class="(art|ico)" src="data:image/png;base64,...">`. Never paste a page into context whole;
they run to 350KB or more.

Pages with a winner recorded in `docs/CHANGELOG.md`, so they are usable as answered questions
today: Aegis Prime (10 candidates, C), Wardstone rim, Chaos Blade round three (9 candidates
across 3 rounds, blood and bone), Ragnarok (the variant siding with its ART over its element),
The Handbag Court (A/B/C, deep mute C), plus the four helmet picker pages, the hats pages, the
head slot rounds, and the glow on/off page for all 150.

**Do not treat a page's own text as the verdict.** The rationale lines are the options as OFFERED,
written before the owner chose. The verdict lives in the changelog row or the memory note that
followed. Confusing the two would train a judge on our own sales pitch rather than on his taste.

## Part A: the failure taxonomy

Twelve classes, each funded by rows in Part B. Ordered by how expensive they were to learn.

**A1. Nonsensical shading.** The deepest one, and the only one a player named unprompted. Every
recolour read site destructures the pixel as `_, s0, v0`, discarding the artist's per-pixel HUE,
then imposes one hue and varies only saturation and value. Real sprite ramps shift hue warm into
the light and cool into shadow, and that shift is much of what makes a highlight read as light
falling on a form. One cause, two complaints: a one-hue ramp IS bland, and brightness kept
without its colour information stops describing a light source. Evidence: B-14.

**A2. Coverage failure.** The tint never reached the art. The colour landed in the soft
see-through haze the artist drew around the sprite rather than on the sprite, so the item shipped
as vanilla under a wash. Measured repeatedly: a median 34 percent of a sword's solid art and as
little as 14; five of nine bows never recoloured at all, two at literally zero; three poles and
two rods at zero. Evidence: B-04, B-06, B-08, B-11, B-12, B-13, B-16, B-21.

**A3. Flat stamp.** One colour smeared over every pixel, erasing the exact feature the item is
named for: the Wardplume's plume, the Zephyr Beret's feather, the Arcanist Cap's painted star.
Evidence: B-01, B-18.

**A4. Wrong engine for the art.** Which mask finds a family's second material is a property of
the ART and it flips between families, sometimes between items. A two-cluster split on line art
finds no second cluster and hands back the vanilla sprite with a wash. Saturation finds the
crossbow's stock but on a sword lands on the blade, where it is DARKNESS that finds guard, grip
and pommel on 15 of 15. Evidence: B-05, B-09, B-17.

**A5. Wrong colour for the item.** The render contradicts the item's own art, name, or element.
The Lightbringer wore a toad green picked for a renamed Toad sword while being the line's only
Holy sword; the Masamune's picture is blue and it wore gold; the Graviton wore ice cyan carrying
no element at all. Evidence: B-09, B-15.

**A6. Twins.** Two items the artist drew with the same picture read as the same object in a list.
The Ravager simply WAS the Defender at 1.0 percent coverage. Applies to deliberate sprite reuse
(7 pairs) and to repeated plain glyphs (15 of 16 byte-identical outlines were unguarded).
Evidence: B-10, B-12, B-19.

**A7. Coloured smoke.** The engines painted every pixel down to the faintest edge, so the
artist's neutral haze took the identity colour and the item looked like it was fuming. Share of a
card's identity colour sitting in the haze rather than on the item: weapons 27.5 percent, hats
12.0, helmets 8.2, shields 4.6. Evidence: B-20, B-21, B-23.

**A8. Speckle.** Per-pixel contrast expansion multiplies the art's own compression grain, and
under a saturated tint that grain reads as blotchy cloud. Evidence: B-22.

**A9. Detail destruction.** The fix for A8 is a blur, and a blur cannot tell a drawn one-pixel
line from compression grain. Applied to engraved metal it smeared the Sunsteel crown's scale
rows and the Timeward's black seams. Right for cloth, wrong for engraving. Evidence: B-22.

**A10. Timid tint.** Saturation too low for the colour to be nameable. Three of six crossbows sat
at 0.15 or below; the owner's word for the family was "dull". Evidence: B-17.

**A11. Metal treatment on cloth.** The punch, specular and contrast recipe that passed on shields
and helmets reads wrong on fabric: highlights and contrast too harsh, blending harsh, colours too
vivid. Cloth wants the artist's own chroma ceiling. Evidence: B-24.

**A12. Name rot.** Three shields were reviewed under names that do not exist in the game, so the
review was about items nobody ships. Nothing in game was ever wrong; the review was. Evidence:
B-03.

## Part B: the verdict log

Format: `B-nn | date | subject | judge | verdict | reason | consequence`. Judge is OWNER unless
stated. Ledger ids in brackets point at the full row in `docs/CHANGELOG.md` or `docs/TODO.md`.

- **B-01** | 2026-08-13 | first full weapon pass, 121 icons [LW-189] | OWNER | PASSED, but only
  as "ACCEPTABLE" in game | the word is the verdict: with the shields setting a higher bar the
  same week, the first-pass weapon tints read as hasty | opened the full-catalogue re-pass
  programme LW-198 through LW-226, one row per equipment section, rather than reopening the row.
  A pass can be accepted and still be judged not good enough, and that is a distinct outcome from
  yes or no.
- **B-02** | 2026-08-13 | 16 shields, two-tone [LW-190] | OWNER | PASSED, "it looks so good in
  game" | settled one by one across TWELVE review rounds including two ten-variant picker pages |
  the bar every later family was measured against.
- **B-03** | 2026-08-13 | shields 140-142 [LW-197] | OWNER | REVIEW INVALID | reviewed under
  invented names ("Ronin Wall", "Conduit", "Bastion") while the real items keep vanilla names |
  lesson banked in the tint table itself: a comment is not a naming source.
- **B-04** | 2026-08-13 | Genji Shield colour call [LW-197] | OWNER | KEEPS its cold steel-blue |
  the item's own DESCRIPTION prose ("pitch-black shield forged from iron") is the deciding
  authority; the set-harmony alternative (crimson, to match Genji Helm/Armor/Gloves) was
  considered and REJECTED against that prose | prose beats set harmony.
- **B-05** | 2026-08-13 | weapons card rule applied to shields [LW-190] | MEASUREMENT | REJECTED
  on evidence | a shield is one convex plate, so colour clustering follows the LIGHTING rather
  than the materials | produced half-painted shields and camouflage speckle. An engine is not
  portable across art shapes.
- **B-06** | 2026-08-14 | 15 swords [LW-199] | OWNER | PASSED after FIVE rounds | shipped as the
  artist's grey sword with a coloured stripe down one edge; colour was landing on the haze, a
  median 34 percent of solid art and as little as 14 | established the vocabulary the five
  families after it reused.
- **B-07** | 2026-08-14 | the no-single-colour rule | OWNER | HARDENED | an audit proved both
  earlier versions cheatable | moved from a config check to a RENDER check with a floor and a
  ceiling. A rule that grades configuration rather than output is not a rule.
- **B-08** | 2026-08-14 | sword second material | MEASUREMENT | REJECTED | body-plus-hilt measured
  as two materials and still LOOKED like one colour | a second material must cross the object's
  LARGEST shape, not merely exist on it. It took the metal running the blade's ridge to read.
- **B-09** | 2026-08-14 | Lightbringer, Graviton [LW-199] | OWNER | WRONG ITEM | the Lightbringer
  wore a toad green picked for a renamed Toad sword while being the line's ONLY Holy sword; the
  Graviton wore ice cyan carrying no element | colour must answer to the item, not to a leftover.
- **B-10** | 2026-08-14 | Ravager, Sunderer, Save the Queen [LW-200] | OWNER | REJECTED | two
  pairs of knight swords shipping as literally the same image; the Ravager reached 1.0 percent of
  its own art, so in the equip list it simply WAS the Defender. Save the Queen shipped bright
  green, which is neither her art nor her name | twins need colour separation as a hard gate.
- **B-11** | 2026-08-14 | Chaos Blade, after 9 candidates across 3 rounds [LW-200] | OWNER |
  blood and bone | settled by a FAMILY argument rather than an item one: all six siblings are
  bright saturated blades, so the dark slot was empty and its colourless art was asking for it |
  the shelf is a legitimate deciding authority when the item alone is silent.
- **B-12** | 2026-08-14 | 9 bows [LW-201] | OWNER | PASSED | five never recoloured at all, two at
  literally zero percent | the STRING is the second material, found by saturation, and it is the
  right SHAPE by B-08's lesson: it crosses the sprite's longest dimension.
- **B-13** | 2026-08-14 | Frostarc [LW-201] | HONEST ODDITY | ACCEPTED | its string is drawn so
  faintly no window finds it, so the mask lands on limb patches, which on an ice bow read as rime
  | right answer, wrong reason, recorded rather than hidden.
- **B-14** | 2026-08-16 | 3-row shield sheet, vanilla / one-hue / two-tone | PLAYER (does
  spritework) | REJECTED, and the owner's summary was "I'm getting a resounding no if users like
  our color rework" | "highlights look rather nonsensicle in the 3rd row, and in the 2nd row
  where they're all just one hue, the shields look bland", plus "may I suggest maybe adhering to
  vanilla's palette?" | THE most important row here. They never said too saturated and never said
  wrong hues; the complaint is VALUE STRUCTURE and SHADING LOGIC. The asymmetry is evidence: the
  one-hue row is bland but not nonsensical, the two-tone row is nonsensical but not bland. Root
  cause A1. Caveat before promising a palette restriction: FFT:IC icons are BC7 TRUECOLOUR, not
  indexed like the PSX art the player is describing, so palette discipline here is self-imposed,
  not a file format we can lean on.
- **B-15** | 2026-08-16 | 11 katanas [LW-204] | OWNER | PASSED | five were painted the wrong
  colour for their own artwork: Masamune's picture is blue and it wore gold, Chirijiraden's is
  amber and it wore blue, Muramasa is the second most strongly coloured sprite in the game and it
  wore violet | six of eleven sit at or above the 0.120 chroma line and KEEP what they have; five
  measure 0.079 to 0.114 and are free under the near-neutral rule.
- **B-16** | 2026-08-14 | guns, poles, spears, rods, staves, harps, veils [LW-203, LW-206,
  LW-207, LW-208, LW-209, LW-211, LW-213] | OWNER | PASSED, signed off 2026-08-16 | each family
  was picked next ON EVIDENCE (measured coverage) rather than queue order; rods were the worst
  left with two at literally zero percent and four under six | coverage measurement is a valid
  way to choose what to work on.
- **B-17** | 2026-08-14 | 6 crossbows [LW-202] | OWNER | REJECTED as "dull", then PASSED, "look
  great" | true twice over: tints timid (three of six at saturation 0.15 or below) AND the engine
  wrong for the art (line art has no second cluster, so the split returned vanilla plus a wash) |
  a per-item opt-in must be able to beat a family default; `engine_for` consults the override
  table BEFORE any category rule precisely for this.
- **B-18** | 2026-08-14 | 12 hats [LW-216] | OWNER | PASSED in game | the flat stamp erased what
  each hat is named for | needed a new engine: a helmet is a body plus one accent, a hat is cloth
  plus a brim or lining plus a crest or painted emblem, and two zones cannot say that.
- **B-19** | 2026-08-14 | Roughspun Cap, Adept's Hood, Martial Band, Assassin's Cowl [LW-216] |
  OWNER | DECIDED OUTRIGHT | owner instruction was to just colour them rather than send another
  round of lettered options | not every item deserves a picker, and being asked for one when the
  answer is obvious is itself a cost.
- **B-20** | 2026-08-14 | Mendsteel Helm, Timeward Helm [LW-215] | OWNER | DECIDED, not lettered |
  both had been left on the old flat tint because their picks never landed, and both old colours
  collided with a sibling anyway | the Timeward is the head slot's one bright metal, "the honest
  answer to a shelf that has no unclaimed colour left on it". An ice blue version was tried and
  dropped because four helmets are already blue.
- **B-21** | 2026-08-14 | the coloured haze, all families [LW-230] | OWNER | REJECTED on the hat
  previews, then asked for already-approved families to be RE-BAKED to carry the fix | every
  recoloured icon looked like it was giving off coloured smoke | shields and helmets shipped;
  weapons deliberately did not, because pulling the smoke off them exposed an older defect
  underneath. The owner's own first-look note was that the fix is small on these two families,
  and the measured shares (4.6 and 8.2 percent) agreed with him.
- **B-22** | 2026-08-14 | smooth-field contrast on 13 helmets [LW-231] | OWNER | REVERTED ON
  SIGHT, same day (42f6c11) | a blur cannot tell a drawn one-pixel line from compression grain,
  and helmet art is engraved metal whose whole subject is that line work | EVERY aggregate
  measurement passed while this happened: blurred difference 8 to 11 of 255, tonal spread down
  under 10 percent, grain swing down 42.3 percent. All true, all structurally blind to one-pixel
  features. For a change about fine detail the picture is the instrument and the summary
  statistic is not.
- **B-23** | 2026-08-19 | the glow rim, all 150 ramp ids [LW-287] | OWNER | REMOVED | looked at
  all 150 pictures with the halo on and off, AT THE SIZE THE GAME REALLY DRAWS THEM, and made the
  call from the pictures rather than from a description | the row started the day parked and
  explicitly out of the release and reversed the same afternoon. Removal turned out to FIX rather
  than cost: our halo had been painted over the artist's own haze, wiping 100 to 270 pixels of it
  per item, so keeping the artist's haze is something the pictures only do now.
- **B-24** | 2026-08-16 | 4 bags, metallic round | OWNER | REJECTED | the shields and helms metal
  treatment reads wrong on cloth: highlights and contrast too harsh, blending harsh, colours too
  vivid. Direction given: MUTED interior, FLASHIER glow | punch is what made them metallic (sat
  floors, hot damp, true-white glint band); cloth wants the artist's own chroma ceiling. Owner
  then picked candidate C, deep mute, from a three-candidate page.

## Part C: standing rules the corpus supports

Rules the owner has stated or that a verdict established, with the row that funds each. These are
the raw material for the style bible; they are NOT yet the bible, because they have not been
organised or checked against each other for contradiction.

1. **Reserved-name rule.** An item that kept its vanilla name must still look like itself. Cited
   in six passes before anything checked it, and now enforced by `icon_preview.py anchors`
   against the RENDERED icon, not the tint, because a recipe can keep an item's colour by moving
   it into a zone. Funded by B-15, and by the gate's own history.
2. **No-single-colour rule.** Render check with a floor and a ceiling, never a config check.
   B-07.
3. **A second material must cross the object's largest shape.** B-08, B-12.
4. **Holy is gold everywhere in this mod** (Excalibur, Lightbringer, and the Perseus Bow ruling
   that keeps gold over a blue bow at chroma 0.120).
5. **The item's own description prose outranks set harmony.** B-04.
6. **The shelf can decide when the item cannot.** B-11, B-20.
7. **Near-neutral art is free.** Below the chroma floor there is no colour to be anchored to; the
   Ivory Pole established it, the katanas used it. B-15.
8. **Judge at real draw size, as a 1x row.** B-23. And for fine-detail changes, additionally
   zoom hard and look. B-22.
9. **Adhere to vanilla's palette**, as self-imposed quantisation discipline. B-14, with the BC7
   caveat recorded there.
10. **Decide rather than lettering options when the answer is obvious.** B-19.

## Part D: open rulings

Live in `recolor_icons.ANCHOR_RULINGS` with a reason each, reported by the gate rather than
blocking it. Carried here so the corpus shows what is unresolved:

- **id 114, Whale Whisker** (LW-238): a red pole at chroma 0.148 shipping cyan. Defence is that
  it is the family's only Water pole. Awaiting the owner.
- **id 36, Ragnarok** (LW-244): warm art at chroma 0.138 rendering lilac under a violet-flame
  fuller, 115 degrees away. The violet was chosen as the dark arriving as fire and was never
  measured against the art. Awaiting the owner.
- **id 142, Venetian Shield** (LW-277): art reads a warm gilt plate at hue 37, the ramp render is
  icy platinum at hue 198, a 161-degree move. Surfaced by the pre-commit anchors run, not
  measured before the ramp arc. Awaiting the owner.
- **id 116, Fallingstar Bag**: RESOLVED by LW-287 without an owner call and deliberately kept as
  a live row, because reviving the glow rim restores the 60-degree gap.
- **The 36 vanilla-named weapons**: whether they read flat without their rim. Flagged 2026-08-21
  rather than buried; the owner sees them at the next gallery.

## Part E: process lessons that protect the corpus

- **Verify over EVERY item, not the family being worked.** 58 weapon icons sat shipping the
  smoking version of themselves because the fix went into the shared shader and their pictures
  were never re-made. Found only by running the check at full scope for the first time. [LW-236]
- **Preview equals production by construction.** `icon_preview.py` imports the engine's `route()`
  rather than copying it, so a gallery cannot show something the bake will not ship. [LW-189]
- **A gate that counts table population is not counting judgement.** Two coverage floors stayed
  green through a rule that graded a layer nobody paints. [LW-287]
- **A comment is not a naming source.** B-03.
- **The picture is the instrument for fine detail; the summary statistic is not.** B-22.
