# DamageMeter — Technical Research Reference

## DIRECTIVE

**This document is the authoritative knowledge base for every technical concept in this plugin.**

Rules for this document:
1. **Every unique concept in the codebase must have an entry here.** If a new type, API, or game mechanic is introduced, add it immediately.
2. **Entries must be complete.** Each section must explain: what it is, how it works internally, every parameter, every gotcha, and how we specifically use it in DamageMeter.
3. **This document is kept current.** When a bug is fixed because we learned something new about a concept, update the relevant section here.
4. **When in doubt, research it.** Do not guess. If the exact behavior of a type or packet field is unclear, perform web research and fill it in.
5. **The EffectKind table is the most critical section.** Every byte value we encounter in game packets must be explained. When `/xllog` shows a new kind value, research it immediately and add it here.

---

## TABLE OF CONTENTS

1. [FFXIV Packet Layer — ActionEffect](#1-ffxiv-packet-layer--actioneffect)
2. [FFXIVClientStructs Types](#2-ffxivClientStructs-types)
3. [Dalamud API Services](#3-dalamud-api-services)
4. [Lumina Excel Data](#4-lumina-excel-data)
5. [SkiaSharp Rendering](#5-skiasharp-rendering)
6. [ImGui Integration](#6-imgui-integration)
7. [DamageMeter Data Model](#7-damagemeter-data-model)
8. [FFXIV Game Concepts](#8-ffxiv-game-concepts)
9. [Known Unknowns / Open Questions](#9-known-unknowns--open-questions)

---

## 1. FFXIV Packet Layer — ActionEffect

### 1.1 `ReceiveActionEffect` / `ActionEffectHandler`

**What it is:** The server-to-client function the game calls whenever it processes the result of any action. This is the single choke point for all combat events: damage, healing, buffs, debuffs, status applications, misses, etc.

**How we hook it:**
```csharp
var addr = ActionEffectHandler.Addresses.Receive.Value;
_hook = gameInterop.HookFromAddress<ReceiveActionEffectDelegate>((nint)addr, OnReceiveActionEffect);
_hook.Enable();
```
`ActionEffectHandler.Addresses.Receive` is a static address resolved by FFXIVClientStructs from a signature scan. It points to the native function inside the game binary.

**Hook delegate signature:**
```csharp
unsafe delegate void ReceiveActionEffectDelegate(
    uint                               casterEntityId,   // raw uint entity ID of who used the ability
    Character*                         casterPtr,        // native Character struct pointer (may be null)
    Vector3*                           targetPos,        // AoE center position (may be null for single-target)
    ActionEffectHandler.Header*        header,           // action metadata
    ActionEffectHandler.TargetEffects* effects,          // effects array (one TargetEffects per target)
    GameObjectId*                      targetEntityIds); // array of target entity IDs
```

**CRITICAL:** Call `_hook!.Original(...)` in a `finally` block always. Never skip. Skipping crashes the game.

**When it fires:** Every time the server sends an action result packet. This covers:
- Player abilities and weaponskills
- Auto-attacks
- NPC/enemy attacks
- DoT ticks
- Healing abilities
- Buff/debuff applications (these have EffectKind values we may not handle yet)
- Ground AoE pulses

**Does NOT fire for:**
- HP recovery from items in menu (uses different packet)
- Passive HP regen ticks (separate regen tick packet)
- Fall damage (territory-level environment damage)

---

### 1.2 `ActionEffectHandler.Header`

```
Header fields (accessed as Header*):
  header->ActionId    — uint, the game action ID (maps to Lumina Action sheet)
  header->NumTargets  — byte or ushort, how many targets were hit (max 32 in practice)
  header->ActionType  — ActionType enum (1 = Action, 2 = Item, etc.)
```

`ActionId` is the key for naming abilities via `IDataManager.GetExcelSheet<Action>().GetRow(actionId).Name`.

**Important:** AoE abilities that hit 32 targets still get `NumTargets = 32`. Abilities that hit nobody (whiff on empty ground) may have `NumTargets = 0` — we skip these.

---

### 1.3 `ActionEffectHandler.TargetEffects` — The Effects Array

The `effects` parameter points to a contiguous array of `TargetEffects` structs, one per target in order matching `targetEntityIds`.

**Memory layout:**
```
effects → [TargetEffects for target 0][TargetEffects for target 1]...
Each TargetEffects = 8 ActionEffect entries × 8 bytes each = 64 bytes
Total = NumTargets × 64 bytes
```

We access this as raw bytes:
```csharp
var effectsBase = (byte*)effects;
var targetEffBase = effectsBase + t * (EffectSize * EffectsPerTarget); // t * 64
var effPtr = targetEffBase + e * EffectSize;                           // + e * 8
```

**Stride verification:** The `64 bytes per target` assumption must be verified against FFXIVClientStructs after major patches. If this is wrong, all effect parsing is garbage.

---

### 1.4 The 8-Byte Effect Entry — Full Layout

Each effect entry is exactly 8 bytes. **Ground-truthed against three independent sources** (FFXIVClientStructs 7.51.0.8301 `ActionEffectHandler.Effect`, ravahn/FFXIV_ACT_Plugin 3.0.2.1 `EffectEntry`/`DamageEffectEntry`, perchbirdd/DamageInfoPlugin `EffectEntry` — all agree):

```
Byte [0]  = Type   (EffectKind below — the effect kind code)
Byte [1]  = Param0 (Damage: bit 0x20 = Critical, bit 0x40 = Direct Hit)
Byte [2]  = Param1 (Damage: low nibble = AttackType, high nibble = ElementType)
                   (Heal:   bit 0x20 = Critical — yes, heal crit lives at a different byte)
Byte [3]  = Param2 (combo amount / positional bonus)
Byte [4]  = Param3 (high word multiplier for extended values — adds Param3*65536)
Byte [5]  = Param4 (bit 0x40 = "extend Value with Param3<<16", bit 0x80 = SourceEntry)
Byte [6]  = Value  low byte  ┐ ushort Value at offset 6
Byte [7]  = Value  high byte ┘
```

**Value extraction (correct formula):**
```csharp
var param3 = effPtr[4];                       // high word multiplier
var param4 = effPtr[5];                       // flags
var value  = (long)*(ushort*)(effPtr + 6);    // ushort Value at bytes 6-7

if ((param4 & 0x40) != 0)
    value += (long)param3 << 16;              // damage > 65535 uses Param3 as high word
```

Max representable value: `65535 + (255 << 16)` = `16,776,960` — covers all in-game damage/heal numbers.

**⚠ HISTORICAL BUG (pre-2026-06-09):** Earlier builds of this file documented Byte [6] as the "Flags" byte and read the extension flag from it. **That was wrong.** Byte 6 is the low byte of Value. The extension flag is in Byte [5] (Param4). The previous "byte 6 dual use" note was a misunderstanding — when the low byte of Value happened to have bit 0x40 set (~50% of hits between 64 and 32768), the broken check triggered a spurious `Param3 * 65536` extension, multiplying many hits by ~65,536. Fixed in 0.2.6.

**Crit / Direct-Hit flags (for `EffectKind.Damage`):**
```csharp
bool isCritical  = (param0 & 0x20) != 0;
bool isDirectHit = (param0 & 0x40) != 0;
```

**Crit flag for `EffectKind.Heal`** lives at a different byte — `(Param1 & 0x20) != 0`. Verified from Ravahn's `HealEffectEntry.IsCritical` getter, and confirmed by perchbirdd treating Heal entries with `dmgType = DamageType.None` (so no DamageType decoding from Param1).

**SourceEntry bit (Param4 & 0x80):** when set, the effect's true target is the action's *source*, not the `targetEntityIds[t]` slot. Used for "heal-yourself-as-a-side-effect" actions (e.g. Bloodbath, Equilibrium). DamageMeter does not yet remap on this bit — heals attributed to the wrong actor when this fires are an outstanding refinement (see §9.6).

**Param0–Param4 semantics by EffectKind:** the table above is the verified core. Beyond that, Param0/Param1/Param2 carry effect-specific data — status effect IDs for status-apply entries, damage subtypes for damage entries, etc. Ravahn's `ParseEffectEntry` strategies are the authoritative cross-reference.

---

### 1.5 `EffectKind` — Authoritative Table

**CRITICAL SECTION.** Cross-validated against three independent sources: `FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler.Effect.Type`, `FFXIV_ACT_Plugin.Parse.EffectEntryType`, and `DamageInfoPlugin.ActionEffectType`. The pre-0.2.6 enum was almost entirely wrong — see §1.5b below.

**Authoritative byte values (`/xllog [DM]` will show byte in hex; match here):**

| Byte | Hex | Name | What it is |
|---:|---:|---|---|
| 0 | 0x00 | Nothing | Unused effect slot — pad in the 8-effect array |
| 1 | 0x01 | Miss | Attack missed |
| 2 | 0x02 | FullResist | Damage fully resisted / immune |
| **3** | **0x03** | **Damage** | **Standard ability damage hit. Count for DPS.** |
| **4** | **0x04** | **Heal** | **Standard heal. Count for HPS.** |
| **5** | **0x05** | **BlockedDamage** | **Damage after block — reduced amount got through. Count for DPS.** |
| **6** | **0x06** | **ParriedDamage** | **Damage after parry — reduced amount got through. Count for DPS.** |
| 7 | 0x07 | Invulnerable | Target invulnerable (Hallowed Ground, Living Dead). Zero damage applied. |
| 8 | 0x08 | NoEffectText | Effect with no visible number (interrupt prevention, etc.) |
| 9 | 0x09 | Unknown_0 | Reserved/unknown (perchbirdd) |
| 10 | 0x0A | MpLoss | MP drained from target |
| 11 | 0x0B | MpGain | MP restored. **Pre-0.2.6 misnamed this "OtherDamage" and counted it as damage** — that's the Lucid Dreaming inflated-DPS bug. |
| 12 | 0x0C | TpLoss | TP drained |
| 13 | 0x0D | TpGain | TP gained |
| 14 | 0x0E | GpGain | Gathering-points gained. **Pre-0.2.6 misnamed this "Heal"** — usually harmless because gathering doesn't fire in combat. |
| 15 | 0x0F | ApplyStatusEffectTarget | Status effect applied to target. Value = duration. |
| 16 | 0x10 | ApplyStatusEffectSource | Status effect applied to source (self-buff side effect of an action). |
| 20 | 0x14 | StatusNoEffect | Status couldn't be applied (immune, already had) |
| 27 | 0x1B | Unknown_0 | Reserved (perchbirdd) |
| 28 | 0x1C | Unknown_1 | Reserved (perchbirdd) |
| 33 | 0x21 | Knockback | Knockback effect |
| 40 | 0x28 | Mount | Mount action |
| 59 | 0x3B | VFX | Visual effect only |
| 61 | 0x3D | JobGauge | Job gauge update |

**Bytes 17–19, 21–26, 29–32, 34–39, 41–58, 60, 62+** are either reserved or specialized/rare. If `/xllog [DM]` shows one, look it up in `FFXIV_ACT_Plugin.Parse.EffectEntryType` first — it has the widest known-byte coverage.

**Damage tracking switch (what to count in TotalDamageDealt):**
- Bytes 3, 5, 6 = damage that landed in some form (full / blocked-partial / parried-partial)
- Byte 7 (Invulnerable) = explicitly *no* damage — exclude
- Byte 11 (MpGain) = mana, not damage — exclude (was wrongly included pre-0.2.6)

**Heal tracking (what to count in TotalHealingDone):**
- Byte 4 only. (Was wrongly looking at byte 14 = GpGain pre-0.2.6 → real heals were going to BlockedDamage by mistake.)

### 1.5b The 0.2.6 enum-value correction

Pre-0.2.6 had hand-rolled enum values that were guesses, and they were off:

| Symbol used in code | Pre-0.2.6 byte | Actual byte | What that pre-0.2.6 byte really is |
|---|---:|---:|---|
| `Damage` | 3 | 3 | Damage ✓ (only one that was right) |
| `BlockedDamage` | 4 | 5 | byte 4 was actually `Heal` |
| `ParriedDamage` | 5 | 6 | byte 5 was actually `BlockedDamage` |
| `Invulnerable` | 6 | 7 | byte 6 was actually `ParriedDamage` |
| `OtherDamage` | 11 | (removed) | byte 11 was actually `MpGain` |
| `Heal` | 14 | 4 | byte 14 was actually `GpGain` |

Real-world consequences this caused (which is what made the meter "totally inaccurate"):
- **Healers showed huge fake DPS.** Real heal effects (byte 4) hit the `BlockedDamage` case and were summed into `TotalDamageDealt`.
- **`TotalHealingDone` was almost never populated.** The code waited for byte 14, which only fires during gathering nodes.
- **Lucid Dreaming / Refresh / in-combat MP regen inflated DPS.** Byte 11 = MpGain entered the `OtherDamage` case.
- **All medium-magnitude hits had a ~50% chance of being multiplied by 65,536.** The flag byte was read from byte 6 (low byte of Value); whenever Value's low byte coincidentally had bit 6 set, the false-positive extension fired.

### 1.5c Updates for §9 (previously-open issues)

- **§9.1 Recuperate** — Recuperate is a PvP self-heal. With the correct `Heal = 4` mapping it should now route through the heal branch with `targetId == casterEntityId`. The existing self-hit filter in the damage branch (which prevented self-damage from counting) was the workaround for the pre-0.2.6 byte/enum mess; with byte/enum fixed, Recuperate's real bytes never enter the damage branch.
- **§9.2 Feint/True North in HealingDone** — these abilities don't produce real heal-typed effects. They produce `ApplyStatusEffectTarget` (byte 15) or status-related effects. The pre-0.2.6 misclassification was the cause: byte 14 (GpGain) firing for some unrelated reason was being counted as heal. With byte 14 no longer mapped to Heal, this should disappear.

---

### 1.6 `GameObjectId` vs `EntityId`

These are two different identifiers for the same game object:

**`EntityId` (uint, 32-bit):**
- The raw game-internal entity ID
- What we use for all tracking: `casterEntityId`, `targetEntityIds[t].ObjectId`
- Stable within a session for a given actor
- Used in `UseAction(ActionType.Action, actionId, (ulong)target.EntityId)`
- Source: `IGameObject.EntityId`, `targetEntityIds[t].ObjectId`

**`GameObjectId` (Dalamud/FFXIVClientStructs composite, 64-bit ulong):**
- Dalamud's higher-level wrapper: encodes `ObjectId` (low 32 bits) + content index (high bits)
- May differ from EntityId for some actors in instanced content
- Source: `IGameObject.GameObjectId`
- Do NOT use for `UseAction` — can silently fail

**Rule:** Always use `EntityId` (uint) for game interactions. `GameObjectId` is a Dalamud abstraction.

---

## 2. FFXIVClientStructs Types

### 2.1 `Character*` / `CharacterData`

Native struct pointer to the game's Character object. Passed as the second parameter of the ActionEffect hook.

```csharp
charPtr->CharacterData.ClassJob  // byte — current class/job ID
```

ClassJob is the FFXIV job ID:
- 1 = GLA (Gladiator), 2 = PGL, 3 = MRD, 4 = LNC, 5 = ARC, 6 = CNJ, 7 = THM
- 8 = CRP, 9 = BSM, 10 = ARM, 11 = GSM, 12 = LTW, 13 = WVR, 14 = ALC, 15 = CUL
- 16 = MIN, 17 = BTN, 18 = FSH
- 19 = PLD, 20 = MNK, 21 = WAR, 22 = DRG, 23 = BRD, 24 = WHM, 25 = BLM
- 26 = ACN, 27 = SMN, 28 = SCH, 29 = ROG, 30 = NIN, 31 = MCH, 32 = DRK
- 33 = AST, 34 = SAM, 35 = RDM, 36 = BLU, 37 = GNB, 38 = DNC, 39 = RPR
- 40 = SGE, 41 = VPR, 42 = PCT

**Job icon path:** `ui/icon/062000/06{2000+classJobId:D4}_hr1.tex`
So classJobId=19 (PLD) → `ui/icon/062000/062019_hr1.tex`

**Important:** `casterPtr` can be null — always null-check before dereferencing.

---

### 2.2 `ActionEffectHandler.Addresses.Receive`

A static field in FFXIVClientStructs that holds the resolved native address of the `ReceiveActionEffect` function. It's resolved via signature scanning at game startup by FFXIVClientStructs.

After major game patches, this signature may change and FFXIVClientStructs must be updated (and thus Dalamud, and thus plugins) before the hook works again.

**What it hooks:** The function called by the game's network packet handler when it processes a `0x029F` (or similar, varies by patch) action effect packet from the server.

---

## 3. Dalamud API Services

### 3.1 `IGameInteropProvider` / `Hook<T>`

**What it is:** Dalamud's managed wrapper around Reloaded (primary) / MinHook (fallback) for detouring native game functions. A factory service — it creates hooks, it doesn't hold them.

**Factory methods:**
| Method | Use case |
|--------|----------|
| `HookFromAddress<T>(nint addr, T detour)` | Primary — hook by raw memory address |
| `HookFromSignature<T>(string sig, T detour)` | Hook by AoB signature scan |
| `HookFromFunctionPointerVariable<T>(nint, T)` | Rewrites a vtable cell, not the function |
| `InitializeFromAttributes(object self)` | Scans for `[Signature]`-decorated fields and fills them |

Default backend is `Reloaded`. Never use MinHook unless Reloaded fails and goatcorp says so.

**`Hook<T>` members:**
```csharp
IntPtr Address { get; }
T Original { get; }              // call-through to the real function
T OriginalDisposeSafe { get; }   // safe to call even after Dispose()
bool IsEnabled { get; }
bool IsDisposed { get; }
void Enable();    // start intercepting
void Disable();   // stop intercepting (hook stays installed)
void Dispose();   // uninstall hook; IsDisposed = true
```

**Thread safety:** The hook fires on the game's main thread. Never block. Keep detour bodies fast.

**CRITICAL rule:** Always call `Original()` in a `finally` block. An exception that bypasses Original will desync the game state and likely crash.

---

### 3.2 `IObjectTable`

**What it is:** The live list of all game objects currently loaded in the client. Equivalent to iterating the object array in memory.

**Important properties:**
- `IObjectTable.FirstOrDefault(o => o.EntityId == entityId)` — O(n) linear scan, max ~600 objects
- Objects are only present if they're within render/load range
- Out-of-range actors return null — this is normal
- Objects include: player characters, NPCs, monsters, furniture, AoE circles, etc.

**Types returned:**
- `IPlayerCharacter` — player entity (includes you and other players)
- `IBattleChara` — any battle entity (player + NPC + monster), has CurrentHp/MaxHp/ClassJob
- `IGameObject` — base type, all entities

**Confirmed lookup methods (from source):**
```csharp
IGameObject? SearchByEntityId(uint entityId)   // use this — matches raw uint from hook
IGameObject? SearchById(ulong gameObjectId)    // Dalamud composite ID — NOT the same value
IGameObject? this[int index]                   // by spawn table index
```

**0xE0000000 sentinel:** `EntityId == 0xE0000000` means the object is **not networked** (client-local: mounts, minions). Cannot be targeted via `UseAction`. Our `entityId == 0` check handles the null case; `0xE0000000` check would filter non-networked actors if needed.

**Always use `SearchByEntityId(uint)`** to match hook entity IDs. Never `SearchById(ulong)` — that takes Dalamud's composite ID which differs.

---

### 3.3 `IPartyList`

**What it is:** The current party/alliance membership as seen by the local client.

**Coverage:**
- Covers the local player's party group (up to 8 members in 8-man content, 24 in alliance raids)
- In alliance raids: ALL alliance members appear in IPartyList
- Solo: contains just the local player
- Cross-world party: members may or may not appear depending on connection state

**Key properties (confirmed from source):**
```csharp
int Length { get; }              // current member count
uint PartyLeaderIndex { get; }
bool IsAlliance { get; }         // TRUE when AllianceFlags > 0 in GroupManager
long PartyId { get; }
```

**Alliance auto-dispatch:** `this[int index]` automatically routes to `GetAllianceMemberAddress(index)` when `Length > 8`. You do NOT need special handling — `foreach` and indexer both work transparently for both party and alliance sizes. `IsAlliance` is the discriminator.

**`IPartyMember.GameObject`:** Calls `ObjectTable.SearchById(EntityId)` on every access — cache the result, don't call it repeatedly in a loop.

**Usage:**
```csharp
foreach (var member in _partyList)
    if (member.EntityId == entityId) return CombatantType.PartyMember;
```

**Limitation:** A player in alliance group B or C is still "PartyMember" by our classification because IPartyList includes all 24. We do not sub-classify by alliance group.

---

### 3.4 `IClientState`

**Key properties:**
```csharp
ushort TerritoryType { get; }     // current zone row ID
uint MapId { get; }
uint Instance { get; }            // instance number (1/2/3) when zone has copies
ClientLanguage ClientLanguage { get; }
bool IsLoggedIn { get; }
bool IsPvP { get; }               // ⭐ TRUE when in any PvP content
bool IsPvPExcludingDen { get; }   // TRUE in PvP, excludes Wolves' Den
bool IsGPosing { get; }
```

**⚠ `LocalPlayer` is DEPRECATED as of API 14.** Use `IObjectTable.LocalPlayer` instead. `IClientState.LocalPlayer` may be removed in future API versions.

**`IsPvP` — KEY FOR OUR CLASSIFICATION BUG:** When `IsPvP == true`, enemy players are `IPlayerCharacter` objects classified as `FriendlyPlayer` by our type system. Use `IsPvP` to switch classification logic: in PvP, players NOT in party are enemies, not friendlies.

**TerritoryChanged timing:** Fires after the zone load completes. Old zone objects are gone from IObjectTable by this point. New zone is active.

**Events:**
```csharp
event Action<ushort> TerritoryChanged;            // new TerritoryType — fires AFTER zone load, objects are live
event Action<ZoneInitEventArgs> ZoneInit;         // fires EARLIER than TerritoryChanged (zone initializing)
event Action Login;
event Action<uint> InstanceChanged;
event Action EnterPvP;
event Action LeavePvP;
event ClassJobChangeDelegate? ClassJobChanged;    // (uint classJobId) — fires for ANY job change
event LevelChangeDelegate? LevelChanged;          // (uint classJobId, uint level)
event Action<ContentFinderCondition> CfPop;       // duty finder pop
```

`TerritoryChanged` does NOT fire on first login — subscribe to `Login` for that.

---

### 3.5 `IFramework` / `Framework.Update`

**What it is:** The per-frame update event from Dalamud, ticked by the game's main loop.

**Key members:**
```csharp
event OnUpdateDelegate Update;     // fires every game tick
DateTime LastUpdate { get; }
TimeSpan UpdateDelta { get; }      // elapsed time since last tick — use for frame-accurate timers
bool IsInFrameworkUpdateThread { get; }

// Scheduling helpers:
Task Run(Action action)            // queue on framework thread
Task RunOnFrameworkThread(Action)  // run immediately if already on it, else queue
Task DelayTicks(long n)            // complete after N ticks, continuation on framework thread
```

**Tick rate:** Tied to game frame rate. NOT fixed — use `UpdateDelta` for elapsed time, not assume 60Hz.

**Thread:** Always fires on the game's main thread. All Dalamud service calls are safe here.

**Deadlock warning:** Inside a `Run` callback, do NOT call `.Wait()` or `.Result` on another Task that's also scheduled on the framework thread — deadlock. Use `Task.Run` to escape to thread pool.

**Usage in DamageMeter:** We subscribe `OnFrameworkUpdate` to detect `ConditionFlag.InCombat` transitions (not InCombat → InCombat starts a session; InCombat → not InCombat ends a session).

---

### 3.6 `IDataManager` / `GetExcelSheet<T>`

**What it is:** Access to FFXIV's Lumina Excel data (game data files, `.exd` format).

**Usage:**
```csharp
var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
var row   = sheet?.GetRow(actionId);
var name  = row?.Name.ToString();
```

**Row access (confirmed API):**
```csharp
var row  = sheet.GetRow(rowId);           // returns T (struct), THROWS if not found
var row  = sheet.GetRowOrDefault(rowId);  // returns T? (nullable), safe
bool has = sheet.HasRow(rowId);
```

**Performance:** O(1) row lookup. Cache results — we use `_actionNames` dictionary to avoid re-fetching. Sheets are lazy-loaded and cached internally by Lumina — calling `GetExcelSheet<T>()` repeatedly is cheap.

---

### 3.7 `ICondition` / `ConditionFlag`

**What it is:** A dictionary-like interface for checking game condition flags (bit flags set by the server).

**Usage:**
```csharp
bool inCombat = _condition[ConditionFlag.InCombat];
```

**Selected ConditionFlag values (confirmed from source):**

| Flag | Value | Notes |
|------|-------|-------|
| `InCombat` | **26** | Player in active combat |
| `Casting` | 27 | Currently casting |
| `Mounted` | 4 | On a mount |
| `Crafting` | 5 | In crafting session |
| `Gathering` | 6 | At gathering node |
| `BoundByDuty` | 34, 56 | In instanced content |
| `BetweenAreas` | 45, 51 | Zone transition in progress |
| `Jumping` | 48, 61 | Airborne |
| `WatchingCutscene` | 58 | |
| `Stealthed` | 46 | |
| `LoggingOut` | 53 | |

**`InCombat` (value 26) behavior:**
- Server-authoritative: ~5s delay after last combat action before it clears
- Strictly local player — does NOT set if only allies are in combat
- In instanced duty content, stays set for the full encounter phase
- Mirrors `Character.Struct->InCombat` at the FFXIVClientStructs layer

**Event-based alternative:** `ICondition.ConditionChange` event fires when any flag changes — `(ConditionFlag flag, bool value)`. Useful instead of polling on every framework tick.

---

### 3.8 `IPlayerCharacter`, `IBattleChara`, `IGameObject`

**Type hierarchy:**
```
IGameObject
    └── IBattleChara          (anything with HP: players, NPCs, monsters)
            └── IPlayerCharacter    (player characters specifically)
```

**`IGameObject`:**
- `EntityId` — uint, raw game entity ID
- `GameObjectId` — ulong, Dalamud composite ID
- `Name` — `SeString`, use `.TextValue` for plain string
- `Position` — `Vector3`
- `ObjectKind` — `ObjectKind` enum (Player, BattleNpc, EventNpc, etc.)

**`IBattleChara`:**
- Everything from IGameObject plus:
- `CurrentHp`, `MaxHp` — uint
- `CurrentMp`, `MaxMp` — uint
- `ClassJob` — `RowRef<ClassJob>`, use `.RowId` for the ID byte
- `StatusList` — active status effects

**`IPlayerCharacter`:**
- Everything from IBattleChara plus:
- `HomeWorld` — `RowRef<World>`, use `.Value.Name.ToString()` for world name
- `CurrentWorld` — current world if visiting via data center travel
- `OnlineStatus` — player's displayed status icon

**Full interface hierarchy (confirmed from source):**
```
IGameObject → ICharacter → IBattleChara → IPlayerCharacter
```

**ICharacter adds over IGameObject:** `CurrentHp/MaxHp`, `CurrentMp/MaxMp`, `CurrentGp/MaxGp`, `CurrentCp/MaxCp`, `ShieldPercentage`, `ClassJob` (RowRef), `Level`, `Customize[]`, `CompanyTag`, `StatusFlags`, `OnlineStatus`.

**IBattleChara adds:** `StatusList`, `IsCasting`, `IsCastInterruptible`, `CastActionId`, `CastTargetObjectId`, `CurrentCastTime`, `TotalCastTime`.

**IPlayerCharacter adds:** `HomeWorld` (RowRef\<World\>), `CurrentWorld` (RowRef\<World\>).

**ObjectKind enum values:** `Player`, `BattleNpc`, `EventNpc`, `Treasure`, `Aetheryte`, `GatheringPoint`, `EventObj`, `Mount`, `Companion`, `Retainer`, `Area`, `Housing`, `CardStand`, `Ornament`.

**Distinguishing enemy vs player:**
```csharp
if (obj is IPlayerCharacter) → player (check IPartyList for party membership)
if (obj is IBattleChara)     → NPC/monster (classified as Enemy)
```
**NOT correct for PvP** — in PvP, enemy players are `IPlayerCharacter` but are hostile. Use `IClientState.IsPvP` to switch to PvP classification mode where non-party `IPlayerCharacter` objects are treated as enemies.

---

### 3.9 `ITextureProvider` / `CreateFromRaw` / `IDalamudTextureWrap`

**What it is:** Dalamud's managed interface for creating and uploading GPU textures.

**`CreateFromRaw` usage:**
```csharp
var spec    = RawImageSpecification.Rgba32(width, height);
var texture = _texProvider.CreateFromRaw(spec, pixelBuffer.AsSpan(), "debug-name");
```

**`RawImageSpecification` static constructors (confirmed):**
```csharp
RawImageSpecification.Rgba32(w, h)  // DXGI_FORMAT_R8G8B8A8_UNORM — use with SkiaSharp Rgba8888
RawImageSpecification.Bgra32(w, h)  // DXGI_FORMAT_B8G8R8A8_UNORM — use for BGRA sources
RawImageSpecification.A8(w, h)      // DXGI_FORMAT_A8_UNORM — grayscale alpha only
```

**IMPORTANT:** SkiaSharp `SKColorType.Rgba8888` produces RGBA bytes → use `Rgba32`. BGRA sources (e.g. raw FFXIV tex files) → use `Bgra32`.

**Owned vs Shared textures:**
- `CreateFromRaw(...)` → owned `IDalamudTextureWrap` — **MUST `Dispose()`**
- `GetFromGame(path)` / `GetFromGameIcon(lookup)` → shared `ISharedImmediateTexture` — **no `Dispose()` needed** (no `RentAsync`)
- Use `GetFromGame` for job icons rather than `CreateFromRaw` — shared textures are cached, owned textures are not

**`IDalamudTextureWrap`:**
- `.Handle` — `ImTextureID` wrapping an `ID3D11ShaderResourceView*` — pass to `ImGui.Image()`
- Must `.Dispose()` when done — frees D3D11 GPU reference
- **DO NOT use `.Handle` after `Dispose()`** — dangling pointer, undefined behavior
- `CreateWrapSharingLowLevelResource()` — creates a second independently-disposable reference (AddRef semantics) for passing to another plugin or holding a second concurrent reference
- Creating a new texture every frame is expensive — only recreate when canvas size changes

**`ImTextureID`:** An opaque handle type for ImGui. Treat it as a cookie — just pass it to `ImGui.Image()`.

---

### 3.10 `IPluginLog`

**Log levels and visibility:**
- `_log.Debug(...)` — only visible in Dalamud's `/xllog` when "Debug" checkbox is enabled
- `_log.Info(...)` — visible in `/xllog` always; also appears in dalamud.log file
- `_log.Warning(...)` — visible in `/xllog`, shown in yellow
- `_log.Error(...)` — visible in `/xllog`, shown in red; also written to dalamud.log

**Levels (confirmed):**

| Method | Default visibility |
|--------|--------------------|
| `Verbose(...)` | Dev plugins: hidden by default — must set `MinimumLogLevel = Verbose` |
| `Debug(...)` | Dev plugins: **visible** in `/xllog` with Debug checkbox on |
| `Info(...)` | Always visible |
| `Warning(...)` | Always visible |
| `Error(...)` | Always visible |
| `Fatal(...)` | Always visible |

**⚠ Message templates, not string interpolation:**
```csharp
// CORRECT:
_log.Debug("Effect kind={Kind} action={Name} val={Value}", kind, name, value);
// WRONG (don't use $"..." with _log methods — still works but loses structured data):
_log.Debug($"Effect kind={kind} action={name} val={value}");
```

**Per-frame logging warning:** Even at `Debug` level, the message template is evaluated on every call. Do NOT use `Debug` in per-frame hot paths in release builds — it floods the log. Our `[DM]` effect logging is Debug level but only active when combat is happening.

**For DamageMeter debugging:** All `[DM]` effect logs use `_log.Debug(...)`. Enable Debug in `/xllog` to see them.

---

## 4. Lumina Excel Data

### 4.1 `Lumina.Excel.Sheets.Action`

The main FFXIV action/ability database sheet. Accessed by `ActionId` (uint).

**Key fields:**
```
Action row:
  .Name           — SeString, localised ability name
  .ActionCategory — RowRef<ActionCategory> — 2=Spell, 3=WeaponSkill, 4=Ability, 9=Item, etc.
  .TargetArea     — bool, true = ground-targeted AoE
  .Range          — byte, targeting range in yalms
  .EffectRange    — byte, AoE radius in yalms
  .CastTime       — ushort, cast time in 1/100s
  .Recast100ms    — ushort, recast time in 1/10s
  .MaxCharges     — byte
  .PrimaryCostType — EffectKind of the cost (MP, TP, etc.) -- NOTE: NOT the same EffectKind as packet
  .ClassJobCategory
```

**PvP actions have different IDs** from their PvE counterparts. A PvE Holy and PvP Holy are different action IDs. The names may be the same or differ slightly.

**Lookup pattern:**
```csharp
_dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(actionId).Name.ToString()
```

---

### 4.2 `Lumina.Excel.Sheets.TerritoryType`

Zone/territory database. Indexed by `IClientState.TerritoryType` (ushort).

**Key fields:**
```
TerritoryType row:
  .PlaceName       — RowRef<PlaceName>, use .Value.Name.ToString() for display name
  .ContentFinderCondition — RowRef, links to dungeon/raid info
  .TerritoryIntendedUse — byte, categorizes zone type (overworld, dungeon, pvp, housing, etc.)
  .WeatherRate     — weather zone
  .BgPath          — string, internal path to the zone geometry
```

**TerritoryIntendedUse values (partial):**
- 0 = Overworld
- 1 = Inn room
- 2 = Dungeon
- 3 = Alliance Raid
- 5 = Raid
- 6 = Trial
- 7 = Housing
- 19 = Pvp Crystalline Conflict
- 22 = Duel area

---

## 5. SkiaSharp Rendering

### 5.1 Overview — The DamageMeter Rendering Pipeline

```
Framework.Update tick
    → MeterCanvas.Render(groups, session, opts)
        → SkiaSharp draws to SKSurface (CPU memory, RGBA8888, premultiplied alpha)
        → RenderSurface.ReadPixels() copies pixels to byte[] buffer
        → TextureManager.Upload() creates IDalamudTextureWrap from raw pixels
        → MainWindow.Draw() calls ImGui.Image(texHandle, size, uv0, uv1) twice:
            - Header slice: uv0=(0,0), uv1=(1, headerH/texH)
            - Body slice: uv0=(0, scrollY/texH), uv1=(1, (scrollY+viewH)/texH)
```

This is a **CPU-rendered texture** approach. Skia draws everything to a CPU bitmap, which is then uploaded to the GPU as a texture each frame.

**Performance characteristics:**
- CPU rendering cost: O(rows) — scales linearly with party size
- GPU upload cost: proportional to texture size (width × height × 4 bytes)
- For a 300×400px canvas: 480KB per frame — acceptable
- The bottleneck is usually the GPU texture upload, not the Skia drawing

---

### 5.2 `SKSurface`

**Creation:**
```csharp
var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
var surface = SKSurface.Create(info);
```

**`SKColorType.Rgba8888`:** Each pixel is 4 bytes: Red, Green, Blue, Alpha — in that byte order.

**`SKAlphaType` — all values:**

| Value | Meaning | Use when |
|-------|---------|----------|
| `Premul` | RGB already multiplied by A | Standard for GPU textures — **what DamageMeter uses** |
| `Unpremul` | RGB and A independent | PNG loaded from disk before Skia processes it |
| `Opaque` | Alpha ignored, always 1.0 | Screenshots, fully opaque surfaces |

**Why premultiplied matters:** A semi-transparent red (255,0,0, alpha=128) in premultiplied form is (128,0,0,128), not (255,0,0,128). Failing to premultiply causes darkening/halos at transparent edges.

**Gotcha — manual pixel writes:** If you write raw pixel bytes into a bitmap (e.g., via GCHandle pinned) and the bytes use straight-alpha (R/G/B full values, A < 255), Skia treats them as already premultiplied → colors appear too bright. Either premultiply manually, or declare the source as `Unpremul` and let Skia convert.

**Reading pixels:**
```csharp
var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
surface.ReadPixels(info, handle.AddrOfPinnedObject(), width * 4, 0, 0);
handle.Free();
```
`GCHandle.Pinned` prevents the GC from moving the byte array while Skia writes to it. This is required because `ReadPixels` takes a raw pointer.

---

### 5.3 `SKCanvas`

The drawing context obtained from `_surface.Canvas`.

**Key drawing methods:**
```csharp
canvas.Clear(SKColors.Transparent);        // clear entire canvas
canvas.DrawRect(rect, paint);              // filled or stroked rectangle
canvas.DrawRoundRect(rect, rx, ry, paint); // rounded rectangle
canvas.DrawPath(path, paint);              // arbitrary path
canvas.DrawText(text, x, y, font, paint);  // text — y is BASELINE, not top of text
canvas.DrawBitmap(bitmap, dest, paint);    // image
canvas.Save() / Restore();                // save/restore clip/transform state
canvas.ClipRect(rect);                    // set clipping region
canvas.ClipRoundRect(rrect);              // clip to rounded rect
```

**Coordinate system:** (0,0) = top-left. X increases right, Y increases down. Same as screen space.

**Render order:** Drawn back-to-front. Last drawn is on top.

**`DrawText` baseline warning:** The `y` coordinate is the **text baseline**, NOT the top of the glyph. To vertically center text in a rect:
```csharp
font.GetFontMetrics(out var m);
float baselineY = rect.MidY - (m.Ascent + m.Descent) / 2f;
// m.Ascent is negative (above baseline), m.Descent is positive (below baseline)
```

**`Save` / `RestoreToCount`:** Canvas state (clip, transform) is a stack. `canvas.Save()` returns an int (save count). `canvas.RestoreToCount(n)` pops to that depth — safer than bare `canvas.Restore()` which only pops one level.

**`SaveLayer`:** Pushes a transparent offscreen buffer. Everything drawn after goes into that layer. On `RestoreToCount`, it composites at the paint's opacity. Used for per-node opacity effects:
```csharp
int save = canvas.SaveLayer(new SKPaint { Color = new SKColor(255,255,255,(byte)(opacity*255)) });
// ... draw children ...
canvas.RestoreToCount(save);
```
`SaveLayer` allocates an offscreen texture — use sparingly. Not for every node.

---

### 5.4 `SKPaint`

The brush/style object. Must be created once and reused — creating SKPaint per draw call is expensive.

**Key properties:**
```csharp
paint.Color      = new SKColor(r, g, b, a);  // solid fill color
paint.Style      = SKPaintStyle.Fill;         // Fill, Stroke, or StrokeAndFill
paint.StrokeWidth = 1f;
paint.IsAntialias = true;                     // always true for quality rendering
paint.Shader     = SKShader.CreateLinearGradient(...); // gradient (overrides Color)
paint.BlendMode  = SKBlendMode.SrcOver;       // normal compositing
```

**All key properties:**
```csharp
paint.Color        — solid fill color (ignored when Shader is set)
paint.Style        — Fill, Stroke, StrokeAndFill
paint.StrokeWidth  — 0 = hairline (1 device pixel), always set explicitly
paint.IsAntialias  — always true for UI rendering
paint.Shader       — gradient/pattern (overrides Color for fill)
paint.ImageFilter  — blur, shadow, etc.
paint.ColorFilter  — per-pixel color transform
paint.BlendMode    — SrcOver (normal), Screen, Multiply, Overlay, etc.
paint.PathEffect   — dashes, discrete, corner-rounding on paths
```

**Gotcha:** If `Shader` is set, `Color` is ignored for fill — but `Color.Alpha` still acts as overall opacity on the shader. If `Shader` is set and you want 50% opacity, set `Color = new SKColor(255,255,255,127)`.

**Gotcha:** After any gradient draw, explicitly `paint.Shader = null` — the paint holds a native reference and prevents GC if not cleared.

**Gotcha:** `ImageFilter` is also a native reference — null it after use or use a separate `using` paint.

**Reuse pattern:** DamageMeter uses a single `_p` paint field, modifying it before each draw call rather than creating new SKPaint instances.

---

### 5.5 `SKShader` — Gradients

**Linear gradient:**
```csharp
SKShader.CreateLinearGradient(
    start: new SKPoint(x0, y0),
    end:   new SKPoint(x1, y1),
    colors: new[] { colorA, colorB },
    colorPos: null,           // null = evenly distributed; or float[] for custom stops
    mode: SKShaderTileMode.Clamp  // Clamp, Repeat, Mirror
)
```

**Radial gradient:**
```csharp
SKShader.CreateRadialGradient(
    center: new SKPoint(cx, cy),
    radius: r,
    colors: new[] { colorCenter, colorEdge },
    colorPos: null,
    mode: SKShaderTileMode.Clamp
)
```

**`SKShaderTileMode` values:**
- `Clamp` — extends the last color infinitely beyond gradient bounds. Best for UI gradients.
- `Repeat` — tiles the gradient.
- `Mirror` — tiles alternating forward/backward.
- `Decal` — transparent outside the gradient bounds (useful when gradient must not bleed into neighboring content).

**`WithLocalMatrix`:** Applies a transform to the shader's coordinate space independently of the canvas transform. Returns a **new** shader — dispose both:
```csharp
using var scrolled = noiseShader.WithLocalMatrix(SKMatrix.CreateTranslation(dx, dy));
paint.Shader = scrolled;
```

**Shader disposal:** SKShader is disposable. When assigned to SKPaint, the paint holds a native reference. Always `paint.Shader = null` then dispose the shader when done. If using `using`, ensure the paint is done drawing before the `using` block closes.

**DamageMeter usage:** Linear gradients for the metallic card background (left warm copper → right dark cool). Radial gradient for the left glow bloom effect.

---

### 5.6 `SKColor`

**Constructor:** `new SKColor(r, g, b, a)` — bytes 0–255, RGBA order.

**Predefined:** `SKColors.Transparent`, `SKColors.White`, `SKColors.Black`, etc.

**WithOpacity:** `color.WithAlpha((byte)(255 * opacity))` — returns a new color with modified alpha.

**Important:** SKColor uses RGBA byte order in its constructor. When comparing to CSS hex colors (which are also RGBA), they match directly: `#FF4444FF` = `new SKColor(0xFF, 0x44, 0x44, 0xFF)`.

---

### 5.7 `SKTypeface` / `SKFont`

**Loading a typeface:**
```csharp
var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
var typeface = SKTypeface.FromFile("path/to/font.ttf");
```

**Creating a font for drawing:**
```csharp
var font = new SKFont(typeface, size: 14f);
font.Subpixel = true;   // subpixel rendering
font.Edging = SKFontEdging.SubpixelAntialias;
```

**Text measurement:**
```csharp
float width = font.MeasureText("Hello", out SKRect bounds);
// bounds.Width/Height give tight bounds; width is advance width
```

**Vertical centering formula (confirmed from PanacheUI):**
```csharp
font.GetFontMetrics(out var m);
// m.Ascent is NEGATIVE (above baseline), m.Descent is POSITIVE (below baseline)
float textH = m.Descent - m.Ascent;                  // total cap height
float baselineY = containerMidY - (m.Ascent + m.Descent) / 2f;
// equivalently: containerTop + (containerH - textH) / 2f - m.Ascent
```

**Gotcha:** `font.MeasureText(text)` returns the **advance width** (how far to move the cursor). The `out SKRect bounds` gives the tight glyph bounding box, which can be smaller than the advance and can extend above/below the baseline. Use advance width for layout, bounds for precise hit detection.

**⚠ Old API:** Avoid setting `paint.TextSize`, `paint.Typeface` directly — deprecated in SkiaSharp 2.x. Use the `canvas.DrawText(text, x, y, align, font, paint)` overload that takes an explicit `SKFont`.

**DamageMeter usage:** Default system typeface loaded at startup. Multiple sizes for different text elements (timer = 22pt, zone = 11pt, name = 13pt, value = 16pt).

---

### 5.8 `SKTextBlob`

A pre-built, cached text rendering object. More efficient than `DrawText` when text layout is expensive or the same string repeats across frames.

**Creation:**
```csharp
var blob = SKTextBlob.Create("text", font);
canvas.DrawText(blob, x, y, paint);
blob.Dispose(); // or using
```

**When to use:** If the string is static across many frames — the glyph shaping happens once at blob creation. For short Latin labels (<50 chars), the difference vs `DrawText` is unmeasurable. For paragraphs or complex unicode text with frame-stable content, `SKTextBlob` is worth it.

**When NOT to use:** When the value changes every frame (like live DPS numbers). Blob creation cost would negate any rendering savings.

**DamageMeter currently uses `DrawText` directly** — row values change every frame, so `SKTextBlob` is not worth caching.

---

### 5.9 CPU Surface Rendering — Performance Model

**Cost breakdown per frame:**

| Operation | Cost | Notes |
|-----------|------|-------|
| `SKSurface.Create(info)` | Allocates `W×H×4` bytes heap | Only pay this on resize — keep surface alive |
| `canvas.Clear(Transparent)` | `memset` of entire buffer | ~120µs for 400×300. Unavoidable. |
| `DrawRect` solid fill | Scales with filled pixel area | A 400×300 fill ≈ same as Clear |
| `DrawRect` gradient fill | ~2–3× solid fill | Shader evaluation per pixel |
| Blur (`SKImageFilter.CreateBlur`) | O(n × radius) | Most expensive — Gaussian blur is costly |
| `ReadPixels` | ~`memcpy(W×H×4)` | ~50–100µs for 400×300 |
| `CreateFromRaw` (texture upload) | D3D11 `UpdateSubresource` | ~50–200µs depending on GPU bus |

**Allocation rules:**
- **Reuse `SKPaint`** — PanacheUI uses 3 long-lived paints. `new SKPaint()` costs managed + native alloc.
- **Reuse `byte[]` buffer** — DamageMeter keeps `_pixelBuffer` across frames, reallocates only on size change.
- **Reuse `SKSurface`** — create once at window init, recreate only on resize.
- **Dispose shaders/filters** — they hold native memory. `using` every `SKShader` and `SKImageFilter`. GC finalizer eventually cleans up but doesn't know about native size → GC pressure is silent.
- **`SaveLayer` is expensive** — each one allocates an offscreen texture. Only use for nodes with fractional opacity or special blend modes. Never SaveLayer every node.

**GCHandle.Pinned pattern (ReadPixels):**
```csharp
var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
try {
    surface.ReadPixels(info, handle.AddrOfPinnedObject(), width * 4, 0, 0);
} finally {
    handle.Free();  // MUST be in finally — a leaked pin prevents GC heap compaction forever
}
```

---

## 6. ImGui Integration

### 6.1 `ImGui.Image()` — UV Slicing

**Signature:**
```csharp
ImGui.Image(ImTextureID textureId, Vector2 size, Vector2 uv0, Vector2 uv1, Vector4 tintCol, Vector4 borderCol)
```

**UV coordinates:**
- `(0, 0)` = top-left corner of the texture
- `(1, 1)` = bottom-right corner of the texture
- Fractional: `(0, 0.23)` = top-left to 23% down the texture

**UV slicing for header/body split:**
```csharp
// Texture is texH pixels tall total
float headerFrac = _headerH / _texH;   // e.g. 92/400 = 0.23

// Draw just the header portion:
ImGui.Image(handle, new Vector2(w, _headerH), new Vector2(0, 0), new Vector2(1, headerFrac));

// Draw body starting at scrollY:
float bodyStartFrac = (_headerH + _scrollY) / _texH;
float bodyEndFrac   = (_headerH + _scrollY + _bodyViewH) / _texH;
ImGui.Image(handle, new Vector2(w, _bodyViewH), new Vector2(0, bodyStartFrac), new Vector2(1, bodyEndFrac));
```

This means we render ONE full canvas texture (header + all rows) and display it in two `ImGui.Image` calls with different UV ranges.

---

### 6.2 `ImGui.SetCursorScreenPos` vs `SetCursorPos`

**`SetCursorScreenPos(Vector2 pos)`:** Sets cursor in **screen space** (absolute pixel position). This is used for placing invisible buttons at exact screen positions (e.g., row hit boxes).

**`SetCursorPos(Vector2 pos)`:** Sets cursor in **window space** (relative to window content region top-left).

**CRITICAL gotcha:** After `SetCursorScreenPos`, ImGui's internal layout cursor is moved to that screen position. Any subsequent ImGui widgets (including `ImGui.InvisibleButton`) will render starting from that position, and the layout cursor advances accordingly. If you don't restore the cursor after placing invisible hit-test buttons, subsequent widgets (like the toolbar) will render in the wrong place.

**Fix:** Always save and restore cursor position:
```csharp
var savedPos = ImGui.GetCursorScreenPos();
// ... place invisible buttons ...
ImGui.SetCursorScreenPos(savedPos);  // restore
```

**Draw list calls do NOT advance the layout cursor.** `GetWindowDrawList().AddRectFilled(...)` draws immediately but leaves the cursor where it was. Only actual ImGui items (Text, Button, Image, InvisibleButton, Dummy, etc.) advance the layout cursor. This means you can draw decorative backgrounds via the draw list without affecting where the next widget lands.

---

### 6.3 `ImGuiCond`

Controls when `SetNextWindow*` / `SetNextItem*` conditions apply:

| Value | Meaning |
|-------|---------|
| `Always` | Apply every frame, unconditionally |
| `Once` | Apply once (first frame ever), then never again |
| `FirstUseEver` | Apply once per imgui.ini key (first time window appears) |
| `Appearing` | Apply only when the window transitions from hidden to visible |

**For DamageMeter detail popup:** Use `ImGuiCond.Always` for `SetNextWindowSize` so the popup size doesn't drift. `Appearing` caused a collapse bug because the size was only set on first-frame and subsequent frames allowed it to shrink.

---

### 6.4 `ImGui.BeginTable` / `EndTable`

**Basic usage:**
```csharp
if (!ImGui.BeginTable("##id", columnCount, flags, outerSize)) return;
ImGui.TableSetupColumn("Ability", ImGuiTableColumnFlags.WidthStretch);
ImGui.TableSetupColumn("Hits",    ImGuiTableColumnFlags.WidthFixed, 40f);
ImGui.TableHeadersRow();

foreach (var row in data)
{
    ImGui.TableNextRow();
    ImGui.TableSetColumnIndex(0);
    ImGui.TextUnformatted(row.Name);
    ImGui.TableSetColumnIndex(1);
    ImGui.TextUnformatted(row.Hits.ToString());
}
ImGui.EndTable();
```

**`outerSize` parameter:** `new Vector2(0, height)` — 0 width = fill available; explicit height = fixed height table with scroll.

**Key flags:**
- `ImGuiTableFlags.ScrollY` — enables vertical scrolling within the table
- `ImGuiTableFlags.BordersOuter` — outer border
- `ImGuiTableFlags.BordersInnerH` — horizontal lines between rows
- `ImGuiTableFlags.RowBg` — alternating row background colors
- `ImGuiTableFlags.SortMulti` / `SortTristate` — multi-column sorting

---

### 6.5 `ImGuiWindowFlags`

Key flags for DamageMeter windows (confirmed values from decompiled DLL):

| Flag | Value | Effect |
|------|-------|--------|
| `NoTitleBar` | 0x01 | Removes the default ImGui title bar — PanacheUI header IS the chrome |
| `NoResize` | 0x02 | Prevents window resizing |
| `NoMove` | 0x04 | Prevents dragging (used when `LockWindow` is enabled) |
| `NoScrollbar` | 0x08 | Hides ImGui's default scrollbar (we implement our own) |
| `NoScrollWithMouse` | 0x10 | Prevents ImGui mouse-wheel scroll (we handle scroll ourselves) |
| `NoCollapse` | 0x20 | Disables double-click title bar collapse |
| `AlwaysAutoResize` | 0x40 | Window resizes to content every frame |
| `NoBackground` | 0x80 | Transparent window background |
| `NoMouseInputs` | 0x200 | Pass all mouse events through to game |
| `NoBringToFrontOnFocus` | 0x2000 | Window doesn't come to front on click |
| `NoDocking` | 0x200000 | |
| `NoDecoration` | 0x2B | = NoTitleBar \| NoResize \| NoScrollbar \| NoCollapse |
| `NoInputs` | 0xC0200 | = NoMouseInputs \| NoNav |

**PanacheUI standard flags:**
```csharp
ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
```

---

### 6.6 `ImDrawList` — Direct Rendering

Obtained via `ImGui.GetWindowDrawList()`. Renders at the window's Z-layer.

**Key methods:**
```csharp
dl.AddRectFilled(min, max, color);              // color is uint ABGR
dl.AddRectFilled(min, max, color, rounding);    // with corner radius
dl.AddImage(texId, min, max);                   // draw texture
dl.AddImage(texId, min, max, uv0, uv1, tint);  // with UV and tint
dl.AddText(font, size, pos, color, text);       // text with specific font
dl.AddLine(a, b, color, thickness);
dl.AddCircleFilled(center, radius, color);
```

**Color format:** `uint` in ABGR byte order (NOT ARGB). Use `ImGui.ColorConvertFloat4ToU32` or bit-shift manually: `((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r`.

**Z-order:** Items added later are drawn on top. Background before foreground.

---

### 6.7 `ImGui.InvisibleButton` — Hit-Test Pattern

```csharp
bool InvisibleButton(string strId, Vector2 size, ImGuiButtonFlags flags = None)
```

- Occupies `size` pixels at the current cursor position
- Returns `true` on left-click (default)
- Renders nothing — just occupies layout space and captures input
- Advances the layout cursor by `size`
- `ImGui.IsItemHovered()` after the call for hover state

**Pattern — hit region over an Image:**
```csharp
var imagePos = ImGui.GetCursorScreenPos();  // capture before Image
ImGui.Image(texHandle, imageSize);           // this advances cursor
ImGui.SetCursorScreenPos(imagePos + offset); // go back to sub-region
if (ImGui.InvisibleButton("##mybtn", buttonSize))
    HandleClick();
bool hovered = ImGui.IsItemHovered();
// restore cursor to after the image so the next widget lands correctly:
ImGui.SetCursorScreenPos(imagePos + new Vector2(0, imageSize.Y));
```

**Alternative (DamageMeter's actual approach):** Capture `imgOrigin` before `Image()`, then do manual rectangle math against `ImGui.GetMousePos()` after the body Image widget. This avoids cursor juggling when many hit regions exist.

---

## 7. DamageMeter Data Model

### 7.1 `MeterType` Enum

```csharp
DamageDealt          // total damage done (sum of all hits)
DPS                  // DamageDealt / fight duration in seconds
HealingDone          // total heals landed (excluding overheal)
HPS                  // HealingDone / fight duration
Overhealing          // total overheal (excess healing beyond target max HP)
DamageTaken          // total damage received by this combatant
AvoidableDamageTaken // damage taken from AoE actions (NumTargets >= 3, proxy for "should dodge")
```

---

### 7.2 `CombatantType` Enum

```csharp
PartyMember    // IPlayerCharacter AND EntityId is in IPartyList
FriendlyPlayer // IPlayerCharacter AND NOT in IPartyList (alliance, bystander, PvP opponent!)
Enemy          // IBattleChara but NOT IPlayerCharacter (NPC, monster, boss)
Unknown        // Not IBattleChara at all, or EntityId == 0
```

**Known limitation:** In PvP (duel, Crystalline Conflict), enemy players are `FriendlyPlayer` not `Enemy`. This causes our "only record DamageDealt vs Enemy" filter to drop all PvP damage. A proper PvP fix requires hostility detection beyond object type.

---

### 7.3 `WindowStyle` Enum

```csharp
Modern   // Full cards with gradient metallic background, job icon, rank, value, bar
Classic  // Simpler flat rows (same height as Modern but simpler rendering)
Minimal  // Single-line compact rows (MinRowH = 26px vs RowH = 66px)
```

---

### 7.4 `AbilityStats`

Per-ability tracking structure. Stored in `CombatantData.DamageByAbility`, `HealingByAbility`, `DamageTakenByAbility` dictionaries, keyed by `ActionId` (uint).

```csharp
ActionId      // the FFXIV action ID
Name          // localised action name (from Lumina at first encounter)
TotalAmount   // sum of all hit values
TotalOverheal // sum of overheal component (healing only)
Hits          // count of times this ability fired
MinHit        // smallest non-overkill hit (0 = not yet set)
MaxHit        // largest hit

// Computed:
Average         = TotalAmount / Hits
OverhealPercent = TotalOverheal / (TotalAmount + TotalOverheal) * 100
```

**`skipMin` / killing blow:** When a hit kills its target, the damage recorded is capped at the target's remaining HP, not the true hit value. We pass `skipMin: true` for killing blows so `MinHit` only reflects surviving hits.

---

### 7.5 `CombatantData`

All combat statistics for one entity over one session.

**EntityId assignment:** Set at first hook encounter. Remains stable for the session even if the object goes out of range in IObjectTable.

**Name/World:** Captured at first encounter. If the object is out of range at first encounter, Name may be empty.

**DamageEvents / HealingEvents:** `List<(long TickMs, long Amount)>` — timestamped event log for DPS/HPS curve rendering. `TickMs` = milliseconds since `CombatSession.StartTime`.

---

### 7.6 `CombatSession`

Represents one combat encounter (InCombat start → end).

**`IsSummary`:** True for auto-generated instance summaries (created on `TerritoryChanged`). Instance summaries aggregate all sessions from the current zone instance.

**`PullCount`:** Number of individual pulls merged into this summary. 0 for normal sessions.

**Session ID format:** `{ZoneName_sanitized}_{yyyy-MM-dd_HH-mm-ss}` — unique, stable, human-readable.

**Minimum duration:** Sessions shorter than 3 seconds are discarded (prevents junk from brief combat flags).

---

### 7.7 `SessionStore` / Persistence

**Storage:** `{pluginConfigDir}/sessions.json` — JSON serialized via Newtonsoft.Json.

```json
{
  "TempSessions": [...],   // up to Config.MaxTempHistory, oldest auto-pruned
  "SavedSessions": [...]   // manually saved, never pruned
}
```

**Save triggers:** End of each session, after each prune, after save/delete actions.

**Data integrity:** If sessions.json is corrupt, we catch the exception and start with empty store — no crash.

---

## 8. FFXIV Game Concepts

### 8.1 `ConditionFlag.InCombat`

The server-authoritative "in combat" state for the local player. Set when:
- You attack an enemy
- An enemy attacks you
- You use an action that initiates combat

Cleared by the server approximately 5 seconds after the last combat action that kept you in combat. During duty encounters (boss fights), may remain set until the phase ends regardless of actions.

**Does NOT trigger for:** Other party members entering combat without you being involved.

---

### 8.2 Job Icons

**Formula:** `62000 + classJobId`
- GLA (1) → icon 62001
- VPR (41) → icon 62041
- PCT (42) → icon 62042

**Lumina path:** `ui/icon/062000/06{classJobId + 2000:D4}_hr1.tex`
- High-res versions end in `_hr1.tex`
- Standard: `ui/icon/062000/062019.tex`
- HD: `ui/icon/062000/062019_hr1.tex`

**TexFile loading in this Dalamud version:**
```csharp
var tex = _dataManager.GetFile<Lumina.Data.Files.TexFile>($"ui/icon/062000/06{2000+classJobId:D4}_hr1.tex");
// tex.TextureBuffer.RawData — raw bytes (BGRA order for B8G8R8A8 format)
// Swap bytes [i] and [i+2] to convert BGRA→RGBA for SkiaSharp
```

---

### 8.3 Overheal Approximation

**Problem:** The packet gives us the heal amount but not the overheal directly.

**Approximation method (used in DamageMeter):**
```csharp
var missing   = (long)chara.MaxHp - (long)chara.CurrentHp;
var overheal  = missing <= 0 ? healValue : Math.Max(0, healValue - missing);
var actual    = healValue - overheal;
```

**Known inaccuracy:** The `CurrentHp` snapshot is taken when the hook fires — before the game applies the heal. But if the game already applied the heal before we read CurrentHp, `missing` will be 0 and everything counts as overheal. This approximation is directionally correct but not exact.

**Better approach (future):** Some parsers track HP change events or use a running HP model. Not currently implemented.

---

### 8.4 Avoidable Damage Taken

**Definition:** Damage from actions that hit 3 or more targets simultaneously (`NumTargets >= 3`).

**Rationale:** AoE abilities that damage many targets simultaneously are usually "ground AoEs" or "cleaves" that players are expected to dodge. Single-target hits (tankbuster, auto-attack) are expected to be taken.

**Limitation:** This includes cleave attacks that physically can't be avoided, and misses attacks that technically could be avoided if flagged as single-target. It's a rough proxy.

---

### 8.5 Instance Summary (TerritoryChanged aggregation)

When the player changes zone (`TerritoryChanged` fires), DamageMeter checks if 2+ sessions have been recorded since the player entered the current zone. If so, it creates a summary session merging all of them.

**Use case:** A dungeon with 3 boss pulls creates 3 individual sessions + 1 summary session showing total stats for the entire dungeon run.

**Merging:**
- Totals are summed
- Ability stats are merged (same ActionId → add hits/amounts, min/max tracked correctly)
- DamageEvents/HealingEvents have their TickMs offset by `(pull.StartTime - summary.StartTime).TotalMilliseconds` so they form a continuous timeline

---

## 9. Known Unknowns / Open Questions

These are gaps in our understanding that need to be resolved through game observation (via `/xllog [DM]` debug output).

### 9.1 Recuperate EffectKind — RESOLVED (0.2.6)

**Was:** Recuperate (PvP self-heal) appeared in Damage Dealt, suggesting it fired with the wrong EffectKind.

**Resolution:** This was a side effect of the §1.5b enum-value bug. Byte 4 is Heal, not BlockedDamage; with the correct mapping, Recuperate's heal effect routes through the heal branch. The self-hit filter (`targetId == casterEntityId`) was the pre-0.2.6 workaround and remains in place for any other self-targeted damage edge case, but is no longer load-bearing for Recuperate.

### 9.2 Feint / True North in Healing Done — RESOLVED (0.2.6)

**Was:** Feint and True North were appearing in HealingDone with small values.

**Resolution:** Pre-0.2.6 was reading `Heal = 14`, but byte 14 is actually `GpGain` (gathering points). Some unrelated effect occasionally produced byte 14 during combat and was being counted as a heal. With `Heal = 4` now correct, byte 14 is no longer in the Heal switch — these phantom heals stop appearing. If Feint or True North still show up in HealingDone after the fix, capture the `[DM] kind=X` log line and file a new sub-bug.

### 9.3 PvP Enemy Classification

**Problem:** In PvP content (duel, Crystalline Conflict), enemy players are `IPlayerCharacter` and classified as `FriendlyPlayer` — not `Enemy`. Our damage recording requires `!isSelfHit` only, which means PvP damage IS recorded as DamageDealt. But `DamageTaken` recording requires `casterData?.Type != CombatantType.PartyMember`, which also allows PvP attackers through.

**Status:** PvP damage dealt and taken are now recorded (as of self-hit-only filter fix). Verify with actual PvP session.

**Future improvement:** Use `IClientState.IsPvP` to switch classification mode — when true, non-party `IPlayerCharacter` objects = enemies. This would allow correct `CombatantType` assignment in PvP.

### 9.4 Full EffectKind Table — RESOLVED (0.2.6)

§1.5 is now ground-truthed from FFXIVClientStructs + Ravahn + perchbirdd. The bytes that matter for damage/heal/MP tracking are covered. Bytes beyond 16 are mostly status-effect bookkeeping, knockback, mounts, VFX — none affect damage accuracy. If a new byte appears in `[DM]` logs, look it up in `FFXIV_ACT_Plugin.Parse.EffectEntryType` (in `devPlugins/DamageMeter/FFXIV_ACT_Plugin/decompiled/parse/`).

### 9.5 History Window — View/Save/Delete Buttons Unresponsive

**Problem:** Clicking View, Save, Delete buttons in the History window does nothing.

**Status:** NOT YET INVESTIGATED. Likely an ImGui hit-test / cursor restore issue similar to the toolbar placement bug. Need to read HistoryWindow.cs button layout code.

### 9.6 SourceEntry Bit (Param4 & 0x80) — Not Yet Honored

**Problem:** When an effect has bit 0x80 set in `Param4` (byte 5), the effect's true target is the *source* of the action, not `targetEntityIds[t]`. This is how the game encodes "this action also heals/buffs me as a side effect" — e.g. Bloodbath, Equilibrium, some PvP abilities.

**Current behavior:** DamageMeter records the heal/damage against the wrong actor when this bit is set. Usually a small minority of total events.

**Fix sketch:** In the hook loop, after reading `param4`, check `(param4 & 0x80) != 0`. If set, swap `targetData` ↔ `casterData` for that one effect entry only. Refer to `ParseStrategyActionEffect.ReportActionEffect` in the decompiled parser for the canonical pattern (it does this remapping via `IsSourceEntry`).

### 9.7 DoT / HoT Ticks — PARTIALLY RESOLVED via FlyText (0.2.7) — DEDUP SCOPE FIX (0.2.9)

**Background:** DoT ticks don't come through `ActionEffectHandler.Receive`. We searched FFXIVClientStructs 7.51.0.8301 for an exposed `EffectResult` / status-tick hook and found none — there's no clean signature to attach to in CS today.

**Current implementation:** `CombatTracker.OnFlyTextCreated` subscribes to `IFlyTextGui.FlyTextCreated`. When `kind` is `AutoAttackOrDot{,Dh,Crit,CritDh}` (damage tick) or `Healing{,Crit}` (heal — used for both direct heal and HoT), we dedup against a short ring buffer that `ProcessEffects` fills from ActionEffect hits. Matched → it's the ActionEffect's own flytext, skip. Unmatched → it's a tick the hook didn't see, credit the local player under the "Damage over Time" / "Heal over Time" pseudo-ability bucket (action IDs `0xFFFF_FFFE` / `0xFFFF_FFFD`).

**0.2.9 fix — auto-attack-only dedup buffer:** the 0.2.7 buffer pushed *every* damage value through ProcessEffects, including direct-ability hits (Burst Shot, Apex Arrow, AoEs). Those hits fire `Damage*` FlyText which the plugin doesn't consume from this buffer, so the entries sat unconsumed for the full 350ms window and ate any DoT tick whose value happened to collide. Bard Stormbite/Caustic Bite were under-counted ~97% — a 31-minute summary showed 19 confirmed DoT applications (expected ~285 ticks) and only 7 captured. Fix: only push values from auto-attack actions (Lumina `ActionCategory == 1`) — those are the only ActionEffects that share `AutoAttackOrDot{*}` FlyText kind with DoT ticks. Direct-ability values no longer pollute the buffer, so DoT ticks pass through and get credited.

**Known limitations (still acceptable for v0.2.9):**
- **Party-member attribution is impossible from FlyText alone.** The event has no source / target entity IDs. Only the local player's DoTs are credited. Other players' DoT damage is still missing.
- **No per-DoT breakdown.** All DoT ticks are bucketed into one "Damage over Time" entry. We can't split Stormbite from Caustic Bite, or Dia from Glare's DoT. Resolving this needs the icon → status-effect → action mapping, which is a Lumina/Resource lookup.
- **HoT overheal isn't computed.** Without target HP at tick time, we record the full HoT value as actual healing — slightly inflates HPS for healers. Fixable by polling the target's HP next frame after each HoT tick.
- **HoT bucket inflated by incoming heals.** Heal FlyText fires for every heal visible to the local player (heals on local, heals on party). Unmatched ones get credited as "Heal over Time" from local even when local isn't the healer. Visible in PvP Frontline data as ~300+ HoT hits crediting non-healer jobs. Same root cause as DoT attribution gap — needs the DoTSimulator port to fix.
- **Rare DoT inflation from party auto-attack FlyText.** If a party member's auto-attack FlyText is visible to local and its value doesn't match any local auto-attack in the buffer, it'll be falsely credited as a local DoT tick. Bounded scope — only auto-attacks, only when "show party damage" is on.

**Next step (v0.3.x):** port `FFXIV_ACT_Plugin.Parse.DoTSimulator` (decompiled, [FFXIV_ACT_Plugin/decompiled/parse/DoTSimulator.cs](FFXIV_ACT_Plugin/decompiled/parse/DoTSimulator.cs)). It tracks status applications from ActionEffect, schedules ticks on a 3-second cadence per target, and computes per-tick damage from the action's base potency × source stats (Crit / Det / DH / SpellSpeed). That gives per-DoT-per-caster attribution to any party member who applied a status. Big port, ~1000 lines incl. helpers, but the source map is now local.

### 9.8 Crit / Direct-Hit Statistics Not Surfaced

**Problem:** Crit% and Direct Hit% per ability are not tracked, despite the bits being readable from `Param0` (§1.4). The 0.2.6 fix added flag-reading to the debug log but did not extend `AbilityStats` to count them.

**Fix sketch:** Add `int CritHits`, `int DirectHits` to `AbilityStats`. Increment in `RecordAbility` when the bits are set. Expose `CritRate` and `DhRate` as computed properties. Show in the HTTP API per-combatant detail. Trivial code change once the data flow is verified.

### 9.9 Instance Summary Not Yet Implemented

**Problem:** After leaving an instance (zone change), there's no auto-created aggregate session merging all pulls from that run.

**Status:** Data model supports it (`IsSummary`, `PullCount`, `MergeAbilities`). Session lifecycle logic (`OnTerritoryChanged`) does not yet create it.

---

## 10. Decompile References

The DamageMeter directory now contains decompiled reference material for cross-validation. **Do not edit these files** — they are read-only references.

| Path | What it is |
|---|---|
| [ACT/](ACT/) | ACT v3.8.5.288 EXE — the `IActPluginV1` host, not used by this plugin but reference for combat-data model concepts |
| [FFXIV_ACT_Plugin/](FFXIV_ACT_Plugin/) | FFXIV_ACT_Plugin v3.0.2.1 main DLL + 2 Deucalion native injectors |
| [FFXIV_ACT_Plugin/extracted/](FFXIV_ACT_Plugin/extracted/) | Costura-bundled sub-DLLs (Common, Parse, Logfile, Network, Memory, Config, Resource + Machina) |
| [FFXIV_ACT_Plugin/decompiled/parse/](FFXIV_ACT_Plugin/decompiled/parse/) | **Gold-standard damage decoder.** `DamageEffectEntry.cs`, `HealEffectEntry.cs`, `EffectEntry.cs`, `ReportCombatData.cs`, `ParseStrategyActionEffect.cs` are the authoritative references for the byte layout and crit/DH/extended-value math. |
| [FFXIV_ACT_Plugin/decompiled/logfile/](FFXIV_ACT_Plugin/decompiled/logfile/) | `LogMessageType` enum (line-type codes 0–43 / 249–254) |
| [FFXIV_ACT_Plugin/decompiled/network/](FFXIV_ACT_Plugin/decompiled/network/) | Packet handlers — `Ability.cs` shows the wire format → log line mapping |
| [FFXIVClientStructs_decompiled/](FFXIVClientStructs_decompiled/) | Dalamud's `ActionEffectHandler.Effect` ground truth — byte layout authority |
| [ACT_REFERENCE.md](ACT_REFERENCE.md) | Full 985-line architectural reference for ACT and the FFXIV plugin family. Background reading. |

External sources used:
- [perchbirdd/DamageInfoPlugin](https://github.com/perchbirdd/DamageInfoPlugin) — third independent confirmation of the `ActionEffectType` enum and byte layout. See `DamageInfoEnums.cs` and `DamageInfoStructs.cs`.
- [ravahn/FFXIV_ACT_Plugin wiki](https://github.com/ravahn/FFXIV_ACT_Plugin/wiki) — supplementary docs on `IDataSubscription` / `IDataRepository`.
- [aers/FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) — repository for ground-truth struct layouts.

---

*Last updated: 2026-06-09 — 0.2.6 accuracy pass: byte-layout (§1.4), EffectKind enum (§1.5), and §9.1/§9.2/§9.4 re-grounded against FFXIVClientStructs + Ravahn parser + perchbirdd. §9.6/§9.7/§9.8 added for remaining accuracy work (SourceEntry remap, DoT tracking, crit stats).*
