# First 3 Battles -- Live Test Runsheet

*The antidote to the 166-box plan. You're on a DEV build, so every weapon is already +3 -- no grinding.
This covers the Section 6 GO/NO-GO. Skip every [EDGE] box for now. Don't touch Wyrmblood / Life Sap (dormant).*

**Watch the log live first** (paste into PowerShell, leave it running in a side window):
```powershell
Get-Content "C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods\prawl.fft.livingweapons\livingweapon.log" -Wait -Tail 40
```
**Whole-session PASS rule:** you NEVER see a line starting `engine:` (a signature threw), and every fight ends
with exactly one `battle: ended`. That alone is GO/NO-GO #8.

---

## Warm-up -- no battle, 2 minutes (instant confidence win)
Open the **Equip** menu and cycle swords 19-32.
- Each reads **Sword** (ids 33-37, 49, 50 read **Knight Sword**) + its overhaul name + flavor + a **Kills:** line.
- PASS = labels + Kills line all present. *(GO/NO-GO #5 labels, #6 card paint.)*

## Battle 1 -- The Staff Quartet  *(GO/NO-GO #3, #4)*
**Party:** 4 casters, each staff in the **main hand** -- Warlock's (60), Mending (61), Sanctus (64),
Staff of the Magi (66). Keep them clustered. Bring one fragile ally you'll let fall.
**Do, and watch the log:**
- Warlock's bearer + an adjacent mage cast a **charge** spell (Fira/a summon) -> both go instant. -> `choir ACTIVE`
- Stand **3** mages adjacent to the Warlock bearer -> only **2** cast instantly (the cap).
- Sanctus mage casts **Cure** -> heal lands ~30% bigger. -> `benediction ACTIVE ... +30%`
- End the Mending bearer's turn with allies in the 8 surrounding tiles -> they regen. -> `renewal ... mended`
- Let the fragile ally get **KO'd** -> its crystal countdown freezes at 3, never crystallizes. -> `sanctuary: divine intervention ... ->3 hearts`

## Battle 2 -- The Sword Line  *(GO/NO-GO #5, #6)*
**Party:** Sanguine Sword (23) on a Knight/Ramza, Zwill (10) on a Thief/Ninja, Swiftedge (28) anyone, Arcanum (30) anyone.
**Do, and watch the log:**
- Open the command menu on the Sanguine wielder -> **Shadow Blade** is there and casts. -> `shadow blade: ACTIVE`
  *(Heads up: enemy Knights show it too -- that's the known leak. Just note it, don't panic.)*
- Land a **kill** with Zwill -> you immediately get another turn. -> `extra-turn: ... scored a kill`
- Take a few Swiftedge turns **without getting hit** -> its Speed climbs +1/turn. -> `afterimage: ... Speed N -> M`  **(unproven -- this is the prize)**
- Hit a **buffed** enemy with Arcanum -> you steal the buff. -> `larceny: STOLE` *(skip if no buffed foes)*

## Battle 3 -- The Collision Stress Test  *(GO/NO-GO #6, #7, #8)*
**Party:** Warlock's Staff (60) on a bearer **+ Gloomfang (3) on an adjacent ally**; plus Venombolt (80),
Stormarc (86), Huntress (89) on others, Yoichi (90) on a Thief. Just fight a normal clustered battle.
**Watch the log for BOTH, same battle:**
- `choir ACTIVE` **and** `GRANT Gloomfang -> Concentration (support 213) ... readback=SET`
  *(I cleared this collision on paper -- different bytes. This confirms the engine honors both at once.)*
- Each non-staff signature fires: `plague:` / `ricochet:` / `maim:` / `barrage: ACTIVE`.
- PASS = both bits coexist + every signature fires + **zero** `engine:` lines + one clean `battle: ended`.

---

## After the 3 battles
You've cleared the live GO/NO-GO except the PROD checks. For those: redeploy prod (`.\BuildLinked.ps1 -Prod`,
fresh kills.json), boot, and confirm a freshly-equipped weapon does NOT fire at 0 kills (no `+3`, no ACTIVE line).
Then you're release-ready. Everything else in the big plan is depth -- chase the [EDGE] cases only if you're bored.
