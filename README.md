# DamageMeter

A real-time damage meter plugin for Final Fantasy XIV, built on the Dalamud plugin framework.

Tracks damage, healing, and other combat metrics live during any fight — solo, light party, full party, or alliance raid — and stores your past sessions for review.

---

## Features

### Live Meter View
- Colored bars updated in real time from the in-game combat log
- Sorted by selected metric (highest first)
- Job icon + player name + @server on the left; value and % on the right
- Hover any row for a full stat breakdown tooltip

### Meter Types (dropdown in window)
| Type | Description |
|------|-------------|
| Damage Dealt | Total damage over the fight |
| DPS | Damage per second (total ÷ duration) |
| Healing Done | Total HP restored |
| HPS | Healing per second |
| Overhealing | Healing wasted on full-HP targets |
| Damage Taken | Total damage received |
| Avoidable Dmg Taken | Damage from AoE hits that could have been avoided |

### Bar Colors
Red for damage, green for healing, blue for damage taken — all fully customizable per type.

### Content Scaling
- 4 / 8-man parties: single flat list
- 24-man alliance raids: collapsible "My Party" and "Alliance" groups

### Session History
- Up to 20 recent sessions stored automatically (configurable)
- Oldest session is pruned when the limit is reached
- **Manual save**: click Save in the History window to keep a session permanently
- Load any past session into the meter view with one click
- Sessions named `ZoneName_yyyy-MM-dd_HH-mm-ss`

### Settings
- **Display**: full name / initials, @server suffix, job icons, row height
- **Values**: show raw number, show %, or both
- **Bar Colors**: per-type color picker with alpha
- **Window**: style preset (Classic / Minimal / Modern), opacity, lock position
- **History**: max temp session count, clear all

---

## Commands

| Command | Action |
|---------|--------|
| `/dm` | Toggle main meter window |
| `/dmhistory` | Open session history |
| `/dmsettings` | Open settings |

---

## Installation

This plugin is distributed as a custom Dalamud plugin.

1. In-game: `/xlsettings` → **Experimental** → Custom Plugin Repositories
2. Add the repo URL (see Releases page)
3. `/xlplugins` → search "DamageMeter" → Install

Or for local dev builds:
1. `dotnet build` in `src/`
2. `/xlsettings` → Experimental → Dev Plugin Locations → add the DLL path

---

## Technical Notes

- Uses Dalamud API Level 14, targeting .NET 10 / x64
- Hooks `ActionEffectHandler.Addresses.Receive` to capture all action effects
- Session data stored as JSON in the Dalamud plugin config directory
- No third-party dependencies beyond what Dalamud provides

---

## Author

**Sansflaire**

---

## Disclaimer

For entertainment and informational use only. DPS meters can cause social friction — be mindful of how you use this data in group content.
