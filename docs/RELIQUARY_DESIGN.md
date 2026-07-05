# The Slayer's Reliquary -- design notes (NORTH STAR, post-release)

STATUS: CONTRACT (Reliquary design; the post-release north star)

Captured 2026-07-04 from a design conversation. This is the "9/10 -> 10/10" bet: the deepest,
wall-free instantiation of the mod's thesis. NOT in the current release scope (see
`docs/RELEASE_SCOPE.md`); it needs probes first. This doc banks the vision + the proven levers so it
does not evaporate. Nothing here is committed to build.

## The one-line idea
**A weapon remembers WHO it killed, not just how many** -- and becomes an unreliable narrator of its
own career. The kill tally stops being a scoreboard and becomes a resume of specific, remembered
deeds only your blade performed. Shaped by the kills: two players' identical weapons diverge into
different characters based on who they were pointed at.

## The story, in three modes (one description line, escalating)
The weapon's flavor line is written by its most-defining deed, priority-ranked, always full:
1. **Legend (rare, permanent, peak):** just landed the killing blow on a NAMED foe ->
   `Demonsbane -- felled Velius, Lucavi of Wrath.` Fires the big center-screen banner.
2. **Title (earned pattern):** no fresh legend, but a strong pattern of use ->
   `the Mage-Slayer -- a caster, or fifty, have died on its edge.`
3. **Last victim (always-fresh fallback):** neither yet -> `Last drank the blood of a Summoner.`
   (Named bosses show a NAME; generics show a CLASS -- which reads better and is cheaper.)

Every weapon always narrates: a plain sidearm shows its last kill, a committed weapon shows its
archetype, a legendary shows its trophies.

## Two tiers of "who"
| | **Legends** (named foes) | **Marks** (species / play-style) |
|---|---|---|
| Trigger | killing blow on a specific named foe (Velius, Gafgarion) | a pattern/threshold (100 goblins, 30 dragons, 50 casters) |
| Feeling | rare, specific, "I remember that fight" | earned identity, "this is a hunter's blade" |
| Spectacle | big center-screen callout banner (stop the game) | quiet toast (existing milestone pipe) |
| Feasibility | needs the named-boss identity probe (harder) | needs victim-CLASS/family classification (easier) -- ship first |

Play-style titles are the weapon **shaped by how you used it**: Mage-Slayer, Wyrmsbane, Beast's-Bane,
Giantfeller, the Headsman, the Kingslayer. Keep it to ~5-6 legible archetypes (twelve titles = none
mean anything). The TITLE is the reward; the count is fine print; ZERO stat bonus (the name carries
the feeling, not a number).

## Display strategy
**REPLACE, do not ADD** (this dissolves the text-overflow worries):
- The **name field is never touched** -- it keeps its `+3` suffix, no epithet appended (an appended
  title overflows the name; the `+3` is the most that field safely holds).
- The earned deed **replaces** the static flavor line (same char budget, better content) -- the
  weapon's blurb becomes what it DID, not what a designer wrote. On-thesis and zero net text growth.
- The card shows the **proudest** title only (a Lucavi Legend outranks a Goblin Mark); the full roll
  lives in the save file + roomier surfaces.
- **EN-only caveat:** the toast text and the composed card line are English-only (Reliquary Phase 1
  shipped, docs/RELIQUARY_AC.md). This is gameplay-neutral in FR -- the French wall
  (french-nxd-override-recipe) blocks item TEXT, not mechanics, and Reliquary's card line is a
  DLL-live paint over existing bytes, not a table/nxd change, so it never trips that wall; it just
  narrates in English regardless of the game's configured language.

**Then EXPAND across surfaces** (the story distributes; the card is one window of ~10):
1. Equip item card -- weapon deeds + Kills (already ours)
2. Unit name label above the sprite (battle) -- the earned title
3. Unit status / detail sheet -- the FULL legend roll (the roomy surface)
4. Formation / party screen -- title beside the name
5. Turn-order / CT forecast list -- title
6. Targeting / hover info panel -- title on the reticle
7. Shop / inventory item list -- weapon title inline
8. Victory / results screen -- "Demonsbane felled Velius" spoils beat
9. Save / party roster menu -- titles at a glance
10. The roster `+0xDC` nickname string itself -- retitles a generic everywhere at once
*(Patrick to finalize the surface list; assign which story goes on which surface.)*

## The apex: the UNIT inherits the blade's legend
A wielder earns a title FROM the weapon's story -- `Ramza, the Demonsbane` -- **but only if loyal to
one growing weapon.**

**Preferred surface (Patrick 2026-07-04, CE prototype in progress): the turn-start COMMAND MENU
HEADER** -- the "Ramza" label above Move/Abilities/Wait -- reworded to "Ramza -- The Oathbreaker".
Recurring, name-anchored, fires at the moment of agency; separate from the weapon desc so no space
fight. Likely the same SetTextString text-hook family (0x14028F79C) PromptSwap already rides.

CE POC 2026-07-04 -- surface CONFIRMED (rendered "Kenrick - Oathsmasher" live in the header):
- Buffer is 8-bit (Unicode=0), **25 chars**, zero-terminated. HARD CAP: `name + " - Title"` must
  stay <= ~24 chars or it clips; overflow CRASHES the game (observed). Long-named story units
  (Mustadio Bunansa) may not fit a title -- titles must be terse (<= ~14 incl. separator).
- The header is a downstream COPY, rebuilt from the source name on refresh -- a data-poke reverts
  after ~a minute, and the heap addrs are VOLATILE (writing a stale/relocated addr crashed the game).
  So persistence = a CODE hook (SetTextString 0x14028F79C, or the header builder's caller), never a
  data write. The generic string-struct populate at 0x140025Cxx is too broad to hook -- find the
  menu-header-specific caller / confirm it routes through 0x14028F79C. Weapon-hop every chapter = nameless; carry one blade through the Lucavi = it
makes a legend of you. The sword makes the hero. This is the attachment thesis leveling from item to
person.

## Proven levers (this is buildable, not a moonshot)
- **CreditKill death edge** already holds the victim's band entry (KillTracker.cs) -- new logic hangs
  off the existing call.
- **Enemy nameId is already read at the attribution edge** (ActorRegister.cs, U16(entry+ANameId),
  Proven ledger row) and classified player-vs-enemy.
- **Persistence:** clone the kills.json atomic (tmp + .bak) pattern into a parallel `legends.json`.
- **Announce:** proven big-banner callout (ShowSpike, eyewitness x4) with PromptSwap toast fallback.
- **Card paint:** the existing SuffixRotation site, DLL-live (so the French wall does NOT bite).
- **UNIT NAME read/write is PROVEN** (`docs/research/SPRITE_SWAP.md`, corrects the earlier "nameId doesn't
  resolve to a string"): roster `+0x230` (u32, "voiceID"/char-identity) drives portrait + on-screen
  NAME + voice via `GetCharNameFromSpecialName(voiceID, +0xDC)`. Unique id -> canon name (Ramza);
  generic id -> the **16-byte writable nickname string at `+0xDC`**. So: read any unit's name;
  RETITLE a generic by writing `+0xDC` (a <=16-char title like "Demonsbane" fits); story units resolve
  canon so title-paint their SURFACE instead. (FFTHandsFree pre-1.5 already grabbed + labeled units.)

## Open probes (do these FIRST, each cheap + bounded)
1. **Victim classification at kill:** can we reliably read the victim's CLASS (mage / monster /
   dragon / knight) at the CreditKill edge? Gates Marks + play-style titles + the last-victim class.
2. **Named-boss discriminator:** does enemy `ANameId` stably tell Velius from a job-sharing grunt at
   hp==0? Ledger flags the enemy/player nameId pools "not proven disjoint" -> may need composite
   job+sprite+level keys. Gates Legends.
3. **Killing-blow edge on marquee bosses:** some story bosses END the battle / crystallize by cutscene
   without a normal corpse-death edge -- confirm a credit actually fires on the Lucavi set.
4. **`+0xDC` retitle:** does writing the nickname hold + display everywhere without breaking saves?
   Gates roster-level generic unit titles.

## Design constraints to respect (hard-won this session)
- Legends are RARE -- many weapons earn none. The durable card epithet (not the rare banner) carries
  the everyday payoff; scarcity = meaning; legend-less weapons are fine.
- Big banner is bosses-ONLY -- if a goblin kill stops the screen, Velius stops feeling special. Hard-
  gate it; Marks earn via the quiet toast.
- Titles are pure fiction, ZERO stat bonus. The moment carries the feeling.
- Always keep the PromptSwap fallback so a Denuvo dead-hook launch never silently eats a once-per-
  campaign boss kill.
- Build order: weapon-side Reliquary first (proven surface, shippable), unit titles as the stretch
  once the weapon is telling its story live. Ship Marks (easier class probe) before Legends.
