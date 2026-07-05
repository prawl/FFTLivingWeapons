# Callout Banner Journey -- on-command battle bubble RE

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

Research/journal log for the "Living Weapon on-command UI" effort: pop the game's black rounded
callout bubble (the one that names an ability or announces a stat change over the acting unit)
with OUR text, on command, to announce Living Weapon tier-ups / kill milestones.

All code addresses are absolute in `fft_enhanced.exe` (image base `0x140000000`, ASLR-free, so
`0x140xxxxxx`-`0x148xxxxxx` module offsets are stable/hardcodable). Heap addresses (`0x436Bxxxxxxx`
band) are per-launch; resolve them from the static chain below, never hardcode.

## TL;DR -- where this stands

- **ON-DEMAND SPAWN: PROVEN LIVE 2026-07-03 03:17** (Patrick eyewitness, 4 fires from 4 different
  threads, log flag=0->1 each time). ShowSpike v9 F4 renders "BUBBA LUVS SPAWNING AT WILL" from the
  plain mod loop thread with NO natural callout in flight. The winning delta over every prior cold
  fire: the ritual must be BRACKETED with the stream-control helper `0x140111B74(ctrl,-2)` then
  `(ctrl,0)` after the orchestrator -- the helper's VM body is the show-flag setter. See "v9" in
  the spike ladder.
- **CUSTOM TEXT IN THE BUBBLE, ON COMMAND: PROVEN LIVE** (Patrick eyewitness + screenshots,
  2026-07-02 21:08 and 21:36). We render our own callout by calling the game's own show ritual
  and committing our text.
- **The "context constraint" is FALSIFIED** (2026-07-03, v9): the ritual paints from ANY thread --
  what every silent cold fire was missing is the helper bracket, the second half of the native
  sequence. "THE WALL, precisely" below preserves the (wrong) contemporaneous reasoning.
- **Shippable feature path (Track A):** queue a toast on a tier-up; fire on the next natural callout
  (frequent in combat -- every action pops one). Reuses only proven mechanisms. BUILT and proven live
  2026-07-02/03 (CalloutDelivery/CalloutHook, uncommitted pending the amended AC).
- **Track B (on-demand spawn/dismiss) is now MANDATORY** (amended AC 2026-07-03: nothing ships
  without it). Strategy: find the game's own callout-request ENQUEUE upstream of the job dispatcher,
  not force the ritual. The natural call's stack is decoded (see the stack-scrape section below); the
  dig is at "identify the dispatcher's queue + its posting side."

## The mechanism, fully specified

### Controller (the callout "director"), reached from statics

    controller = [[[0x143CD9DA8] + 0x10] + 0x48] + 0x58

Live-verified `== 0x436B07D000` this session: vtable `0x140712990`, id `0x998` (unique among class
instances), request type at `+0x50`. This static chain **replaces the launch-stable-heap assumption**
-- the controller and everything hanging off it (holder = `controller + 0x58`) are reachable without a
heap scan. The wrapper `0x1400E8A84` resolves the controller by exactly this chain.

Controller layout that matters:
- `+0x30 / +0x38 / +0x40 / +0x48` -- four object pointers; the gate `0x1400F5828(ctrl+0x30, 4, 0, 0)`
  requires the first four non-null (banner objects exist) or the orchestrator bails.
- `+0x38` objA (parameter popup), `+0x40` objB (ability-name bubble), `+0x48` objC.
- `+0x50` (u32) = request TYPE, CALLER-stamped (see the type trap below).
- `+0x58` = holder (text widget holder), `+0x88` of holder == `+0xE0` of controller = the SHOW FLAG.
- `+0x98` / `+0xC0` = where the orchestrator copies the token object.
- `+0x18` -> an object the show-flag function passes to its child-finder helper.

### Orchestrator (the text/setup half) -- TRUE ENTRY 0x140111D20

Canonical prologue (`mov [rsp+0x18],rbx; push rbp/rsi/rdi/r12-r15; mov rbp,rsp; sub rsp,0x30`), 0x20
bytes before the CE capture site `0x140111D40`, past int3 padding. It disassembles CLEAN end to end
(ret at `0x1401120CF`) -- it is NOT VM-dispersed; only its CALLERS are virtualized. Earlier notes
calling the orchestrator "Denuvo-dispersed" were wrong.

Args: `rcx` = controller, `rdx` = token text object, `r8d` = has-text; **`r9` is ignored** (xor'd
first instruction).

Ritual (the setup/text half):
1. gate `0x1400F5828` (four objs non-null) else bail.
2. clear `[obj+0x1A0]` on all three objs.
3. select obj by type `ctrl+0x50`: 1-6 -> `+0x38`, 7/8 -> `+0x48`, else `+0x40`; REQUIRE
   `[obj+0x218] == 0xD` or the selected obj is NULL and the next deref AVs (pre-check when replaying).
4. FindChild(obj+0x110, region-name) -> rebind holder via `0x14028F658`.
5. if has-text: copy token obj into `ctrl+0x98` (`0x1401EC074`) and `ctrl+0xC0` (`0x1401CD750`),
   then SetTextString(holder, `0x1400FCBB4(tokenObj)`) via `0x14028F79C`.
   ELSE (r8d=0): SetTextStringAndCommit(holder, literal "?" @ `0x1407040A8`) -- the ritual STILL
   runs with no token object at all (a zero-pointer-risk test path).
6. Commit `0x14028F7C8(holder, 1)`.
7. show `0x140409A00(obj)`.
8. per-type NAMED animation track lookup `0x140409808` + arm `[track+0x68]=1` + play `0x14040016C`
   + kick `0x140409CBC`. Track names by type: 1=ParameterUp, 2=ParameterDown, 3=Item, 4=Trap,
   5=TreasureBoxCrystal, 6=DestroySteal, 7=NineGrid00/01+Layer00+Notification, 8=Error.

### Our entry: wrapper 0x1400E8A84

Resolves the controller from the static chain, does a readiness vcall `[[subsys]+0x10]`, then calls
the orchestrator forwarding `(rdx, r8d)`. Sibling wrappers (`0x1400E8A28` etc.) drive other callout
subsystems (P+0x58 id 0xBE7, P+0x68 id 0x17CD, P+0x90 id 0x1904).

### Token stream format

`rdx` points at a 0x60-byte stream = template string-table id at **`+0xC`** (u16) + E1/E2-tagged
binding tokens (the lstr/lnum "envelope" slots the template fills from). Two templates seen live:
- `0x5062` = the ability-name string (objB name bubble, type 0).
- `0x508C` = "`<string=lstr1>'s <ennoun(C_MES_ID_SAMPLE,5,lnum4,1)> increased by <num=lnum2>!`"
  (objA parameter popup, type 1). `C_MES_ID_SAMPLE` is a leftover dev placeholder id. Unbound
  placeholders render LITERALLY -- that raw template string is what the id-swap screenshots showed,
  proving the on-screen text is driven by these tokens.

### The TYPE trap (cost v3 a false negative)

`ctrl+0x50` is CALLER-stamped request state. Idle it latches the last value; a fresh launch reads 0.
Type 0 walks off BOTH switch ladders in the orchestrator (obj-select AND the track arm), so set-text
runs but the visible track-arm/kick are skipped -> a silent no-op. **Stamp the type before firing.**

### Natural shape

One player action = TWO orchestrator calls on the same controller: type=0 (name bubble, objB) then
~1.6s later type=1 (parameter popup, objA, ParameterUp track). Plain attacks and enemy actions ALSO
fire type=0 callouts (id `0x800F` streams). So the orchestrator fires FREQUENTLY in ordinary combat
-- the piggyback portal is dense.

## THE WALL, precisely: context, not a value -- **FALSIFIED 2026-07-03 (see v9)**

> **This section's conclusion was WRONG.** The observations below are all real, but the inference
> ("the show is gated on the action job's context") did not survive v9: the full native sequence
> INCLUDING the `0x140111B74(-2)/(0)` helper bracket draws from any thread, cold. What CHASE had
> and every cold fire lacked was never the context -- it was that in the natural/chase case the
> NATURAL CALLER executed the helper calls right after our fire. Kept unedited below as the
> contemporaneous reasoning (workflow-conventions: never tidy a contradicted claim silently).

Every state layer was force-tested and every one is a MIRROR/derived -- poking it draws nothing:
holder show flag, banner-obj vector, child animation tracks (the engine even ADVANCED our stamped
track timers exactly like a natural show), the text-widget shown-state trio, alpha. Full post-ritual
snapshots (bound widget, `w34==3`, obj `+0x218`/`+0x280`, alpha) are BYTE-IDENTICAL between a natural
draw and a silent fire. Alpha even begins ramping (0 -> 0x2a) on our fire. Nothing draws.

What distinguishes a drawing fire from a silent one is **execution context**:
- The orchestrator ran on a DIFFERENT thread id every session (29976, 28024, 21684, 10500, then v8's
  49416). Not "the logic thread": whatever worker got handed the callout job.
- Thread census 2026-07-02: **130 threads**, all parked in ntdll/kernel waits when idle -- a job/worker
  pool, not a fixed logic/render split. (An earlier "148" figure was wrong; measured 130.)
- **The decisive contrast:** CHASE (fire from inside the natural call's own stack, mid-job) DRAWS.
  v8 (fire from a planted per-frame pump callback -- a different job) does NOT, even though both run on
  "some worker." The difference is not thread identity; it is being INSIDE the callout/action job.

Conclusion: the deep VM show-write is gated on the action job's context (almost certainly a
scheduler "current job" / fiber-local pointer), not on any holdable struct byte. Piggybacking puts us
inside that context for free.

### The separate show-flag function (CE write-bp finding, 2026-07-02 22:16)

Write-breakpoint on the show flag `holder+0x88` (== `controller+0xE0`, live `0x436B07D0E0`) caught a
DISTINCT function -- VM/Denuvo-lifted, instruction `0x14A20C3B6`, on the action worker (stack
`0x12D2xxxx`) -- doing:

    mov al, 01
    mov rcx, [rbx+18]        ; rbx = controller; rcx = [ctrl+0x18]
    mov [rbx+0E0], al        ; SET show flag = 1
    call 1403F9E34           ; rax = first child in [rcx+0x68..rcx+0x70) whose +0x218 == 1
    mov rdi, rax

So the show flag is set by a SEPARATE action-thread function, NOT by the orchestrator we call.
`0x1403F9E34` is a small generic "find child in state 1" helper (185 callers). The containing function
is VM-lifted (can't cleanly disasm), essentially `showBanner(controller)` in the action job. This is
further evidence the visibility half lives in the action/event system, separate from the orchestrator's
text/setup half.

## The spike ladder (evidence trail)

BannerSpike/ShowSpike is dev-gated (`#if LWDEV`) scaffolding, uncommitted. Each rung eliminated a
hypothesis:

- **BannerSpike v1-v6 (earlier):** proved in-process calls work (no Denuvo interference on delegate
  calls), the text pipeline is 100% (Commit pushes our bytes into the live render widget), and the
  HIJACK: poll `holder+0x88` rising edge every in-battle tick + SetTextStringAndCommit -> the game's
  own natural callout renders OUR text ("BUBBALUVS+3", eyewitness). The hijack REPLACES a natural
  callout's text; it cannot create one.
- **ShowSpike v3:** call the wrapper with the previous-launch token capture. Fired without crash, drew
  nothing (the type=0 trap, before it was understood).
- **v4:** hook the orchestrator ENTRY as a capture/replay instrument. Proved our fire REACHES the
  orchestrator with byte-identical fresh-capture args and still draws nothing -> gap is context, not
  args. Mapped the two-call natural shape.
- **v5:** discovered the thread split (natural on the logic/action worker, our render-thread portal is
  a different thread) and that post-ritual state is identical natural-vs-ours.
- **v6:** two content-swapping experiments to make success VISIBLE. CHASE = re-fire id-patched right
  after a natural call returns (logic worker, mid-job). MUTATE = patch the natural call's own token id
  in-flight. BOTH rendered the WRONG string in the bubble (screenshots) -> tokens drive the on-screen
  text and our injected content reaches pixels.
- **v7:** CHASE + immediate SetTextStringAndCommit(holder, custom ANSI). Rendered "BUBBA LUVS THE
  ORCHESTRATOR" -- full custom text through the native show, on command (eyewitness 21:36).
- **v8:** plant a managed thunk in the pump's per-frame callback slot `0x14114B738` (NULL at rest;
  data write, no code patch, Denuvo-invisible, reversible) to fire at an arbitrary time on the logic
  loop. The callback fires -- but on a sibling worker, not the action job -- and does NOT draw. This is
  what nailed "context, not thread identity" (a reading v9 later falsified: v8 was missing the
  helper bracket, not the context).
- **v9 (2026-07-03 03:17, THE CRACK -- Patrick eyewitness x4):** F4 replays the FULL native show
  order recovered by the Track B disasm dig (ShowBubbleCallout `0x1400EF494`'s body):
  dismiss-if-lingering -> arm `[ctrl+0xE8]=0x78` -> stamp type -> wrapper-fire captured type-0
  tokens -> SetTextStringAndCommit(custom) -> `0x140111B74(ctrl,-2)` -> `0x140111B74(ctrl,0)` --
  from the MOD LOOP THREAD, the deliberately-wrong context. **Draws every time** (4 fires, 4
  different pool threads, log flag=0->1, "BUBBA LUVS SPAWNING AT WILL" on screen with no natural
  callout in flight). The helper bracket was the missing visibility half all along (its VM body
  `0x14A20C367` contains the CE-caught show-flag setter `0x14A20C3B6`). BONUS: post-fire
  `[ctrl+0xE8]` DECREMENTS (120->119 observed) -- the engine runs its own countdown on OUR bubble,
  so duration = write a bigger arm value. DISMISS PROVEN same night (F6 after the eaten-F9 rebind;
  Patrick eyewitness: "F6 removes it early") = zero `[ctrl+0xE8]` + helper(ctrl,-2). DURATION
  QUANTIFIED same night: armed 0x258 (600 ticks) -> ~10s eyewitness lifetime, exactly 5x the
  120-tick baseline => **the countdown runs at 60 ticks/sec; duration = seconds * 60** (native
  shows arm 0x78 = 2s).

Instrument lessons: `F3`, `F7`, and `F9` are eaten below `GetAsyncKeyState` on this box (LL keyboard
hook); `F2`/`F4`/`F6`/`F8` work. One-key-one-experiment (a multi-mode F6 cycle caused double-armed
casts). Hook-canary
discipline (post logging-overhaul: "orch CALLED" demoted to Debug-tier, file-only by default) -- a
natural callout bubble with no "show-spike: orch hook ALIVE" line EVER logged this launch = Denuvo
dead-hook launch, restart. (Enable Config.VerboseLog to see "orch CALLED" itself on the console.)

## Operational gotchas

- Sentinels (Offsets.cs is authoritative): battleMode `0x1409069A0`, slot0 `0x140782A30`,
  slot9 `0x140782A54`.
- battleMode 3 = cursor on a unit tile / menus; the battle-ENTER edge needs a mode-2 frame (cursor on
  EMPTY ground, ~20s settle+locate).
- Deploy recipe: kill `FFT_enhanced.exe` -> `.\BuildLinked.ps1` -> launch via Reloaded.
- Probes: `tools/probes/thread_sample.py` (suspend/getcontext RIP+stack histogram),
  `tools/probes/code_dump.py`, `hexdump.py`, `caller_scan.py`, `retaddr_check.py` (stack-scrape qword
  -> return-address classifier), `disasm.py`. Throwaway edge/census probes lived in the OS temp
  scratchpad.

## Track B dig: the natural call's stack, DECODED (2026-07-03 01:38 session)

RtlCaptureStackBackTrace from inside the orchestrator detour truncates at frames[1] (the unwind walk
dies at the managed/Denuvo boundary -- observed live). The workaround that WORKED: a raw STACKSCRAPE
(ShowSpike F6) -- deref the current thread's own stack `rsp..min(rsp+0x4000, stack base)` and log every
qword in `0x140000000..0x14D000000`. Three captures landed: two NATURAL type=0 shows plus our own
piggyback delivery (self-validating -- its unique frames are our wrapper `0x1400E8A84`/`0x1400E8AD0`).

`tools/probes/retaddr_check.py` (new instrument) then classified every scraped qword: a qword is a real
return address only if a call instruction decodes ending exactly at it. The NATURAL chain, outermost
first (all verified live; sites and decoded targets from the probe):

    0x1405DAE12  site 0x1405DAE0C  call qword [rip+0x36c76]   ; ptr 0x140611A88 = 0x14060CE50 (in-image)
    0x1471B83AF  site 0x1471B83AC  call qword [rax+8]         ; Denuvo VM frame
    0x1402F2EC1  site 0x1402F2EBE  call qword [rbx+0x30]      ; vtable call, slot 6
    0x1402F3799  site 0x1402F3797  call qword [rbx]           ; vtable call, slot 0
    0x1402F3884  site 0x1402F387E  call qword [rip+0x31d954]  ; ptr 0x1406111D8 = 0x7ffdfefa7070 (system-DLL import)
    0x1402322B6  site 0x1402322B1  call 0x1400E5F78           ; real code -> unexplored entry
    0x14952FB84  site 0x14952FB82  call rdx                   ; Denuvo VM frame
    0x149635AAC  site 0x149635AA7  call 0x140111D20           ; the orchestrator -- via the KNOWN Denuvo-region site

The second natural capture (the ~1.2s re-show of the same bubble) adds `0x1400EF5CF` (site
`0x1400EF5CA`: `call 0x14041FB70`) -- the same function region as known orch site `0x1400EF57A`.
Scraped qwords that are NOT return addresses (spilled registers / data / fn pointers):
`0x1408033C8` (the innermost slot), `0x142FF0E20`, `0x142FF0770`, `0x143792670` (twice),
`0x1437930D0`, `0x1405C0080`.

Reading: the `0x1402F2xxx/0x1402F3xxx` frames are a JOB-DISPATCHER worker loop -- wait on an OS
primitive (the system-DLL import), pop a job object, virtual-call it (`call [rbx]`). The callout job
body runs in `0x140232xxx` (which also holds two known direct orch sites) and reaches the orchestrator
through VM frames via the known Denuvo-region caller `0x149635AA7`. The request-ENQUEUE that feeds
that dispatcher queue is the on-demand spawn target. This supersedes open question 1 below (the
real-code callers are now known without CE).

Same session, incidental: the swept-holder triple CLOSED -- BannerPipe's independent heap sweep
located the holder at `0x436B07D058` (log 01:39:07), equal to the piggyback holder and the static-chain
holder. All three legs agree; that was the last fact needed to retire BannerPipe in the cleanup stage.

## Track B dig part 2: the machinery behind the chain (2026-07-03 ~02:00, live disasm sweep)

Three parallel read-only disasm passes over the running process (PID 32792; all claims below verified
by disassembly + live RPM reads unless marked INFERRED). This section supersedes the guesswork in
"THE WALL" about what the job context IS -- the pipeline is now mapped end to end, and the
request-enqueue APIs that feed it are real-code-callable.

### The scheduler: stackful FIBERS, not a lock-based job queue

M:N cooperative fiber scheduler -- this IS the "action-job context" the whole Track B wall was about.
17 OS worker threads (registry at `[0x143CD9E98]`, 17 entries x 0x60: tid `+0x18`, per-thread event
HANDLE `+0x30`, scheduler event `+0x668`) and 17 fiber SLOTS in a static array at `0x143791D70`
(17 x 0x120: `+0x00` cur_fn, `+0x08` req_fn, `+0x110/+0x118/+0x11C` dirty flags). The pieces (each
contains one of the natural-capture frames):

- **Fiber runner `0x1402F372C`** (frame 0x1402F3799): `rbx = 0x143791D70 + slot*0x120`; swaps
  cur_fn=req_fn when dirty, saves fiber context via `0x14060CF70` (CET/rdsspq-aware reg+xmm save;
  restore twin `0x14060CE50`), then **`call qword [rbx]`** = run the slot's coroutine on the logic
  thread. This is where the callout coroutine executes -- the context our v8 pump callback wasn't in.
- **Park `0x1402F3810`** (frame 0x1402F3884): GetCurrentThreadId -> linear-scan the registry ->
  SetEvent(scheduler) -> WaitForSingleObject(own per-thread event).
- **Mailbox invoker `0x1402F2E7C`** (frame 0x1402F2EC1): WaitForSingleObject(delegate+0x28) ->
  `call [delegate+0x30]`.
- Wait primitive `[0x1406111D8]` = `kernel32!WaitForSingleObject` (resolved by module-enum +
  GetProcAddress on the live PID); siblings `[+8]=SetEvent`, `[0x1406111B0]=CreateEventA`,
  `[0x1406111A8]=CloseHandle`, `[0x1406113F0]=GetCurrentThreadId`.
- **Single-writer cooperative** -- NO interlocked ops in the enqueue path, so there is no atomic
  queue head/tail to hook; the "queue" is per-slot req_fn + dirty flags.
- Generic coroutine SPAWN: `0x1402F7E78(ecx=slot_id, rdx=fn)` (105 sites, clean
  `mov ecx,SLOT; lea rdx,[handler]; call` idiom) over low-level `0x1402BA620(ecx=slot, rdx=fn)`
  (85 sites; writes req_fn to `jobslot[slot]+0x08` + dirty + argtabA `[0x14078D5D8]`). Both are E9
  thunks into the Denuvo VM but stable/callable addresses.

### THE REQUEST-ENQUEUE (the prize) -- two convergent real-code entries

1. **`0x1400E5F78` -- EMIT / callout request-enqueue.** This IS the natural-capture site
   `0x1402322B1` (two agents landed on it independently). Signature:
   `emit(rcx = manager [0x143CD9DC0], edx = opcode/template id, r8 = &descriptor, r9d = 1)`. E9 thunk
   to REAL (non-VM) code at `0x14952FAF0`: three UI/battle readiness gates (virtual calls off a
   mgr-of-mgrs), then `r10 = [manager + opcode*8 + 0x9C8]` and `call r10` = the per-opcode dispatch
   that posts the show. 15 direct sites; siblings `0x1400E5C44` (20), `0x1400E5CE8` (14). The
   DESCRIPTOR (r8, on stack at rsp+0x58): `{u32 kind@+0 = 1, u32 @+4 = 0x1B, u16 templateId@+0xC}`;
   template ids seen live: 0xF8D / 0xF41 / 0xEE1.

2. **`0x1400EF494` -- ShowBubbleCallout**, a mid-level "show a bubble" entry (whole function
   disassembled). `bool f(rcx = manager [0x143CD9DC0], edx = eventKind, r8d = eventParam,
   r9d = unitId & 0x7FF)`. Calls the Denuvo-VM SELECTOR `0x1400EE264` -> state byte `obj+0x3514`,
   chosen id `obj+0x3518`; **only if the selector returns 4** does it run the ritual (dismiss any
   current show, arm linger, build token `{1, 0x1B, 0, calloutId}`, `call 0x140111D20` orchestrator,
   bracket with dismisses, return 1). Selector state 3 -> sibling `0x1400EEC18` (voiced/portrait
   flavor via the `+0x90` UI child). The selector's decide-whether/which logic is VM-opaque; its
   plaintext outputs (`obj+0x3514/+0x3518/+0x351C`) are the observable surface.

The two ShowBubbleCallout callers (`0x1402320A3`, `0x1402E86C7`) are twin fallback ladders that load
`rcx = [0x143CD9DC0]`, try the voiced flavor first, then the plain bubble; the `0x140232xxx` ladder's
gate-false branch lands exactly on the natural-capture return `0x1402322B6`.

### DISMISS + the real linger timer (the linger negative's mechanism, LOCATED)

- **`0x140111B74(ctrl, edx)`** is the engine's own callout dismiss/commit helper (E9 thunk -> VM
  `0x14A20C367`, stable address). `edx = 0xFFFFFFFE (-2)` = **kill the current bubble**; `edx = 0` =
  post-show commit. `ctrl = [[[0x143CD9DA8]+0x10]+0x48]+0x58`.
- **`[ctrl+0xE8]` (dword) is the LINGER COUNTDOWN.** ShowBubbleCallout arms it to `0x78` (120 ticks)
  at show start; a per-tick routine decrements it to auto-dismiss. This is the exact timer the linger
  experiment (CalloutLinger.cs) could not budge by re-committing TEXT -- now located. It is NOT in the
  "state-byte holds get rewritten every frame" dead-list (ctrl+0xE0 show-flag / objB+0x280 / etc.),
  so **holding `[ctrl+0xE8]` at 0x78 each tick is an untested, cheap infinite-linger / dismiss-control
  candidate**: the countdown decrements, so pinning it high should block the auto-dismiss. Distinct
  from the text-reading timer (armed once from committed text length); visible lifetime is gated by
  whichever expires first.

### Manager / roots (heap, per-launch -- resolve, never hardcode)

Singleton `[0x143CD9DC0]` = live `0x515AFF5140` (vtable `0x1407046E0`); its `+0x3510..0x3528` block
is the selector's I/O, zero at idle. Root `[0x143CD9DA8]` = `0x436B0130C0`.

### What this means for the amended AC (spawn + dismiss on demand)

- **DISMISS: high-confidence, testable now.** `0x140111B74(ctrl, -2)` + zero `[ctrl+0xE8]` is the
  native teardown.
- **SPAWN: two candidate entries** (`0x1400E5F78` emit, `0x1400EF494` whole-show). The open question
  is unchanged in KIND but far narrower in SCOPE: do these render when we are NOT already inside a
  natural callout's fiber-coroutine context? Emit-from-piggyback (our proven drawing context) is
  nearly free; truly arbitrary-time (no callout nearby) may still need to SPAWN the coroutine onto a
  fiber slot (`0x1402F7E78`) -- the harder path, but now mapped rather than a mystery.
- **Experiment ladder (read-only first, per discipline):**
  1. Read-only detour-log at `0x1402322B1` on a NATURAL callout -> capture the exact `edx` (templateId)
     + the 16-byte `r8` descriptor at `[rsp+0x58]` + dump the manager object = the full arg recipe.
  2. Watch `[ctrl+0xE8]` lifetime -> confirm the linger timer (0x78 -> countdown -> 0 at auto-dismiss).
  3. Cold-call `0x1400E5F78` (or `0x1400EF494`) from the piggyback with the captured descriptor ->
     does a SECOND bubble draw? = on-demand SPAWN proof.
  4. `0x140111B74(ctrl, -2)` -> on-demand DISMISS proof. (Bonus: pin `[ctrl+0xE8]=0x78` per tick ->
     infinite linger.)

## UI-contention observation session (2026-07-03 ~04:45, ui_contention_probe.py) -- design inputs

Scripted eyewitness session (F4 spawn -> unit clicks with the "Select a tile" prompt up -> real
move+attack -> status card) while `tools/probes/ui_contention_probe.py` (NEW instrument) watched
battleMode + the `[ctrl+0xD0..0x100)` window of our controller AND the wrapper-family sibling
subsystems ([parent+0x58]/[+0x68]/[+0x90], ids 0xBE7/0x17CD/0x1904, plus their +0x58/+0xA0 hops).

Findings (all timestamp-correlated with the scripted actions):
1. **The move-prompt overlay is NOT in the callout sibling family** -- none of the 11 watched
   windows changed during unit selection; only battleMode moved. Its owning subsystem remains
   unidentified and does NOT need to be: see 2.
2. **battleMode is the practical spawn gate** (sentinel `0x1409069A0`, already read per tick):
   1/2 = free-roam cursor states (our F4 fired in mode 1 and rendered clean), 3 = cursor on
   unit/menus (prompt territory), 3->4->5->0 = action execution (natural callouts fire in the
   0 phase right after), flapping 1<->2<->3 = cursor gliding across tiles.
3. **Overlays occlude but do NOT kill our show**: the 10s F4 bubble's countdown ran uninterrupted
   through unit clicks with the prompt on top (flag stayed 1, expiry exactly on schedule).
   Yield-on-overlay is a UX courtesy, not a correctness requirement.
4. **60 ticks/sec re-confirmed to the decimal**: armed 0x258 = 600 ticks -> flag fell 10.02s later,
   countdown visibly decrementing per ~16.6ms sample. Natural expiry clears flag AND timer together
   (`+0xE0:01->00 +0xE8:01->00`), and the dword keeps decrementing past zero (0 -> 0xFFFFFFFF...)
   while idle -- harmless, but means "timer > 0" alone is NOT an is-showing predicate; use the flag.
5. Specimen, not load-bearing: natural type-0 shows stamp `ctrl+0xD0/+0xD1 = 78/DC`; our F4 path
   stamps `60/D9` (flipped back by the next natural show). Meaning unknown.
6. Weak single blip: `[parent+0x58]` subsystem (id 0xBE7) byte +0xD0 1->0 near the status-card
   step. Uncorroborated; ignore unless it recurs.

### The locked toast-scheduler rulebook (design, pre-/build)

- Milestone -> queue -> spawn only when battleMode in {1,2} AND ~2s since the last natural
  orchestrator call (the hook timestamps every one) AND our surface idle (flag +0xE0 == 0).
- Our toast showing and battleMode leaves {1,2} (unit selected / action starting) -> instant
  self-dismiss (proven: zero +0xE8 + helper(ctrl,-2)); requeue once if it got < half its time.
- A natural callout takes the surface (hook fires while our toast shows) -> yield, requeue-once.
  Never re-assert over the game's text; evicted twice = drop and log.
- Duration 10s default (Patrick's preference), per-kind Tuning knob, ticks = seconds * 60.
- Same battle gates as every module (InLiveBattle etc.); queue resets on battle exit.
- Mechanics behind a seam (Spawn(text, seconds) / Dismiss()); scheduler policy pure + unit-tested.

## The Wait-state pivot: facing-prompt slot delivery (2026-07-03 ~06:00, PROVEN 06:14)

Live testing of the scheduler build surfaced the design flaw no unit test could: opportunistic
delivery (spawn in the next safe window) is RANDOM from the player's seat -- "first blood" arrived
3-4 enemy turns after the kill, out of context. Patrick's redesign: **deliver in the ONE
deterministic, reproducible slot the game already provides** -- the Wait-state facing prompt
("Select a facing direction and press <keyicon=ok>"), which (a) appears at the end of every player
action = the exact attribution moment, (b) STAYS until the player confirms facing = reader-
controlled duration with zero contention, and (c) tells experienced players nothing they need.
Logic: toast pending -> take the slot; nothing pending -> vanilla fallback.

Hunt (ShowSpike v10, the F5 COMMIT TAP -- both text setters hooked, logging holder + guarded
ASCII preview + tid; run 2 added dedupe after a HUD name-plate's 60Hz re-commit flooded run 1):

    06:00:21 commit-tap: SetTextString holder=0x436B017118 text=0x4D5BFBE0C0
             "Select a facing direction and press <keyicon=ok>" (tid 48948)

- The prompt rides **SetTextString 0x14028F79C** (the orchestrator's token-string setter), not
  the ANSI commit 0x14028F720. Text = plain ASCII with markup tags.
- The holder 0x436B017118 sits inside the [parent+0x58] sibling subsystem (ctrl id 0xBE7 -- the
  ui_contention probe's child58 0x436B017000): the prompt banner was in the wrapper family all
  along, at an offset outside the probe's +0xD0..0x100 window.

Proof (ShowSpike v11, PROMPT SWAP -- F6 arms a one-shot: in the SetTextString hook, prefix-match
"Select a facing" and swap rdx to a pinned plain-ANSI payload, then forward):

    06:14:09 F6 -- PROMPT SWAP armed (one-shot)
    06:14:25 PROMPT SWAP fired -- holder=0x436B017118 now carries "BUBBA LUVS THE WAIT STATE"

Patrick eyewitness: renders in the prompt slot, STAYS until facing is confirmed, next prompt
vanilla. All three product behaviors proven in one shot. EN-only text match (the FR text layer is
walled anyway).

### PRODUCTIONIZED + LIVE-VERIFIED (2026-07-03 06:50-07:11, Patrick eyewitness + log)

Built via build-lite (SHIP 9/10, suite 1544/0/0, both flavors, analyze 0): `PromptSwap.cs`
(testable decision core: guarded prefix read + dequeue-only-after-match + 96-char clamp; native
hook half: 4-arg-forwarding detour on 0x14028F79C, pinned buffer, always-forward fault posture)
plus the BannerToast first-blood/tier same-pass MERGE (`FirstBloodTierPayload`). The entire
scheduler-era delivery stack (ToastScheduler(.Policy), CalloutSpawner, CalloutCapture,
CalloutHook, their tests, the 7 Tuning scheduler knobs) was DELETED as superseded -- the
production toast stack is now BannerToast (queue + wording) + PromptSwap, full stop. The v9
cold-spawn/dismiss/duration mechanics remain proven-and-banked here and in ShowSpike for future
non-toast uses.

The acceptance log (Chirijiraden, dev thresholds, DevSeedKills=false, zero-kill start):

    06:50:43 kill: Chirijiraden earns kill #1
    06:50:44 prompt-swap: delivered "Chirijiraden draws first blood and has grown to +!"
    06:51:31 kill: Chirijiraden earns kill #2
    06:51:32 prompt-swap: delivered "Chirijiraden has gained its 2nd kill and has grown to +2!"
    07:10:39 kill: Chirijiraden earns kill #3
    07:10:41 prompt-swap: delivered "Chirijiraden has gained its 3rd kill and has grown to +3!"
    07:11:40 kill: Chirijiraden earns kill #4        (no crossing -> vanilla prompt, no line)

Every delivery ~1.3s after its kill, in that action's own Wait slot; non-kill events fall through
to the stock facing prompt (Patrick eyewitness). The 72-char max-length payload rendered on the
first prompt of the session (width check passed).

## Open questions / future experiments (Track B only)

1. SUPERSEDED by the sections above (the real-code chain is decoded and the enqueue APIs are mapped
   without CE). The CE write-bp on `0x436B07D0E0` remains the fallback instrument if a dig stalls.
2. CE write-bp on the inner-widget alpha `+0xC7` AFTER our fire -- catch what tears our bubble down
   (now largely answered: `[ctrl+0xE8]` is the linger countdown; confirm live).
3. Thread-id-gated fire: only fire when the pump callback runs on the tid the orchestrator hook last
   recorded. Cheap; may or may not be sufficient if the gate is fiber-local rather than thread-local
   (the scheduler IS fiber-based, so this is likely insufficient -- spawn a coroutine instead).
4. Whether one natural caller invokes BOTH halves (orchestrator text + VM show-flag) in sequence --
   answered: `0x1400EF494` does exactly this (orchestrator + the +0xE8/dismiss bracket) in one body.

## Implementation plan

### Track A -- the piggyback toast (SHIP THIS)

Reuses only verified mechanisms. Design:
- A permanent Reloaded hook on the orchestrator entry `0x140111D20` (the hooks plumbing --
  Mod/Engine/csproj/ModConfig -- is the infrastructure this needs; commit it as its own stage).
- A toast queue: a tier-up / kill-milestone enqueues a payload (short ANSI string, <= holder budget).
- On the next natural orchestrator RETURN (inside the detour, mid-job = the drawing context), if a
  toast is pending: run the ritual (stamp type, fire the wrapper with a captured token stream) then
  immediately SetTextStringAndCommit(holder, payload). One toast per queued event, not every callout
  (pacing policy). No text restore needed (the natural path rewrites the template every show).
- Density argument: a kill IS an action; the killing action fires its own callout, so a job is in
  flight at the exact moment we want the toast. Latency: sub-second to a few seconds.
- Productionize as a real module (NOT `#if LWDEV`): config gate, `IGameMemory`-injected for tests,
  unit tests for the queue/pacing/type-stamp logic, prod compile. Pattern-match the existing
  `BannerToast` / `BannerPipe`.
- Alternative simplest variant: the proven HIJACK (repaint the natural callout's text on its rising
  edge) -- but it EATS the ability name; the extra-callout piggyback is cleaner UX.

### Track B -- arbitrary-time show (optional polish)

Only needed for an out-of-combat / no-action toast (e.g. a Chronicle milestone with no callout in
flight). Requires solving the action-job context requirement (open questions 1/3/4). Multi-day
scheduler dig; NOT required for the tier-up toast. Document and defer.

### When this lands, update:

- memory `living-weapon-levelup-banner` (flip to SHIPPED with the module name).
- `docs/LIVE_LEDGER.md` (the callout row: Patrick flips to PROVEN).
- `docs/MECHANICS.md` (add the confirmed on-command callout mechanic).
