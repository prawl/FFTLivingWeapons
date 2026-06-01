# TODO / ideas

Stuff I want to try next. Rough order, not strict priority. Most of this came out of
the Reddit thread + my own testing, so it's half design notes, half "go check if this
is even possible."

## 1. Innate Parry

Give classes innate Parry so weapon-evade builds aren't the only way to dodge melee.

Big open question: every class, or just the melee/combat jobs?
- Every job is simplest, and it barely moves difficulty since humanoid enemies get it
  too. Only physical attacks are affected and you still can't parry from behind.
- Melee-only is more flavorful. One idea from the thread: innate at half strength for
  combat jobs, full strength with the Parry passive equipped, and Knight gets full
  parry no matter what.

If I add it, probably cut the current weapon evade values (half or quarter) so it's
not double-dipping. Need to retest difficulty either way.

## 2. Make weapon reach cost something

Right now a reach-2 sword still lets you equip a shield (tested it), so reach is
basically a free upgrade. That needs a real cost.

Idea: the long-reach swords give up their off-hand; trade the shield / dual-wield
slot for the extra range. Maybe a reach-3 tier as the dedicated big two-hander
(claymore / zweihander vibe).

Things to actually verify before promising any of it:
- Can a sword even hit at reach 3? Lunging is confirmed at reach 2; I haven't gotten 3.
- Lunging does NOT force two-handed (shield still equips). So I need to find whatever
  flag actually blocks the off-hand. If there isn't one, fake the cost with lower
  WP/evade so reach is still a tradeoff.

## 3. Bake abilities into weapons

Let a weapon grant an ability so it frees up a slot. Example: the Riposte sword comes
with Counter built in, so you don't have to spend your reaction slot on it; equip the
sword, pick something else for the slot.

Need to check whether IVC equipment can grant reaction/support abilities at all, or if
I'm limited to the stat / element / status riders I'm already doing through EquipBonus.
Research before designing around it.
