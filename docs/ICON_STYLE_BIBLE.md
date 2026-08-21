# Icon Style Bible

STATUS: CONTRACT (doctrine for making icon art; owner signature pending, see Part 7)

What to DO when recolouring equipment art, across all three surfaces a weapon's colour lives on
(menu icon, menu art, battle sprite; see Part 0). Its companion `docs/ICON_VERDICTS.md` is the evidence, one
row per recorded verdict; this file is the doctrine derived from it. Every rule here cites the
corpus row that funds it, so a rule can always be traced back to a moment somebody looked at a
picture and said yes or no. A rule with no citation is a rule somebody invented, and it does not
belong here.

READ THIS BEFORE ANY ICON WORK. Then append your round's verdicts to the corpus, and if a round
teaches something this file does not say, change this file in the same commit.

## The thesis, in one sentence

**The vanilla artist's work is the substrate; we recolour it, we do not redraw it.** Every rule
below is a consequence of that sentence. The model is bad at drawing pixels and good at choosing
palettes, parameters and donor art, and at judging pictures, so the system channels it into the
second list and keeps the artist's drawing underneath.

## Part 0: Three surfaces, one colour decision

**Owner directive 2026-08-21: a colour decision is not about the menu icon alone. Change one
surface and the others move with it.** A weapon's colour lives in three places, and a player who
sees a violet sword in the menu and a steel one in battle has been told two different things.

| Surface | Where it lives | Granularity | Refresh |
|---|---|---|---|
| **Menu icons** | `.tex` in `equip_item` (100px card) and `equip_item_s` (48px list) | **per item**, all 234 | live, repaint in place |
| **Menu art** | the g2d container | per entry | read once per launch |
| **Battle sprite** (the weapon a unit swings) | palette block of FFTPack file 71 | **per PALETTE, not per item** | re-read every battle load, no restart needed |

Assumption flagged rather than buried: I have read "icons, art, sprites" as those three. If the
middle row is something else, correct it and this table moves.

### Lockstep is impossible at per-item granularity, and here is the measurement

**127 weapons share 13 battle palettes.** The largest group is 20 weapons spanning 15 categories:
palette 14 carries knives, a gun, a bow, a bag, a staff and a katana together. Change one member's
colour and you change all twenty.

Worse, the groups do not agree about what colour they want. Measuring each palette group's member
`iconTint` hues as a circular mean:

- coherence runs **0.13 to 0.78** across the 13 groups (1.0 would be perfect agreement)
- **13 groups, mean worst-member deviation 137 degrees, 3 of 13 at the 180 ceiling.** Method,
  because a bare 137 is unattackable-looking and therefore untrustworthy: per group take the
  circular mean of member `iconTint` hues, find the member furthest from it, then average those 13
  worst-member deviations. 180 is the MAXIMUM possible circular distance, so the three groups
  sitting there are off the end of the instrument, not merely bad. A mean containing saturated
  values is a FLOOR on the true badness, not an estimate of it, which makes this stronger evidence
  against a per-group compromise colour than the number alone suggests.

So there is no single hue a group can wear that serves its members. This is why the parked weapon
sheet baker turns the sheet violet and pink when it paints each palette with its group's design
hue: at coherence 0.13 a circular mean is not a meaningful summary of anything.

**And we cannot regroup them.** Choosing which palette a weapon uses is WALLED, ledger row
`[weapon-palette-assignment-walled]`: four levers tried, four live negatives, each with an
untouched control. Reopening needs a draw-path hook (LW-291).

### What lockstep can therefore actually mean

`data/items.json` `iconTint` stays the single source of colour truth and drives every surface, but
the surfaces resolve it at different granularity, and the sprite surface needs a stated rule for
what a group wears when its members disagree by up to 180 degrees. **That rule is an owner
decision and is not made here.** The candidates on the table:

1. ~~**Accent zones only** on the six palettes that carry a living weapon.~~ **DEAD, measured
   2026-08-21.** Its whole premise was containment, and there is none: `meta.json` joined to the
   palette map shows 30 weapons carry a signature and they span **12 of the 13 palettes** (only
   palette 9 is clear). The "six palettes" figure appears in LW-289 and in the sibling repo's
   findings and reproduces from neither. Coverage sinks it independently: on the real weapons
   page, slots 1 to 4 are 51.3 percent of ink and slot 15 is 3.6 percent, so "accent only" moves
   almost nothing on a sprite that is on screen only during an attack animation, which is taxonomy
   A2 coverage failure by our own rules.
2. **Dominant member wins**: the group takes the hue of its most prominent weapon and the rest
   ride it. Honest about the collision instead of averaging it away.
3. **Leave battle sprites vanilla** and accept that lockstep covers menu surfaces only, stating
   it as a deliberate scope rather than a gap.

Until that call is made, treat any rule below as governing the MENU surfaces, and treat the
battle sprite as pending. Do not bake the weapon sheet before the tints are final: the bake reads
those tints, so baking early guarantees rework (LW-289, LW-290).

## Part 1: The prime directive, and the largest open debt

**Preserve the artist's per-pixel hue.** Shading must keep the hue RELATIONSHIP the artist drew,
not just the brightness.

Why: real sprite ramps shift hue warm into the light and cool into the shadow, and that shift is
much of what makes a highlight read as light falling on a form. Every recolour read site in the
engine currently destructures the pixel as `_, s0, v0`, throwing the artist's hue away, then
imposes ONE hue and varies only saturation and value. One cause, both of the complaints a real
spriter made: a one-hue ramp IS bland, and brightness kept without its colour information stops
describing a light source. [B-14, taxonomy A1]

**Status: PARTLY IMPLEMENTED. The paragraph above overstated the defect, and the correction was
bought by the sibling ColorCustomizer session reading our live code on 2026-08-21; every claim
below was then re-verified in this repo rather than taken on trust.**

The live RAMP engine, which paints all 150 reviewed items, does NOT discard the artist's hue. Its
chromatic branch takes the source pixel's offset from its cluster's circular mean and adds that to
the target hue (tools/recolor_icons.py:1811-1814): `off = ((hh - cmean + 0.5) % 1.0) - 0.5`,
scaled by `trust = min(1, s/0.35)`, hard-clamped to +/-0.04 turn (14.4 degrees) for ROTATE_ALL ids
and +/-0.08 (28.8 degrees) otherwise. That is ROTATE WITH A CAP. The cap is a real constraint and
nobody has measured it, but the hue is not thrown away. The neutral branch does take hue and
saturation from a 5-step donor ramp, but only on pixels below saturation 0.18, where there is
almost no artist hue left to preserve; that scoping is a deliberate defence and belongs in the
record.

The `_, s0, v0` sites that genuinely do discard hue all sit in engines the router no longer
reaches. Worse for the old story: those dormant engines do not scale saturation multiplicatively
either, they replace it with a per-item constant, so the repair there is not "uncross the axes",
it is that no per-pixel saturation relationship exists to repair.

**And B-14, the verdict funding this entire section, condemns a SUPERSEDED engine.** B-14 is dated
2026-08-16. `RAMP_IDS` does not exist at 4b4a74e (2026-08-16); the ramp engine landed in 02f8629
on 2026-08-17, the day after. Both re-checked here. The shields the spriter called nonsensical and
bland were painted by `shield_two_tone`, and no shield has been painted by that function since.
**So the largest stated debt in this file rests on a complaint about art we no longer produce.**
Nobody has re-rendered that sheet under today's engine and looked. Until someone does, this
section is an open question and not a known defect, and that re-render is the cheapest decisive
experiment in the whole programme: one bake, one look, no agents.

Caveat, so nobody over-promises: the player who diagnosed this also suggested "adhering to
vanilla's palette". FFT:IC icons are BC7 TRUECOLOUR, not indexed like the PSX art they were
describing, so palette discipline here is a self-imposed quantisation choice we would have to
build, not a file format we can lean on. [B-14]

## Part 2: The hard floors

Numbers already enforced in code. They are not style opinions and are not negotiable inside a
normal pass; changing one is its own ledger row with its own gallery round.

| Floor | Value | Means |
|---|---|---|
| `SOLID_TINT_FLOOR` | 0.02 | below this the item ships as vanilla art wearing a coloured glow, which is the A2 coverage failure |
| `ANCHOR_CHROMA` | 0.120 | below this an item's own art has no colour to be anchored to, so it is free |
| `ART_HUE_FLOOR` | 15.0 degrees | minimum separation between two RENDERED icons that share a picture |
| `HALO_LO` / `HALO_HI` | 48 / 224 | the artist's neutral haze band. Never own it; the tint only fully owns genuinely solid pixels |
| `SHIP_GLOW_RIM` | False | we paint no rim. Reviving it restores several anchor violations that removing it closed |
| `DETAIL_GAIN` | 0.30 | how much fine grain survives contrast expansion |

## Part 3: Choosing a colour, in order

Work down this list and stop at the first rule that answers. The order IS the doctrine: it exists
because these authorities genuinely disagree with each other, and without an order every pass
re-argues them.

1. **Does the item keep its vanilla name?** Then it must still look like itself. Judge the
   RENDERED icon against the vanilla icon, never the tint against the vanilla, because a recipe
   can legitimately keep an item's colour by moving it into a zone. [Part C rule 1, the anchors
   gate]
2. **Is its own art below `ANCHOR_CHROMA`?** Then it is free. Near-neutral art has no colour to
   betray. [B-15, the Ivory Pole precedent]
3. **Does the item's own description prose state a colour?** Prose wins, including over set
   harmony. The Genji Shield keeps cold steel-blue on the strength of "pitch-black shield forged
   from iron", and the crimson that would have matched Genji Helm, Armor and Gloves was
   considered and rejected against that prose. [B-04]
4. **Does a mod convention apply?** Holy is gold everywhere (Excalibur, Lightbringer). When the
   convention and the art disagree, **the art keeps the BODY and the convention takes a ZONE**:
   the Masamune is the Holy blade and its picture is blue, so the holy went into a gold fuller;
   the Kiyomori poisons and its picture is cyan, so the venom went into the edge. [B-15] See
   Part 5.3 for the case that contradicts this.
5. **Still silent?** The shelf decides. Fill the empty slot: the Chaos Blade took blood and bone
   because all six siblings are bright saturated blades and the dark slot was open; the Timeward
   Helm took bright metal as "the honest answer to a shelf that has no unclaimed colour left on
   it". [B-11, B-20]
6. **Never inherit.** A colour picked for a name the item no longer carries is not a reason. The
   Lightbringer wore a toad green chosen for a renamed Toad sword while being the line's only
   Holy sword. [B-09]

Two further constraints that apply throughout:

- **Make it nameable, judged on the RENDERED ITEM, never on a saturation floor.** "Dull" is a
  rejection: three of six crossbows sat at saturation 0.15 or below and the family read as the drab
  corner of the list. [B-17, taxonomy A10]

  **RESCOPED 2026-08-21, and this was a contradiction rather than a tidy-up.** As previously
  written this rule penalised low-coverage, low-saturation results in general. Both recorded
  shield winners ARE low-coverage accents: Aegis Prime's "gold kept to the edges and gem",
  Wardstone's "thin white geometric rim over rich purple" [LW-190]. So the bible's stated rule
  and the bible's own exemplars disagreed, and any judge built from this text would disagree with
  the owner's own picks by construction. That is exactly what happened: the blind judge of
  2026-08-21 cited rule 2 AND rule 3 together, calling the owner's actual Aegis pick "a timid
  wash". Patching the composition rule alone (2fbea67) left the other half of the same defect live.

  A global saturation floor cannot tell a deliberate white, silver or steel garment from a timid
  tint, so it measures the wrong thing. Supporting evidence from the sibling ColorCustomizer
  corpus, offered as a falsifier rather than as proof: across 51 human-chosen, owner-approved NPC
  section colours, the median saturation is 0.282, 17 of 51 sit at or below 0.15, and nine are at
  exactly zero. Those are approvals, not rejections. **Their metric and ours are NOT comparable**
  (HLS over stored hex on a 16-colour indexed palette, versus whatever our icon tooling computed
  over BC7 truecolour), so the direction transfers and the threshold does not.

  What survives: an item must read as a colour you can name AT DRAW SIZE. Judge the rendered
  picture. Do not gate on a saturation number, and never infer "timid" from where an accent sits.
- **A comment is not a naming source.** Three shields were reviewed under names that do not exist
  in the game. Read `data/items.json`, which is the naming authority. [B-03]

## Part 4: Choosing an engine, by the shape of the art

**Which mask key finds a family's second material is a property of the ART, and it flips between
families and sometimes between items.** This is the most reliably forgotten rule in the
programme, so it gets a table rather than a sentence.

| Art shape | Key that works | Trap |
|---|---|---|
| Convex plate (shield) | saturation finds the fittings | colour clustering follows the LIGHTING, not the materials. The weapons card rule was tried and produced half-painted shields and camouflage speckle [B-05] |
| Blade plus furniture (sword) | **darkness** finds guard, grip and pommel on 15 of 15 | saturation lands on the blade here, the opposite of the crossbow |
| Line art (crossbow) | saturation, on limb and frame against a bright stock | a two-cluster split finds no second cluster and hands back vanilla plus a wash [B-17] |
| Bow | saturation finds the string at 10 to 24 percent | the sword's furniture key claims under 12 percent and lands on scattered limb tips [B-12] |
| Cloth (hat) | three or more zones, in order, last wins on overlap | two zones cannot say cloth plus brim plus crest, and a flat stamp erases the feature the hat is named for [B-18] |
| Engraved metal (helm) | two zones, body plus one accent | **never** apply smooth-field contrast; a blur cannot tell a drawn one-pixel line from compression grain [B-22] |
| Fabric (bag) | muted, no punch | the shields and helms metal treatment reads harsh on cloth; cloth wants the artist's own chroma ceiling [B-24] |

**A second material must cross the object's LARGEST shape, not merely exist on it. WEAPONS AND
LINE ART ONLY.** Body plus hilt measured as two materials and still looked like one colour until
the metal ran the blade's ridge. [B-08]

**It does NOT hold for shields, and stating it globally was a real defect in the first draft of
this file.** Both shield winners put their accent at the RIM: Aegis Prime landed on bright
sapphire with "gold kept to the edges and gem", Wardstone on "a thin white geometric rim over
rich purple" [LW-190]. The same ledger row records that the weapons card rule was tried on
shields first and rejected on evidence, because a shield is one convex plate whose clustering
follows the lighting rather than the materials [B-05]. So the rule was over-generalised from
swords and bows to everything, in a file whose own Part 4 says the key is a property of the ART.
Caught 2026-08-21 by the judge calibration: a blind judge applied the rule exactly as written,
rejected the owner's actual pick as "a timid wash", and chose a candidate whose gold "genuinely
crosses the largest shape". The rule did the misleading, not the judge. Treat this as the
standing warning against promoting any family's lesson to a universal one.

**A per-item opt-in must be able to beat a family default.** `engine_for` consults the override
table before any category rule precisely so one odd item does not force a family-wide compromise.
[B-17]

## Part 4b: THIS FILE LEAKS ANSWERS. Do not paste it into a judge.

**Verified in this repo 2026-08-21.** The shield counter-example in Part 4 names Aegis Prime and
Wardstone, quotes a winning design verbatim ("gold kept to the edges and gem"), carries the blind
judge's own rejection phrase ("a timid wash"), and describes the losing candidate's identifying
feature. That is an answer key plus a distractor key, sitting in exactly the text a "judge with
the house rules" arm would paste.

The 2026-08-21 two-by-two experiment was designed to hand this file to a judge on two answered
picker rounds. The rules arm would have won by construction and the result would have meant
nothing. It was caught before it ran, by the sibling session reading this file instead of
trusting it, and confirmed here by grep.

**Standing constraint on the whole judge programme: any text in this file that names an item and
its verdict contaminates every future trial on that item.** Shuffling the candidate letters does
not help, because the candidates are identified here by description rather than by position.
Redact before use, or test only on items this file never names.

## Part 5: Contradictions this bible does NOT resolve

Stated openly, because a doctrine that hides its own inconsistencies teaches people to ignore it.
Each of these is a live owner call.

**5.1 Vanilla palette adherence versus nameable colour.** "Adhere to vanilla's palette" [B-14]
and "dull is a rejection" [B-17] pull in opposite directions. Restraint reads as bland, and the
bland complaint and the too-harsh complaint have both been made about our work within two months.
No rule currently says where the line is.

**5.2 Reserved names versus twin separation.** An item that kept its vanilla name must look like
itself [rule 3.1], and two items drawn with the same picture must separate by at least
`ART_HUE_FLOOR` [Part 2]. Two RESERVED items sharing one sprite cannot satisfy both. The
silhouettes gate currently judges exempt pairs on rendered pixels, which is the honest completion
of the exemption, but it does not say which rule yields when they collide.

**5.3 Holy is gold versus the art keeps the body.** Rule 3.4 says the art keeps the body and the
convention takes a zone, funded by the Masamune and the Kiyomori. The Perseus Bow went the other
way: a blue bow at chroma 0.120 KEEPS gold, on the convention. That is exactly on the anchor
floor, so it may be rule 3.2 firing rather than a genuine exception, but it has never been
stated which. Until it is, cite the Masamune for zones and the Perseus for bodies and expect an
argument.

**5.4 The corpus is failure-biased.** It records far more about rejections than approvals,
because sign-offs arrive as three or four words. Any rule inferred from it is better evidenced
on what to avoid than on what to aim for.

## Part 6: How to present work for review

- **Judge at real draw size, as a 1x row.** The glow removal was decided by looking at all 150
  pictures at the size the game actually draws them. [B-23]
- **For any claim about fine detail, render a hard zoom (5x nearest neighbour, light and dark
  ground) and LOOK.** Every aggregate metric passed while the smooth-field fix smeared the
  helmets' engraving, because blur-then-compare and neighbour-swing integrate over exactly the
  scale the defect lives at and cannot see it by construction. Say "no aggregate metric can see
  this" out loud and build a fixture that isolates it. [B-22]
- **Decide rather than lettering options when the answer is obvious.** Being asked for a picker
  you did not need is itself a cost. [B-19]
- **Run the pixel check over EVERY item, not the family being worked.** Fifty-eight icons shipped
  the smoking version of themselves because a shared-shader fix never reached their pictures.
  [corpus Part E, LW-236]
- **Preview must equal production by construction.** `icon_preview.py` imports the engine's
  `route()` rather than copying it, so a gallery cannot show something the bake will not ship.

## Part 7: What is owed before this file is trusted

- **Owner signature.** This bible claims to describe Patrick's taste. Until he has read it and
  said so, it is one reader's inference from the record. That sign-off is half of LW-278's
  Done means.
- **The Part 5 contradictions** need rulings, or an explicit "leave them open" decision.
- **The Part 1 debt** needs the five-lens diagnostic run. Every rule here is provisional under a
  system that still discards hue.
- **Nothing here has been calibrated.** The judge harness is graded against the corpus, not
  against this file; a rule can be well cited and still be wrong about what he would pick next.
- **The Part 0 lockstep rule.** Which of the three candidates governs the battle sprite when a
  palette group's members disagree. Every rule in Parts 1 to 6 was written from menu-icon
  verdicts, because that is the only surface with a review history; none of them has been tested
  against how a colour reads on a swung sprite at battle scale.
