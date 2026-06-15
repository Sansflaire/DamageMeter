# DamageMeter — Catalog of Things I Broke

Trist's standing order: **every time I (Claude) break something, mess something up,
ship a bad fix, or hand him bad instructions, log it here**. Then read this file
before every change, build, commit, push, or "this is fixed" claim in this repo —
no exceptions. The file grows, never shrinks.

The point isn't punishment, it's preventing repeats. Each entry must answer:
1. **What happened** — concrete symptom Trist or a user saw.
2. **Why it happened** — the actual root cause, not the surface explanation.
3. **How to never repeat it** — the rule, check, or habit that catches it next time.

---

## 1. Told Trist to type `/xlrestart` — shut his game down

**Date:** 2026-06-15

**What:** After deploying a DamageMeter rebuild I told Trist "type `/xlrestart`
DamageMeter" so the new DLL would load. The command tore the whole game/Dalamud
session down rather than reloading just the dev plugin.

**Why:** I treated `/xlrestart` like a hot-reload command without verifying its
actual scope. There is already global guidance to prefer per-plugin reload
mechanisms; I ignored it and reached for the heaviest hammer.

**Rule going forward** (also written into `~/.claude/CLAUDE.md` and project
memory): **never** instruct Trist to type `/xlrestart`. To reload a dev plugin
after a rebuild, suggest in order:
1. Wait — Dalamud often hot-reloads dev plugins on DLL change.
2. `/xlplugins` → find the plugin → Disable, then Enable.
3. `/xldev` Plugins panel → per-plugin reload button.
4. If a full Dalamud restart is genuinely required, ask Trist to close and
   reopen XIVLauncher himself; do not write the command in chat.

---

## 2. v0.2.7 FlyText dedup window 350 ms — swallowed bard DoT ticks

**Date:** shipped 2026-06-10, diagnosed 2026-06-13/15.

**What:** Bard's Caustic Bite and Stormbite DoT ticks were under-counted ~97%.
A 31-minute summary session showed 19 confirmed DoT applications (~285 expected
ticks) but only 7 captured.

**Why:** The value-based FlyText dedup buffer pushed every ActionEffect damage
value (direct hits, AoEs, everything) and used a 350 ms expiry window. Two
compounding mistakes:
- The buffer was polluted with direct-hit values that fire `Damage*` FlyText
  the plugin doesn't consume, so those entries sat un-dedup'd and collided
  with later DoT-tick values.
- 350 ms was way too short. Combat-log capture proved FlyText for damage
  numbers consistently arrives 600–1000 ms behind the originating ActionEffect
  because the game queues them visually.

**Rule going forward:**
- When dedup or matching ActionEffect ↔ FlyText events, **measure the actual
  inter-event delay in a live combat log first**. Don't guess at window sizes.
- Any buffer used for kind-X dedup must only contain values from events that
  produce kind-X FlyText. Pushing "all damage" into a buffer consumed only by
  `AutoAttackOrDot*` is a scoping bug regardless of window length.

---

## 3. Silently dropped any ActionEffect that arrived before `InCombat` flipped

**Date:** shipped pre-0.2.10, diagnosed 2026-06-15.

**What:** Bard's opening Stormbite (instant cast, ~11k initial + DoT
application) was completely absent from the meter even though ACT, the in-game
combat log, and the game UI all showed it landed. Caustic Bite cast a moment
later was captured correctly.

**Why:** `OnReceiveActionEffect` only processed effects when
`ActiveSession != null`, and `ActiveSession` was only created from the
framework tick when `ConditionFlag.InCombat` flipped true. The flag fires a
few hundred ms after the first damage exchange, which is long enough to lose
an instant-cast pre-pull. The packet arrived, found no session, and was
dropped without a log line.

**Rule going forward:**
- For any event that should always be captured if it involves the local
  player, **the hook itself decides whether to start a session**, not a
  framework-tick condition poll. ConditionFlag.InCombat is a useful coarse
  signal but never the only one.
- When a user says "X is missing entirely" and the tooling shows X happened,
  the first suspect is a silent drop at a hook entry guard. Add a log line
  there before assuming the issue is downstream.

---

## 4. Off-by-one in `EffectKind` (14/15/16) — masked DoT application detection

**Date:** shipped 0.2.6 accuracy pass, diagnosed 2026-06-15.

**What:** Per-DoT attribution required detecting `ApplyStatusEffectTarget` to
know when the local player applied a DoT to a target. The enum had
`GpGain=14, ApplyStatusEffectTarget=15, ApplyStatusEffectSource=16` —
shifted by one from the actual byte values. Bards have no GP gauge, so
slot-1 entries on Caustic Bite (which the combat log showed as `kind=14,
val=1200`) couldn't possibly be GpGain; 1200 is the Caustic Bite status id
and the kind has to be ApplyStatusEffectTarget.

**Why:** The 0.2.6 pass rewrote 3 / 4 / 5 / 6 from a previous wrong layout
and got those right, but kept the wrong values for 14–16. Cross-checking
stopped at the kinds we actively used in the switch statement.

**Rule going forward:**
- When fixing an enum, ground-truth **every byte value the codebase touches
  AND every value adjacent to it**. The next reader assumes the whole table
  is right, not just the cell that was hot.
- Cross-reference against `FFXIV_ACT_Plugin/decompiled/parse/.../EffectEntryType.cs`
  — it's the authoritative reference and is checked into this repo.

---

## 5. Lowercased the session ID before lookup in `/history/{id}` HTTP route

**Date:** present in current StatusApi.cs as of 2026-06-15. Not yet shipped a
fix; logging here so I don't pretend `/history/{id}` works during diagnostics.

**What:** `GET http://localhost:17778/history/Thavnair_2026-06-15_08-14-05`
returns `{"error":"session not found"}` even though the session exists.

**Why:** `StatusApi.HandleRequest` does
`path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()`. The
session IDs stored in `Tracker.Store` keep original casing (e.g.
`Thavnair_…` with a capital T), and `FindSession` compares with `==`. The
lookup always misses on mixed-case IDs.

**Rule going forward:**
- Don't lowercase the entire URL when only the route prefix needs to be
  case-insensitive — match the route segment, then preserve original casing
  for parameter segments.
- When a diagnostic API "doesn't work" during debugging, check the API
  parsing before re-investigating the data — it cost me a few rounds here.

---

## 6. Used `IFlyTextGui` as the DoT-tick data source

**Date:** entered the codebase in 0.2.6, diagnosed 2026-06-15.

**What:** Bard DoTs (Stormbite, Caustic Bite) consistently went missing or
under-counted. After fixing the dedup buffer + window in 0.2.10, a clean
11-second test fight still recorded **zero** DoT damage — and yet ACT showed
3 Stormbite + 2 Caustic Bite ticks for the same fight, and FFXIV's in-game
combat log also showed the DoT activity. The combat-log JSONL I added for
diagnostics captured every FlyText event the plugin received: not one had a
value or kind consistent with a DoT tick. So the ticks were definitely
landing — the plugin just wasn't seeing them.

**Why:** `IFlyTextGui.FlyTextCreated` only fires when FFXIV actually renders
the floating number popup above a target. Whether that popup renders is
gated by the user's in-game Pop-up Text settings (System Configuration →
Character Configuration → Log Window → Pop-up Text). If "continuous damage"
text is disabled, the floating number never appears and the Dalamud event
never fires — *but the damage still happens server-side and still shows up
in the combat log and in ACT (which reads packets directly)*. So FlyText is
"the things the player can see floating on screen," not "everything that
deals damage." Treating it as a damage source was structurally wrong.

I then doubled down by telling Trist "your pop-up setting is off" — but
that's just the symptom. The fix isn't to push his game settings around; the
fix is to stop using a display-layer event for combat-data capture.

**Rule going forward:**
- **Never use `IFlyTextGui` as a *source of truth* for combat events.** It's
  a UI signal. Use it only for things that are inherently UI ("did the
  player see this popup"), never for "did this damage happen."
- **For combat data the user can verify from their in-game combat log,
  capture it from the same path FFXIV uses for the combat log** —
  `IChatGui.ChatMessage` with the combat XivChatType variants, or hook the
  EffectResult network packet, or port the DoTSimulator (compute ticks from
  status applications + caster stats, FFXIV_ACT_Plugin's approach). FlyText
  is downstream of all of these and can be silently filtered by client
  settings.
- **When a user shows the in-game combat log as proof of activity the
  plugin missed, that's a near-certain sign the plugin is reading from the
  wrong layer.** Don't argue the user's game settings — find the canonical
  data source.

---

## 7. Per-DoT FlyText attribution credited misaligned values to live DoTs

**Date:** shipped 2026-06-15 (the same commit that added per-DoT attribution),
diagnosed 2026-06-15.

**What:** Trist's 13-second test pull showed Stormbite total = 20,958 and
Caustic Bite total = 20,027 in the meter, both higher than reality. ACT
showed Stormbite initial = 11,131 (one direct hit) and Caustic Bite initial
= 13,791 (one direct hit). The extra 9,827 on Stormbite and 6,236 on Caustic
Bite were the user's Shot crit and a plain Shot — auto-attacks whose
ActionEffect arrived at t=1,898 ms and t=4,942 ms, but whose FlyText events
didn't fire until t=6,509 ms. The 2,000 ms dedup window already expired so
the unmatched FlyTexts cascaded into `PickActiveDotForTick`, which happily
credited them to the oldest active DoT.

**Why:** Two compounding bad assumptions on my part:

- I treated "FlyText dedup missed" as equivalent to "this must be a DoT
  tick." That conflation is the bug. FlyText for *any* damage can be queued
  arbitrarily long behind earlier popups (the visual stagger is unbounded
  in busy combat). An unmatched damage FlyText could be a DoT tick, a
  late-arriving auto-attack popup, a late ability hit, an enemy's hit on
  you — anything. Without source/target IDs on the event, we cannot tell.
- I then routed these "DoT ticks" through `PickActiveDotForTick`, which
  always returns *some* active DoT if any are running. The attribution
  pipeline had no "this looks wrong, drop it" exit. Any leaked value found
  a home.

**Rule going forward:**
- **A fallback that always succeeds is not a fallback, it's a bug
  generator.** Per-DoT attribution must be able to say "I don't know whose
  tick this is, discard." When the source signal can't be trusted, *don't
  credit anything*.
- **FlyText-based DoT credit is now disabled in code.** Unmatched damage
  FlyTexts are dropped silently. DoT capture moves to the chat-log path
  exclusively (or, if that proves insufficient, to DoTSimulator).
- **When fixing one mis-credit problem, double-check you haven't built a
  worse mis-credit pipeline downstream.** v0.2.10 widened the dedup window
  to fix v0.2.9's inflation, then immediately added a per-DoT fallback
  that re-introduced inflation in a different shape. The next reviewer
  (future-me) should ask "what does this do when the input is wrong?"
