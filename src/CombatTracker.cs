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
/// Hooks ReceiveActionEffect to capture damage/healing events, manages session
/// lifecycle, categorises combatants (party / friendly / enemy), and tracks
/// per-ability breakdowns for the detail popup.
/// </summary>
public sealed class CombatTracker : IDisposable
{
    // ── ActionEffect hook ─────────────────────────────────────────────────────
    //
    // Raw ActionEffect entry — 8 bytes per effect, layout:
    //   [0] Type   (EffectKind below)
    //   [1] Param0
    //   [2] Param1
    //   [3] Param2
    //   [4] Param3 — high byte for extended values (damage > 65 535)
    //   [5] Param4
    //   [6] Flags  — bit 0x40 = extend value via Param3
    //   [7] Flags2
    // Value ushort = bytes [6..7]; extended = value | (Param3 << 16) when Flags & 0x40.
    // TODO: re-verify layout against FFXIVClientStructs after major game patches.

    private const int EffectSize       = 8;
    private const int EffectsPerTarget = 8;

    private enum EffectKind : byte
    {
        Nothing       = 0,
        Miss          = 1,
        FullResist    = 2,
        Damage        = 3,
        BlockedDamage = 4,
        ParriedDamage = 5,
        Invulnerable  = 6,
        OtherDamage   = 11,
        Heal          = 14,
    }

    private unsafe delegate void ReceiveActionEffectDelegate(
        uint                               casterEntityId,
        Character*                         casterPtr,
        Vector3*                           targetPos,
        ActionEffectHandler.Header*        header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId*                      targetEntityIds);

    private Hook<ReceiveActionEffectDelegate>? _hook;

    // ── Services ──────────────────────────────────────────────────────────────
    private readonly IPluginLog   _log;
    private readonly ICondition   _condition;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IFramework   _framework;
    private readonly IDataManager _dataManager;
    private readonly IPartyList   _partyList;

    // ── State ─────────────────────────────────────────────────────────────────
    private bool          _wasInCombat;
    private readonly Configuration _config;
    private readonly string        _storePath;

    // Action name cache: looked up from Lumina on first encounter
    private readonly Dictionary<uint, string> _actionNames = new();

    public CombatSession? ActiveSession { get; private set; }
    public SessionStore   Store         { get; private set; } = new();

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
        IPartyList           partyList,
        Configuration        config,
        string               configDir)
    {
        _log         = log;
        _condition   = condition;
        _objectTable = objectTable;
        _clientState = clientState;
        _framework   = framework;
        _dataManager = dataManager;
        _partyList   = partyList;
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
        if (inCombat && !_wasInCombat) StartSession();
        if (!inCombat && _wasInCombat) EndSession();
        _wasInCombat = inCombat;
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────
    private void StartSession()
    {
        var zone    = GetZoneName();
        var now     = DateTime.UtcNow;
        ActiveSession = new CombatSession
        {
            Id        = CombatSession.MakeId(zone, now),
            ZoneName  = zone,
            StartTime = now,
        };
        _log.Info($"DamageMeter: Combat started — {ActiveSession.Id}");
        OnSessionStarted?.Invoke(ActiveSession);
    }

    private void EndSession()
    {
        if (ActiveSession == null) return;
        ActiveSession.EndTime = DateTime.UtcNow;

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

    // ── ActionEffect hook ─────────────────────────────────────────────────────
    private unsafe void OnReceiveActionEffect(
        uint                               casterEntityId,
        Character*                         casterPtr,
        Vector3*                           targetPos,
        ActionEffectHandler.Header*        header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId*                      targetEntityIds)
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
        uint                               casterEntityId,
        Character*                         casterPtr,
        ActionEffectHandler.Header*        header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId*                      targetEntityIds)
    {
        if (ActiveSession == null) return;

        var numTargets = header->NumTargets;
        var actionId   = header->ActionId;
        var isAoe      = numTargets >= 3;
        var tickMs     = (long)(DateTime.UtcNow - ActiveSession.StartTime).TotalMilliseconds;
        var casterData = GetOrCreateCombatant(ActiveSession, casterEntityId, casterPtr);
        var actionName = GetActionName(actionId);

        var effectsBase = (byte*)effects;

        for (int t = 0; t < numTargets && t < 32; t++)
        {
            var targetId      = targetEntityIds[t].ObjectId;
            if (targetId == 0) continue;

            var targetEffBase = effectsBase + t * (EffectSize * EffectsPerTarget);
            var targetData    = GetOrCreateCombatantById(ActiveSession, targetId);

            for (int e = 0; e < EffectsPerTarget; e++)
            {
                var effPtr = targetEffBase + e * EffectSize;
                var kind   = (EffectKind)effPtr[0];
                var param3 = effPtr[4];
                var flags  = effPtr[6];
                var value  = (long)*(ushort*)(effPtr + 6);

                if ((flags & 0x40) != 0)
                    value += (long)param3 << 16;

                if (value <= 0) continue;

                switch (kind)
                {
                    case EffectKind.Damage:
                    case EffectKind.BlockedDamage:
                    case EffectKind.ParriedDamage:
                    case EffectKind.OtherDamage:
                        if (casterData != null)
                        {
                            casterData.TotalDamageDealt += value;
                            casterData.DamageEvents.Add((tickMs, value));
                            RecordAbility(casterData.DamageByAbility, actionId, actionName, value);
                        }
                        if (targetData != null)
                        {
                            targetData.TotalDamageTaken += value;
                            if (isAoe) targetData.TotalAvoidableDamageTaken += value;
                            RecordAbility(targetData.DamageTakenByAbility, actionId, actionName, value);
                        }
                        break;

                    case EffectKind.Heal:
                        if (casterData != null)
                        {
                            var overheal   = ComputeOverheal(targetId, value);
                            var actualHeal = value - overheal;
                            casterData.TotalHealingDone     += actualHeal;
                            casterData.TotalOverhealingDone += overheal;
                            casterData.HealingEvents.Add((tickMs, actualHeal));
                            RecordAbility(casterData.HealingByAbility, actionId, actionName, actualHeal, overheal);
                        }
                        break;
                }
            }
        }
    }

    // ── Ability stats helpers ─────────────────────────────────────────────────
    private static void RecordAbility(
        Dictionary<uint, AbilityStats> dict,
        uint actionId, string name, long amount, long overheal = 0)
    {
        if (!dict.TryGetValue(actionId, out var stats))
        {
            stats = new AbilityStats { ActionId = actionId, Name = name };
            dict[actionId] = stats;
        }
        stats.Record(amount, overheal);
    }

    private string GetActionName(uint actionId)
    {
        if (_actionNames.TryGetValue(actionId, out var cached)) return cached;
        try
        {
            var name = _dataManager
                .GetExcelSheet<Lumina.Excel.Sheets.Action>()
                ?.GetRow(actionId).Name.ToString();
            var result = !string.IsNullOrWhiteSpace(name) ? name : $"#{actionId}";
            _actionNames[actionId] = result;
            return result;
        }
        catch
        {
            _actionNames[actionId] = $"#{actionId}";
            return _actionNames[actionId];
        }
    }

    // ── Combatant resolution ──────────────────────────────────────────────────
    private unsafe CombatantData? GetOrCreateCombatant(
        CombatSession session, uint entityId, Character* charPtr)
    {
        if (entityId == 0) return null;
        if (session.Combatants.TryGetValue(entityId, out var existing)) return existing;

        var data = new CombatantData { EntityId = entityId };
        var obj  = _objectTable.FirstOrDefault(o => o.EntityId == entityId);

        if (obj != null)
        {
            data.Name  = obj.Name.TextValue;
            data.World = GetPlayerWorld(obj);
            data.Type  = DetermineType(entityId, obj);
        }

        if (charPtr != null)
            data.ClassJobId = charPtr->CharacterData.ClassJob;

        session.Combatants[entityId] = data;
        return data;
    }

    private CombatantData? GetOrCreateCombatantById(CombatSession session, uint entityId)
    {
        if (entityId == 0) return null;
        if (session.Combatants.TryGetValue(entityId, out var existing)) return existing;

        var obj = _objectTable.FirstOrDefault(o => o.EntityId == entityId);
        if (obj == null) return null;

        var data = new CombatantData
        {
            EntityId = entityId,
            Name     = obj.Name.TextValue,
            World    = GetPlayerWorld(obj),
            Type     = DetermineType(entityId, obj),
        };

        if (obj is IBattleChara chara)
            data.ClassJobId = (byte)chara.ClassJob.RowId;

        session.Combatants[entityId] = data;
        return data;
    }

    private CombatantType DetermineType(uint entityId, IGameObject obj)
    {
        if (obj is IPlayerCharacter)
        {
            // Check if in the local party list
            foreach (var member in _partyList)
            {
                if (member.EntityId == entityId)
                    return CombatantType.PartyMember;
            }
            return CombatantType.FriendlyPlayer;
        }
        if (obj is IBattleChara)
            return CombatantType.Enemy;
        return CombatantType.Unknown;
    }

    private long ComputeOverheal(uint targetId, long healValue)
    {
        var obj = _objectTable.FirstOrDefault(o => o.EntityId == targetId);
        if (obj is IBattleChara chara)
        {
            var missing = (long)chara.MaxHp - (long)chara.CurrentHp;
            if (missing <= 0) return healValue;
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
        catch { return "Unknown"; }
    }

    // ── Session management ────────────────────────────────────────────────────
    public void SaveSession(CombatSession session)
    {
        if (Store.SavedSessions.Any(s => s.Id == session.Id)) return;
        session.IsSaved = true;
        Store.SavedSessions.Add(session);
        Store.TempSessions.Remove(session);
        SaveStore();
    }

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
            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            var json     = JsonConvert.SerializeObject(Store, Formatting.Indented, settings);
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
        if (ActiveSession != null) EndSession();
        _hook?.Dispose();
        _log.Info("DamageMeter: CombatTracker disposed.");
    }
}
