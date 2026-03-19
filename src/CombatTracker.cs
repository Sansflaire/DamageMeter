using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

using Newtonsoft.Json;

namespace DamageMeter;

/// <summary>
/// Core combat-tracking service.
/// Hooks ReceiveActionEffect to capture damage/healing events in real time,
/// manages session lifecycle (start on InCombat, end when combat drops),
/// and persists sessions to JSON.
/// </summary>
public sealed class CombatTracker : IDisposable
{
    // ── FFXIV ActionEffect hook ───────────────────────────────────────────────
    //
    // Layout of each 8-byte ActionEffect entry (unchanged since Stormblood):
    //   [0] Type   (byte)  — see EffectKind below
    //   [1] Param0 (byte)  — damage sub-type / context
    //   [2] Param1 (byte)
    //   [3] Param2 (byte)
    //   [4] Param3 (byte)  — high byte for extended values (> 65535)
    //   [5] Param4 (byte)
    //   [6] Flags  (byte)  — bit 0x40 = extend Param3 into high 8 bits of value
    //   [7] Flags2 (byte)  — additional effect flags
    // Value (ushort) is stored at bytes 6-7 in some versions; verify against
    // current FFXIVClientStructs if damage numbers look wrong.
    //
    // TODO: If damage numbers appear incorrect after a major patch, cross-reference
    // the ActionEffect struct layout with FFXIVClientStructs source on GitHub.

    private const int EffectSize       = 8;  // bytes per ActionEffect entry
    private const int EffectsPerTarget = 8;  // max effects per target per packet

    // Effect type bytes — verify against ActionEffectType in FFXIVClientStructs
    private enum EffectKind : byte
    {
        Nothing          = 0,
        Miss             = 1,
        FullResist       = 2,
        Damage           = 3,
        BlockedDamage    = 4,
        ParriedDamage    = 5,
        Invulnerable     = 6,
        OtherDamage      = 11,  // e.g. fall, DoT ticks in some versions
        Heal             = 14,
        // TODO: verify these values match the installed FFXIVClientStructs
    }

    private unsafe delegate void ReceiveActionEffectDelegate(
        uint                              casterEntityId,
        Character*                        casterPtr,
        Vector3*                          targetPos,
        ActionEffectHandler.Header*       header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId*                     targetEntityIds);

    private Hook<ReceiveActionEffectDelegate>? _hook;

    // ── Services ──────────────────────────────────────────────────────────────
    private readonly IPluginLog    _log;
    private readonly ICondition    _condition;
    private readonly IObjectTable  _objectTable;
    private readonly IClientState  _clientState;
    private readonly IFramework    _framework;
    private readonly IDataManager  _dataManager;

    // ── State ─────────────────────────────────────────────────────────────────
    private bool _wasInCombat;
    private readonly Configuration _config;
    private readonly string        _storePath;

    /// <summary>Currently active session, or null when out of combat.</summary>
    public CombatSession? ActiveSession { get; private set; }

    /// <summary>All persisted sessions (temp + saved).</summary>
    public SessionStore Store { get; private set; } = new();

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<CombatSession>? OnSessionStarted;
    public event Action<CombatSession>? OnSessionEnded;

    // ── Constructor ───────────────────────────────────────────────────────────
    public CombatTracker(
        IGameInteropProvider gameInterop,
        IPluginLog           log,
        ICondition           condition,
        IObjectTable         objectTable,
        IClientState         clientState,
        IFramework           framework,
        IDataManager         dataManager,
        Configuration        config,
        string               configDir)
    {
        _log         = log;
        _condition   = condition;
        _objectTable = objectTable;
        _clientState = clientState;
        _framework   = framework;
        _dataManager = dataManager;
        _config      = config;
        _storePath   = Path.Combine(configDir, "sessions.json");

        LoadStore();

        unsafe
        {
            var addr = ActionEffectHandler.Addresses.Receive.Value;
            _hook = gameInterop.HookFromAddress<ReceiveActionEffectDelegate>(
                (nint)addr, OnReceiveActionEffect);
            _hook.Enable();
        }

        _framework.Update += OnFrameworkUpdate;
        _log.Info("DamageMeter: CombatTracker initialized.");
    }

    // ── Framework tick ────────────────────────────────────────────────────────
    private void OnFrameworkUpdate(IFramework fw)
    {
        var inCombat = _condition[ConditionFlag.InCombat];

        if (inCombat && !_wasInCombat)
            StartSession();

        if (!inCombat && _wasInCombat)
            EndSession();

        _wasInCombat = inCombat;
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────
    private void StartSession()
    {
        var zone     = GetZoneName();
        var now      = DateTime.UtcNow;
        var session  = new CombatSession
        {
            Id        = CombatSession.MakeId(zone, now),
            ZoneName  = zone,
            StartTime = now,
        };
        ActiveSession = session;
        _log.Info($"DamageMeter: Combat started — {session.Id}");
        OnSessionStarted?.Invoke(session);
    }

    private void EndSession()
    {
        if (ActiveSession == null) return;

        ActiveSession.EndTime = DateTime.UtcNow;

        // Skip trivially short pulls (< 3 seconds)
        if (ActiveSession.DurationSeconds >= 3.0 && ActiveSession.Combatants.Count > 0)
        {
            Store.TempSessions.Add(ActiveSession);
            PruneTempSessions();
            SaveStore();
        }

        _log.Info($"DamageMeter: Combat ended — {ActiveSession.Id} ({ActiveSession.FormattedDuration})");
        OnSessionEnded?.Invoke(ActiveSession);
        ActiveSession = null;
    }

    private void PruneTempSessions()
    {
        while (Store.TempSessions.Count > _config.MaxTempHistory)
            Store.TempSessions.RemoveAt(0);
    }

    // ── Action effect hook ────────────────────────────────────────────────────
    private unsafe void OnReceiveActionEffect(
        uint                              casterEntityId,
        Character*                        casterPtr,
        Vector3*                          targetPos,
        ActionEffectHandler.Header*       header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId*                     targetEntityIds)
    {
        try
        {
            if (ActiveSession != null && header->NumTargets > 0)
                ProcessEffects(casterEntityId, casterPtr, header, effects, targetEntityIds);
        }
        catch (Exception ex)
        {
            _log.Error($"DamageMeter: Hook error — {ex.Message}");
        }
        finally
        {
            _hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        }
    }

    private unsafe void ProcessEffects(
        uint                              casterEntityId,
        Character*                        casterPtr,
        ActionEffectHandler.Header*       header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId*                     targetEntityIds)
    {
        if (ActiveSession == null) return;

        var numTargets   = header->NumTargets;
        var isAoe        = numTargets >= 3; // proxy for "avoidable" AoE
        var tickMs       = (long)(DateTime.UtcNow - ActiveSession.StartTime).TotalMilliseconds;
        var casterData   = GetOrCreateCombatant(ActiveSession, casterEntityId, casterPtr);

        // Pointer to the first TargetEffects (each is 8 * 8 = 64 bytes)
        var effectsBase = (byte*)effects;

        for (int t = 0; t < numTargets && t < 32; t++)
        {
            var targetId      = targetEntityIds[t].ObjectId;
            if (targetId == 0) continue;

            var targetEffBase = effectsBase + t * (EffectSize * EffectsPerTarget);
            var targetData    = GetOrCreateCombatantById(ActiveSession, targetId);

            for (int e = 0; e < EffectsPerTarget; e++)
            {
                var effPtr  = targetEffBase + e * EffectSize;
                var kind    = (EffectKind)effPtr[0];
                var param3  = effPtr[4]; // high byte for extended value
                var flags   = effPtr[6];
                // Value: ushort at bytes [6..7]
                // Note: in FFXIV post-Shadowbringers the value ushort sits at offset 6.
                // Some community parsers find it at offset 7 on certain patch versions.
                // TODO: verify if numbers look wrong after a game patch.
                var valueLo = *(ushort*)(effPtr + 6);
                var value   = (long)valueLo;

                // Extend to >65535 via Param3 when the extend flag (0x40) is set
                if ((flags & 0x40) != 0)
                    value += (long)param3 << 16;

                if (value <= 0) continue;

                switch (kind)
                {
                    case EffectKind.Damage:
                    case EffectKind.BlockedDamage:
                    case EffectKind.ParriedDamage:
                    case EffectKind.OtherDamage:
                        // Caster deals damage to target
                        if (casterData != null)
                        {
                            casterData.TotalDamageDealt += value;
                            casterData.DamageEvents.Add((tickMs, value));
                        }
                        // Target takes damage
                        if (targetData != null)
                        {
                            targetData.TotalDamageTaken += value;
                            if (isAoe)
                                targetData.TotalAvoidableDamageTaken += value;
                        }
                        break;

                    case EffectKind.Heal:
                        // Caster heals target
                        if (casterData != null)
                        {
                            // Approximate overheal: if target has full HP, entire heal is overheal
                            var overheal = ComputeOverheal(targetId, value);
                            var actualHeal = value - overheal;
                            casterData.TotalHealingDone     += actualHeal;
                            casterData.TotalOverhealingDone += overheal;
                            casterData.HealingEvents.Add((tickMs, actualHeal));
                        }
                        break;
                }
            }
        }
    }

    // ── Combatant resolution ──────────────────────────────────────────────────
    private unsafe CombatantData? GetOrCreateCombatant(
        CombatSession session, uint entityId, Character* charPtr)
    {
        if (entityId == 0) return null;
        if (session.Combatants.TryGetValue(entityId, out var existing))
            return existing;

        var data = new CombatantData { EntityId = entityId };

        // Try Dalamud object table first (has name + world)
        var obj = _objectTable.FirstOrDefault(o => o.EntityId == entityId);
        if (obj != null)
        {
            data.Name  = obj.Name.TextValue;
            data.World = GetPlayerWorld(obj);
        }
        // No raw pointer name fallback — unreliable across FFXIVClientStructs versions

        if (charPtr != null)
            data.ClassJobId = charPtr->CharacterData.ClassJob;

        session.Combatants[entityId] = data;
        return data;
    }

    private CombatantData? GetOrCreateCombatantById(CombatSession session, uint entityId)
    {
        if (entityId == 0) return null;
        if (session.Combatants.TryGetValue(entityId, out var existing))
            return existing;

        var obj = _objectTable.FirstOrDefault(o => o.EntityId == entityId);
        if (obj == null) return null; // NPCs/unknown objects — skip

        var data = new CombatantData
        {
            EntityId   = entityId,
            Name       = obj.Name.TextValue,
            World      = GetPlayerWorld(obj),
        };

        // Job: try cast to IBattleChara (players + enemies both implement it)
        if (obj is IBattleChara chara)
            data.ClassJobId = (byte)chara.ClassJob.RowId;

        session.Combatants[entityId] = data;
        return data;
    }

    private long ComputeOverheal(uint targetId, long healValue)
    {
        var obj = _objectTable.FirstOrDefault(o => o.EntityId == targetId);
        if (obj is IBattleChara chara)
        {
            var missing = (long)chara.MaxHp - (long)chara.CurrentHp;
            if (missing <= 0) return healValue; // already full HP
            return Math.Max(0, healValue - missing);
        }
        return 0;
    }

    private static string GetPlayerWorld(IGameObject obj)
    {
        if (obj is IPlayerCharacter pc)
            return pc.HomeWorld.Value.Name.ToString();
        return "";
    }

    private string GetZoneName()
    {
        try
        {
            var territory = _dataManager
                .GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                ?.GetRow(_clientState.TerritoryType);
            return territory?.PlaceName.Value.Name.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    // ── Session management (public API) ───────────────────────────────────────

    /// <summary>Manually save a temp session to the permanent saved list.</summary>
    public void SaveSession(CombatSession session)
    {
        if (Store.SavedSessions.Any(s => s.Id == session.Id)) return;
        session.IsSaved = true;
        Store.SavedSessions.Add(session);
        Store.TempSessions.Remove(session);
        SaveStore();
    }

    /// <summary>Delete a session from either list.</summary>
    public void DeleteSession(CombatSession session)
    {
        Store.TempSessions.Remove(session);
        Store.SavedSessions.Remove(session);
        SaveStore();
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private void LoadStore()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var json = File.ReadAllText(_storePath);
                Store = JsonConvert.DeserializeObject<SessionStore>(json) ?? new SessionStore();
                _log.Info($"DamageMeter: Loaded {Store.TempSessions.Count} temp + {Store.SavedSessions.Count} saved sessions.");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"DamageMeter: Failed to load sessions — {ex.Message}");
            Store = new SessionStore();
        }
    }

    public void SaveStore()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Store, Formatting.Indented);
            File.WriteAllText(_storePath, json);
        }
        catch (Exception ex)
        {
            _log.Error($"DamageMeter: Failed to save sessions — {ex.Message}");
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;

        // End any active session cleanly
        if (ActiveSession != null)
            EndSession();

        _hook?.Dispose();
        _log.Info("DamageMeter: CombatTracker disposed.");
    }
}
