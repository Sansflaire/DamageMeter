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
    // Raw ActionEffect entry — 8 bytes per effect.
    // Ground-truthed against FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler.Effect,
    // ravahn/FFXIV_ACT_Plugin DamageEffectEntry/HealEffectEntry, and perchbirdd/DamageInfoPlugin
    // (all three agree on byte layout):
    //   [0] Type   = EffectKind below
    //   [1] Param0 = bit 0x20 = Critical (Damage), bit 0x40 = DirectHit (Damage)
    //   [2] Param1 = low nibble = AttackType, high nibble = ElementType (Damage)
    //                bit 0x20 = Critical (Heal — yes, Heal's crit bit is at a different byte)
    //   [3] Param2 = combo amount / positional bonus
    //   [4] Param3 = high word multiplier for extended values (added when Param4 & 0x40)
    //   [5] Param4 = bit 0x40 = "extend value with Param3 << 16", bit 0x80 = SourceEntry
    //   [6] Value low byte ┐
    //   [7] Value high byte┘  ushort at offset 6
    //
    // Extended damage formula: damage = Value + ((Param4 & 0x40) != 0 ? Param3 * 65536 : 0).

    private const int EffectSize       = 8;
    private const int EffectsPerTarget = 8;

    // EffectKind byte values are authoritative from FFXIVClientStructs / Ravahn / perchbirdd.
    // The previous values for BlockedDamage/ParriedDamage/Invulnerable/Heal were off by 1+,
    // and "OtherDamage = 11" was actually MpGain — see RESEARCH.md §1.5.
    private enum EffectKind : byte
    {
        Nothing                 = 0,
        Miss                    = 1,
        FullResist              = 2,
        Damage                  = 3,
        Heal                    = 4,    // was 14 (which is actually GpGain)
        BlockedDamage           = 5,    // was 4 (which is actually Heal)
        ParriedDamage           = 6,    // was 5
        Invulnerable            = 7,    // was 6
        NoEffectText            = 8,
        MpLoss                  = 10,
        MpGain                  = 11,   // previously misnamed "OtherDamage" and counted as damage
        TpLoss                  = 12,
        TpGain                  = 13,
        GpGain                  = 14,   // was previously labeled "Heal" here
        ApplyStatusEffectTarget = 15,
        ApplyStatusEffectSource = 16,
        StatusNoEffect          = 20,
        Knockback               = 33,
        Mount                   = 40,
        VFX                     = 59,
        JobGauge                = 61,
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

    // Instance summary tracking: record time when we enter a new zone so we can
    // collect all sessions recorded there and merge them on zone exit.
    private DateTime _instanceEnterTime = DateTime.UtcNow;

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

        _framework.Update          += OnFrameworkUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
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

    // ── Territory change → instance summary ───────────────────────────────────
    private void OnTerritoryChanged(uint newTerritoryId)
    {
        try   { TryCreateInstanceSummary(); }
        catch (Exception ex) { _log.Error($"DamageMeter: Instance summary error — {ex.Message}"); }
        _instanceEnterTime = DateTime.UtcNow;
    }

    private void TryCreateInstanceSummary()
    {
        var pulls = Store.TempSessions
            .Where(s => !s.IsSummary && s.StartTime >= _instanceEnterTime)
            .ToList();

        if (pulls.Count < 2) return;

        var summary = BuildInstanceSummary(pulls);
        Store.TempSessions.Add(summary);
        PruneTempSessions();
        SaveStore();
        _log.Info($"DamageMeter: Instance summary — {summary.Id} ({pulls.Count} pulls)");
    }

    private static CombatSession BuildInstanceSummary(List<CombatSession> pulls)
    {
        var zoneName  = pulls[0].ZoneName;
        var startTime = pulls[0].StartTime;
        var endTime   = pulls.Max(s => s.EndTime ?? s.StartTime);

        var summary = new CombatSession
        {
            Id        = CombatSession.MakeId(zoneName, startTime) + "_summary",
            ZoneName  = zoneName,
            StartTime = startTime,
            EndTime   = endTime,
            IsSummary = true,
            PullCount = pulls.Count,
        };

        foreach (var pull in pulls)
        {
            var offset = (long)(pull.StartTime - startTime).TotalMilliseconds;
            foreach (var (entityId, src) in pull.Combatants)
            {
                if (!summary.Combatants.TryGetValue(entityId, out var dst))
                {
                    dst = new CombatantData
                    {
                        EntityId   = src.EntityId,
                        Name       = src.Name,
                        World      = src.World,
                        ClassJobId = src.ClassJobId,
                        Type       = src.Type,
                    };
                    summary.Combatants[entityId] = dst;
                }

                dst.TotalDamageDealt          += src.TotalDamageDealt;
                dst.TotalHealingDone          += src.TotalHealingDone;
                dst.TotalOverhealingDone      += src.TotalOverhealingDone;
                dst.TotalDamageTaken          += src.TotalDamageTaken;
                dst.TotalAvoidableDamageTaken += src.TotalAvoidableDamageTaken;

                MergeAbilities(dst.DamageByAbility,      src.DamageByAbility);
                MergeAbilities(dst.HealingByAbility,     src.HealingByAbility);
                MergeAbilities(dst.DamageTakenByAbility, src.DamageTakenByAbility);

                foreach (var ev in src.DamageEvents)
                    dst.DamageEvents.Add((ev.TickMs + offset, ev.Amount));
                foreach (var ev in src.HealingEvents)
                    dst.HealingEvents.Add((ev.TickMs + offset, ev.Amount));
            }
        }

        return summary;
    }

    private static void MergeAbilities(
        Dictionary<uint, AbilityStats> dst,
        Dictionary<uint, AbilityStats> src)
    {
        foreach (var (id, s) in src)
        {
            if (!dst.TryGetValue(id, out var d))
            {
                d = new AbilityStats { ActionId = id, Name = s.Name };
                dst[id] = d;
            }
            d.TotalAmount   += s.TotalAmount;
            d.TotalOverheal += s.TotalOverheal;
            d.Hits          += s.Hits;
            d.MinHit = (d.MinHit == 0) ? s.MinHit
                     : (s.MinHit  == 0) ? d.MinHit
                     : Math.Min(d.MinHit, s.MinHit);
            d.MaxHit = Math.Max(d.MaxHit, s.MaxHit);
        }
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
                var param0 = effPtr[1];                       // crit (0x20), DH (0x40) for Damage
                var param3 = effPtr[4];                       // high word multiplier
                var param4 = effPtr[5];                       // flags: 0x40 = extend, 0x80 = source entry
                var value  = (long)*(ushort*)(effPtr + 6);    // ushort Value

                // The previous build read the flag byte from effPtr[6], which is the LOW BYTE
                // of Value — that caused spurious extension by Param3*65536 on roughly half
                // of all medium-magnitude hits. The flag actually lives in Param4 at offset 5.
                if ((param4 & 0x40) != 0)
                    value += (long)param3 << 16;

                if (value <= 0) continue;

                bool isCritical  = (param0 & 0x20) != 0;
                bool isDirectHit = (param0 & 0x40) != 0;
                var casterName = casterData?.Name ?? $"#{casterEntityId}";
                var targetName = targetData?.Name ?? $"#{targetId}";
                var casterType = casterData?.Type.ToString() ?? "null";
                var targetType = targetData?.Type.ToString() ?? "null";
                _log.Debug($"[DM] kind={(byte)kind}(0x{(byte)kind:X2} {kind}) " +
                           $"action={actionId}({actionName}) val={value}" +
                           (isCritical ? " CRIT" : "") + (isDirectHit ? " DH" : "") +
                           $" caster={casterName}[{casterType}] target={targetName}[{targetType}]" +
                           (targetId == casterEntityId ? " SELF" : ""));

                switch (kind)
                {
                    case EffectKind.Damage:
                    case EffectKind.BlockedDamage:
                    case EffectKind.ParriedDamage:
                    {
                        bool killingBlow = IsKillingBlow(targetId, value);
                        bool isSelfHit   = targetId == casterEntityId;

                        // Damage dealt: record for any hit that isn't a self-hit.
                        // Self-heals like Recuperate arrive as EffectKind.Damage with
                        // casterEntityId == targetId — that's the only case we exclude.
                        if (casterData != null && !isSelfHit)
                        {
                            casterData.TotalDamageDealt += value;
                            casterData.DamageEvents.Add((tickMs, value));
                            RecordAbility(casterData.DamageByAbility, actionId, actionName, value, skipMin: killingBlow);
                        }
                        // Damage taken: record unless the caster is a party member of the target
                        // (party members can't damage each other in any content we care about).
                        // Unknown caster (null) = environment damage — always record.
                        if (targetData != null && !isSelfHit && casterData?.Type != CombatantType.PartyMember)
                        {
                            targetData.TotalDamageTaken += value;
                            if (isAoe) targetData.TotalAvoidableDamageTaken += value;
                            RecordAbility(targetData.DamageTakenByAbility, actionId, actionName, value);
                        }
                        break;
                    }

                    case EffectKind.Heal:
                    {
                        // Only record heals that target a friendly entity — filters out abilities
                        // like Feint/True North that produce spurious Heal effects on enemies.
                        if (casterData != null && targetData?.Type != CombatantType.Enemy)
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
    }

    // ── Ability stats helpers ─────────────────────────────────────────────────
    private static void RecordAbility(
        Dictionary<uint, AbilityStats> dict,
        uint actionId, string name, long amount, long overheal = 0, bool skipMin = false)
    {
        if (!dict.TryGetValue(actionId, out var stats))
        {
            stats = new AbilityStats { ActionId = actionId, Name = name };
            dict[actionId] = stats;
        }
        stats.Record(amount, overheal, skipMin);
    }

    /// <summary>
    /// Returns true if this hit would kill the target (pre-hit HP &lt;= hit value).
    /// Must be called BEFORE Original fires — CurrentHp is still the pre-hit value.
    /// </summary>
    private bool IsKillingBlow(uint targetId, long value)
    {
        var obj = _objectTable.FirstOrDefault(o => o.EntityId == targetId);
        if (obj is IBattleChara chara && chara.CurrentHp > 0)
            return (long)chara.CurrentHp <= value;
        return false;
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
        _framework.Update             -= OnFrameworkUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        if (ActiveSession != null) EndSession();
        _hook?.Dispose();
        _log.Info("DamageMeter: CombatTracker disposed.");
    }
}
