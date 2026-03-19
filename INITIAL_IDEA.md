# DamageMeter — Initial Design Brief

Author: Trist (Sansflaire)
Date: 2026-03-19
Status: Captured from initial user conversation

---

## Core Concept

A real-time damage meter plugin for FFXIV, similar to ACT/FFLogs but running natively
inside Dalamud. Reads the in-game combat log (via ActionEffect hook) and displays
everyone's contribution as colored bars that update live during a fight.

The visual reference was a crude mockup showing:

```
┌─────────────────────────────────────────────────────────┐
│ DAMAGE METER                                        [✓] │
├──────────────────────────────────────────────────────── ┤
│ [Tank icon]  Tank Manchild@Goblin             43%  [███]│
│ [DPS icon]   Barbarosa Quee@Genova            20%  [██] │
│ [DPS icon]   Biscuit Jones@Goblin             32%  [███]│
│ [Range icon] Terrence Loserface@Zalera        05%  [█]  │
└─────────────────────────────────────────────────────────┘
```

Left side: job icon + player name + @server
Right side: percentage of group total (and optionally full value)
Bar: color-coded fill proportional to that player's share

---

## Meter Types (Dropdown)

| Label | What It Tracks |
|-------|---------------|
| Damage Dealt | Total raw damage over the entire fight |
| DPS | Damage per second (Total ÷ fight duration) |
| Healing Done | Total HP restored to others |
| HPS | Healing per second |
| Overhealing | Healing wasted on already-full targets |
| Damage Taken | Total damage received by each player |
| Avoidable Damage Taken | Damage taken that "could have been avoided" |

### Avoidable Damage Taken — Definition

User's original idea: "simple math would be, 'if gained vuln, bad'?"
Interpretation: a proxy metric. Initial implementation = damage from AoE actions
that simultaneously hit 3+ targets (likely avoidable cleaves/ground effects).

Future improvement could detect vulnerability-up applications to more precisely
flag damage from mechanic failures.

---

## Bar Colors (Default)

| Meter | Color |
|-------|-------|
| Damage Dealt / DPS | Red |
| Healing Done / HPS | Green |
| Overhealing | Yellow-green |
| Damage Taken | Blue |
| Avoidable Damage Taken | Orange |

All colors are fully configurable per-type in Settings.

---

## Party Tracking Scope

- **4-player light party**: Full party shown in one group
- **8-player full party**: Full party in one group
- **24-player alliance raid**: Players grouped as "My Party" vs "Alliance" with
  collapsible group headers. Main party members are determined from Dalamud's
  IPartyList; all others are grouped under Alliance.

---

## Combat Session Lifecycle

1. `ICondition[InCombat]` transitions false → true: new session starts
2. All ActionEffect hook data is attributed to the active session
3. `ICondition[InCombat]` transitions true → false: session ends
4. Sessions < 3 seconds or with 0 combatants are discarded (wipes, accidents)
5. Valid session is appended to temp history and persisted to `sessions.json`

---

## Session History

### Temporary (auto)
- Up to 20 sessions stored automatically (configurable in settings)
- Oldest is deleted when the limit is exceeded
- Named: `ZoneName_yyyy-MM-dd_HH-mm-ss`

### Saved (manual)
- User can click "Save" on any temp session to move it to permanent storage
- Permanent sessions are never auto-pruned
- Stored alongside temp sessions in `sessions.json`

### Loading
- History window: list of all sessions, click "View" to pin it to main meter view
- "Unpin" returns the main window to live/most-recent display

---

## Settings

### Display
- Show full numeric value (toggle)
- Show % of group total (toggle)
- Show @Server suffix (toggle)
- Full name / Initials only (radio)
- Show job icon (toggle)
- Row height (slider, 16–40px)

### Bar Colors
- Per-meter-type color picker (with alpha)

### Window
- Style preset: Classic / Minimal / Modern
- Opacity slider
- Lock window (prevent accidental moves)

### History
- Max recent sessions (slider, 1–50)
- Clear all recent sessions button

---

## Technical Notes

- Hook: `ActionEffectHandler.Addresses.Receive.Value` (same function as AutoReactFFXIV's SpiteDetector)
- Effect layout: 8 bytes per entry — Type(1), Params(5), Flags(1), Value(1)
- Extended damage: `value | (param3 << 16)` when `flags & 0x40`
- Overheal: computed by comparing heal amount to target's missing HP via IObjectTable
- Commands: `/dm`, `/dmhistory`, `/dmsettings`
- Persistence: JSON in Dalamud plugin config directory

---

## Future Ideas (Not Implemented)

- Per-ability breakdown (expand a row to see which skills did the most damage)
- Timeline graph view (damage curve over fight duration)
- Export to clipboard (FFLogs-compatible format)
- Overlay mode (smaller, transparent, non-interactive)
- Pet/minion damage attribution to owner
- Precise "avoidable damage" via vulnerability stack detection
- DPS phases (ignore pre-pull, split by boss phase)
