#!/usr/bin/env python
r"""
Turn-owner TEAM signal probe -- for the Shadow Blade turn-gate (stop ability 165 leaking to
enemy Knights). The fix wants to inject Shadow Blade into the job-global JobCommand record ONLY
during player turns, and remove it during enemy/ally turns, so enemy AI reads a clean record.

That hinges on ONE question this probe answers empirically: is the condensed turn-queue TEAM
field (0x14077D2A2) a RELIABLE turn-owner signal on THIS build?

  - FFTHandsFree uses it (battleMode != 0 && team == 1 => enemy turn) and live-verified it.
  - BUT our own KillTracker calls the team field "unreliable" (reads 0/1/3 for the same player,
    often 3 on a save-load entry) and the condensed struct is documented to follow CURSOR HOVER,
    not the turn owner. So we must see what it actually does here before building on it.

It only READS memory (no writes) -- cannot affect the game.

USAGE (game running, in/around a live battle):
  python tools\probes\turnteam_probe.py            # ~33ms poll, prints on change
  python tools\probes\turnteam_probe.py 0.05        # custom poll interval (seconds)

SCENARIOS TO RUN while it watches (call out each as you do it so we can correlate the log):
  1. YOUR unit's turn -- open its command menu. Expect team=0(PLAYER) stable.
  2. While it's YOUR turn, move the cursor to HOVER an ENEMY (free-look). Does team flip to 1?
  3. An ENEMY Knight's turn -- watch it move/act. Expect team=1(ENEMY) for the whole turn.
  4. An NPC ALLY/guest turn if available. Expect team=2.
  5. Battle FORMATION / the moment a battle starts, before the first turn. What does team read?
  6. Load a SAVE straight into a battle (the case our tests flagged reads 3). What does team read?

What we need: team==1 STABLE during an actual enemy turn (so removal is safe), and team==0
(or at least NOT 1/2) during your own turn even while hovering an enemy (so no menu flicker).
"""
import os, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

BATTLE_MODE = 0x140900650          # u8
COND_BASE   = 0x14077D2A0          # condensed turn-queue / active-unit struct
T_TEAM, T_LVL, T_HP, T_MAXHP = 0x02, 0x00, 0x0C, 0x10   # team u16, level u16, hp u16, maxhp u16
ACTED       = 0x14077CA8C          # u8 "active unit has acted"
MENU_CURSOR = 0x1407FC620          # u8
SUBMENU     = 0x140D3A10C          # u8
PAUSE       = 0x140C64A5C          # u8

MODE = {0: "out", 1: "target", 2: "move", 3: "menu", 4: "target-ok", 5: "cast"}
TEAM = {0: "PLAYER", 1: "ENEMY", 2: "ALLY"}


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else -1


def u16(h, a):
    b = rd(h, a, 2)
    return (b[0] | (b[1] << 8)) if b else -1


def snap(h):
    mode = u8(h, BATTLE_MODE)
    team = u16(h, COND_BASE + T_TEAM)
    acted = u8(h, ACTED)
    lvl = u16(h, COND_BASE + T_LVL)
    hp = u16(h, COND_BASE + T_HP)
    mhp = u16(h, COND_BASE + T_MAXHP)
    cur = u8(h, MENU_CURSOR)
    sub = u8(h, SUBMENU)
    pause = u8(h, PAUSE)
    return (mode, team, acted, lvl, hp, mhp, cur, sub, pause)


def fmt(t0, s):
    mode, team, acted, lvl, hp, mhp, cur, sub, pause = s
    tm = TEAM.get(team, f"?{team}")
    md = MODE.get(mode, f"?{mode}")
    star = "  <-- TEAM" if team not in (0,) else ""
    return (f"[{time.time()-t0:7.1f}s] mode={mode}({md:<9}) team={team}({tm:<6}) "
            f"acted={acted} cur={cur} sub={sub} pause={pause} | active L{lvl} HP{hp}/{mhp}{star}")


def main():
    interval = float(sys.argv[1]) if len(sys.argv) > 1 else 0.033
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    print(f"watching {PROC} (pid {pid}) every {interval*1000:.0f}ms -- prints on change. Ctrl+C to stop.\n"
          f"call out each scenario as you do it (your turn / hover enemy / enemy turn / formation / save-load).\n")
    t0 = time.time()
    last = None
    try:
        while True:
            s = snap(h)
            if s != last:
                print(fmt(t0, s))
                last = s
            time.sleep(interval)
    except KeyboardInterrupt:
        print("\nstopped.")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
