TODO's
- Galewind doesn't work
- Outrider pistol only works outside of battles
- Finish the Samurai Swords
- Tune the Rods to not be as strong
- Replace the Stormbrand (it's sorta lame)
- Fix the swords art when swinging
- Claymore is being held in two hands but not benefitting from doublehand
- Address all user feedback
- Give the mod a flight recorder.
- Figure out how to make the mod work in French
- Migrate the remaining lossy-detection siblings (CharmLock/EagleEye/Larceny/Maim/Ricochet) to the cache + rearm
- Kill-tally milestones on the equip card beyond the counter (e.g., suffix flair at high milestones) -- cheap polish
- Remove Treasure Master
- Remove Offensive Chemist
- Alter Axes and Flails.
- Scholar's Ring idle nag repeats mid-battle despite the once-per-battle latch -- confirm ResetBattle double-fire
- Remove the inaccurate turn-detection methods from the repo: pointer-PRESENCE windows are falsified
  (ActorPtr parks on struck victims for seconds and never sits on human-driven units mid-turn --
  session logs 2026-07-04). Puppeteer's arrival/departure stepper is being replaced by the turn-credit
  clock; re-examine Iai's ReleaseSignal arrival (a wielder struck before its opening turn could
  false-release the Speed hold via the same dwell) and prefer acted-edge/turn-credit sampling everywhere
