using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
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
        Nothing                   = 0,
        Miss                      = 1,
        FullResist                = 2,
        Damage                    = 3,
        Heal                      = 4,
        BlockedDamage             = 5,
        ParriedDamage             = 6,
        Invulnerable              = 7,
        NoEffectText              = 8,
        MpLoss                    = 10,
        MpGain                    = 11,
        TpLoss                    = 12,
        TpGain                    = 13,
        // 14/15/16 corrected from prior off-by-one (had 14=GpGain, 15=Target,
        // 16=Source). Ground-truthed against FFXIV_ACT_Plugin's EffectEntryType
        // and confirmed live: pressing Caustic Bite at lv80 emitted a slot-1
        // entry with kind=14 and val=1200 (the Caustic Bite status id), which
        // can only be ApplyStatusEffectTarget. Bards have no GP gauge.
        ApplyStatusEffectTarget   = 14,
        ApplyStatusEffectSource   = 15,
        RecoveredFromStatusEffect = 16,
        LoseStatusEffectTarget    = 17,
        LoseStatusEffectSource    = 18,
        StatusNoEffect            = 20,
        Knockback                 = 33,
        Mount                     = 40,
        VFX                       = 59,
        JobGauge                  = 61,
    }

    private unsafe delegate void ReceiveActionEffectDelegate(
        uint                               casterEntityId,
        Character*                         casterPtr,
        Vector3*                           targetPos,
        ActionEffectHandler.Header*        header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId*                      targetEntityIds);

    private Hook<ReceiveActionEffectDelegate>? _hook;

    // ── UseAction hook (captures every button press by anyone, including local) ─
    private unsafe delegate bool UseActionDelegate(
        FFXIVClientStructs.FFXIV.Client.Game.ActionManager* thisPtr,
        FFXIVClientStructs.FFXIV.Client.Game.ActionType     actionType,
        uint                                                actionId,
        ulong                                               targetId,
        uint                                                extraParam,
        FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseActionMode mode,
        uint                                                comboRouteId,
        bool*                                               outOptAreaTargeted);

    private Hook<UseActionDelegate>? _useActionHook;

    // ── Services ──────────────────────────────────────────────────────────────
    private readonly IPluginLog   _log;
    private readonly ICondition   _condition;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IFramework   _framework;
    private readonly IDataManager _dataManager;
    private readonly IPartyList   _partyList;
    private readonly IFlyTextGui  _flyTextGui;
    private readonly IChatGui     _chatGui;

    // ── State ─────────────────────────────────────────────────────────────────
    private bool          _wasInCombat;
    private readonly Configuration _config;
    private readonly string        _storePath;
    private readonly CombatLog     _combatLog;

    // Instance summary tracking: record time when we enter a new zone so we can
    // collect all sessions recorded there and merge them on zone exit.
    private DateTime _instanceEnterTime = DateTime.UtcNow;

    // Action name cache: looked up from Lumina on first encounter
    private readonly Dictionary<uint, string> _actionNames = new();

    // ── DoT/HoT FlyText pseudo-ability IDs ────────────────────────────────────
    // Real game action IDs are < 0x40000 (262144). We use sentinels above that to
    // avoid colliding with any real action. Both buckets aggregate all tick damage
    // from the FlyText hook into one entry per combatant.
    private const uint DotPseudoActionId = 0xFFFF_FFFE;
    private const uint HotPseudoActionId = 0xFFFF_FFFD;

    // ── Limit Break pseudo-combatant ──────────────────────────────────────────
    // Limit Break actions (ActionCategory == 8 in Lumina) are attributed to a
    // single shared "Limit Break" pseudo-combatant rather than the player who
    // pressed the button — the user's call. EntityId sentinel chosen above all
    // real game IDs to avoid collisions.
    private const uint LimitBreakEntityId = 0xFFFF_FFFA;

    // Action ID → IsLimitBreak cache; Lumina lookup is too expensive to do per hit.
    private readonly Dictionary<uint, bool> _limitBreakCache = new();

    // Action ID → IsAutoAttack cache. ActionCategory.RowId == 1 in Lumina = AutoAttack.
    private readonly Dictionary<uint, bool> _autoAttackCache = new();

    // ── Pet/Owner sentinel ─────────────────────────────────────────────────────
    // FFXIV uses 0xE0000000 as the "no owner" / "no target" sentinel. Any other
    // non-zero OwnerId on a Character* points at the entity that owns the pet
    // (Esteem → DRK player, Bahamut → SMN, Eos → SCH, etc.).
    private const uint NoOwnerSentinel = 0xE0000000;

    // ── FlyText/ActionEffect dedup buffer ─────────────────────────────────────
    // FlyText AutoAttackOrDot{*} fires for two things only: auto-attacks AND
    // DoT ticks. Auto-attacks also fire ActionEffectHandler.Receive (we see
    // them through the hook); DoT ticks don't. So if we record every recent
    // *auto-attack* ActionEffect value, any matching FlyText is the auto-attack's
    // own flytext (skip), and any unmatched FlyText is a DoT tick (credit local).
    //
    // CRITICAL: we ONLY push values from ActionCategory == 1 (auto-attack) hits.
    // Pre-fix this buffer was polluted with direct-ability values (Burst Shot,
    // AoEs, etc.) which fire `Damage*` FlyText that the plugin doesn't consume —
    // those entries sat in the buffer for the full window and ate real DoT ticks
    // whose value happened to collide. Bard DoTs were under-counted ~97% as a
    // result. With the buffer limited to auto-attacks, collisions are rare.
    //
    // The buffer is List<(long,long)> instead of Queue<T> so arbitrary entries
    // can be removed on match (not just FIFO).
    // 350ms was the original guess; the 0.2.10 combat log shows FlyText for
    // auto-attacks consistently arriving 600–1000 ms after the ActionEffect
    // (the game queues damage flytexts to avoid visual overlap on screen). At
    // 350 ms most of them never dedup, so Shot/Burst Shot/enemy AA values were
    // being credited as DoT ticks — exactly the inflation the user reported.
    // 2000 ms is comfortably above observed maxes while still narrower than
    // the slowest auto-attack cadence so buffer collision risk stays low.
    private const long DedupWindowMs = 2000;
    private readonly List<(long Value, long TickMs)> _recentDamageHits = new();
    private readonly List<(long Value, long TickMs)> _recentHealHits   = new();

    // ── Active DoTs applied by the local player ───────────────────────────────
    // We can't tell from a FlyText event which target tick'd or which DoT it
    // came from. But when ProcessEffects sees EffectKind.ApplyStatusEffectTarget
    // (kind 14) with caster=local, we know the user just applied a status to
    // an enemy. Track those and attribute unmatched DoT FlyTexts to the active
    // DoT whose last tick is the most overdue. Multi-DoT bards (Stormbite +
    // Caustic Bite) then see ticks split between the two action names instead
    // of dumped into one generic "Damage over Time" bucket.
    private sealed class ActiveDot
    {
        public uint   TargetId;
        public uint   ActionId;
        public string ActionName = "";
        public long   AppliedAtMs;
        public long   LastTickAtMs;
        public long   ExpiresAtMs;
    }
    private readonly List<ActiveDot> _activeDots = new();
    // Most DoT-applying actions in the game last 30s, 45s, or 60s. We don't
    // know the exact duration per status without a Lumina Status-sheet lookup
    // (TODO), so a generous 60s window is the default — overestimates expiry
    // for short DoTs but never under-credits a still-running tick.
    private const long DefaultDotDurationMs = 60_000;

    // ── Network-free DoT simulator (replaces FlyText fallback) ────────────────
    private readonly DoTSimulator _dotSim;
    // Caches the local player's crit/DH rate so the simulator can roll per-tick
    // crits without re-reading every frame. Re-sampled on each ApplyStatus
    // event from the latest local-cast direct hit (simplest stat snapshot).
    // For BRD at level 80 typical values are ~25% / ~25%; defaults until we
    // see any local crit/DH telemetry.
    private double _localCritRate = 0.25;
    private double _localDhRate   = 0.25;
    private int    _localCritHits, _localTotalHits;
    private int    _localDhHits;

    // Per-packet "remember the initial-hit value" registers. Reset at the top
    // of every ProcessEffects call. Lets the ApplyStatusEffectTarget handler
    // see what the same-packet Damage entry rolled, even though they're
    // separate iterations in the effect loop.
    private long _packetInitDamage;
    private bool _packetInitCrit;
    private bool _packetInitDh;

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
        IFlyTextGui          flyTextGui,
        IChatGui             chatGui,
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
        _flyTextGui  = flyTextGui;
        _chatGui     = chatGui;
        _config      = config;
        _storePath   = Path.Combine(configDir, "sessions.json");
        _combatLog   = new CombatLog(configDir);
        _dotSim      = new DoTSimulator(log);

        LoadStore();

        unsafe
        {
            var addr = ActionEffectHandler.Addresses.Receive.Value;
            _hook = gameInterop.HookFromAddress<ReceiveActionEffectDelegate>(
                (nint)addr, OnReceiveActionEffect);
            _hook.Enable();

            try
            {
                var useAddr = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Addresses.UseAction.Value;
                _useActionHook = gameInterop.HookFromAddress<UseActionDelegate>(
                    (nint)useAddr, OnUseAction);
                _useActionHook.Enable();
            }
            catch (Exception ex)
            {
                _log.Warning($"DamageMeter: UseAction hook failed — {ex.Message}");
            }
        }

        _framework.Update             += OnFrameworkUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _flyTextGui.FlyTextCreated    += OnFlyTextCreated;
        _chatGui.ChatMessage          += OnChatMessage;
        _chatGui.LogMessage           += OnLogMessage;
        _log.Info("DamageMeter: CombatTracker initialized.");
    }

    // ── Framework tick ────────────────────────────────────────────────────────
    private void OnFrameworkUpdate(IFramework fw)
    {
        var inCombat = _condition[ConditionFlag.InCombat];
        if (inCombat && !_wasInCombat) StartSession();
        if (!inCombat && _wasInCombat) EndSession();
        _wasInCombat = inCombat;

        // Drain any DoT ticks that have come due. Each tick is credited to the
        // local player's combatant under "{DotName} (DoT)" via a pseudo-action
        // id (real-action id with the high bit set) so initial-hit rows and
        // tick rows stay separate in the meter.
        if (ActiveSession != null)
        {
            var tickMs = (long)(DateTime.UtcNow - ActiveSession.StartTime).TotalMilliseconds;
            var ticks  = _dotSim.AdvanceTo(tickMs);
            if (ticks.Count > 0)
                DrainSimulatorTicks(ticks, tickMs);
        }
    }

    private const uint DotPseudoActionMask = 0x8000_0000u;

    private void DrainSimulatorTicks(
        List<(DoTSimulator.SimulatedDot Dot, long Value, bool Crit, bool Dh)> ticks,
        long tickMs)
    {
        if (ActiveSession == null) return;
        unsafe
        {
            var local   = _objectTable.LocalPlayer;
            var localId = local?.EntityId ?? 0;
            if (localId == 0) return;
            var localPtr = (Character*)(local?.Address ?? IntPtr.Zero);
            var caster   = GetOrCreateCombatant(ActiveSession, localId, localPtr);
            if (caster == null) return;

            foreach (var t in ticks)
            {
                var pseudoId = t.Dot.ActionId | DotPseudoActionMask;
                caster.TotalDamageDealt += t.Value;
                caster.DamageEvents.Add((tickMs, t.Value));
                RecordAbility(caster.DamageByAbility, pseudoId, t.Dot.DotName, t.Value);

                try
                {
                    _combatLog.Write(
                        "\"e\":\"sim_tick\"," +
                        "\"aid\":" + t.Dot.ActionId + "," +
                        "\"name\":\"" + CombatLog.Esc(t.Dot.DotName) + "\"," +
                        "\"tgt\":" + t.Dot.TargetId + "," +
                        "\"val\":" + t.Value + "," +
                        "\"crit\":" + (t.Crit ? "true" : "false") + "," +
                        "\"dh\":" + (t.Dh ? "true" : "false") + "," +
                        "\"baseline\":" + t.Dot.TickBaseline.ToString("F2") + "," +
                        "\"critRate\":" + t.Dot.CritRate.ToString("F3") + "," +
                        "\"dhRate\":" + t.Dot.DhRate.ToString("F3"));
                }
                catch { }
            }
        }
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
        if (ActiveSession != null) return;       // guard against double start
        _activeDots.Clear();                     // fresh DoT tracking per pull
        _dotSim.Reset();
        _localCritHits = _localDhHits = _localTotalHits = 0;
        var zone    = GetZoneName();
        var now     = DateTime.UtcNow;
        ActiveSession = new CombatSession
        {
            Id        = CombatSession.MakeId(zone, now),
            ZoneName  = zone,
            StartTime = now,
        };
        // Open per-session JSONL combat log. Lets us replay any fight to
        // diagnose missing-damage bugs (e.g. Stormbite not showing up).
        try
        {
            var local = _objectTable.LocalPlayer;
            var meta = "\"sessionId\":\"" + CombatLog.Esc(ActiveSession.Id) + "\"," +
                       "\"zone\":\"" + CombatLog.Esc(zone) + "\"," +
                       "\"startUtc\":\"" + now.ToString("o") + "\"," +
                       "\"localId\":" + (local?.EntityId ?? 0) + "," +
                       "\"localName\":\"" + CombatLog.Esc(local?.Name.TextValue ?? "") + "\"," +
                       "\"localJob\":" + (local is IBattleChara b ? b.ClassJob.RowId : 0);
            _combatLog.StartSession(ActiveSession.Id, now, meta);
        }
        catch (Exception ex) { _log.Warning($"DamageMeter: CombatLog start failed — {ex.Message}"); }

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

        _dotSim.EndSession();
        _combatLog.EndSession();
        _log.Info($"DamageMeter: Combat ended — {ActiveSession.Id} ({ActiveSession.FormattedDuration})");
        OnSessionEnded?.Invoke(ActiveSession);
        ActiveSession = null;
    }

    private void PruneTempSessions()
    {
        while (Store.TempSessions.Count > _config.MaxTempHistory)
            Store.TempSessions.RemoveAt(0);
    }

    // ── UseAction hook (button-press logger) ──────────────────────────────────
    // Fires for every action the local ActionManager attempts. We only LOG when
    // a session is active and the caller is the local player — enough to
    // reconstruct what the user pressed during a fight. The hook itself is
    // pass-through; we never alter the return or args.
    private unsafe bool OnUseAction(
        FFXIVClientStructs.FFXIV.Client.Game.ActionManager* thisPtr,
        FFXIVClientStructs.FFXIV.Client.Game.ActionType     actionType,
        uint                                                actionId,
        ulong                                               targetId,
        uint                                                extraParam,
        FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseActionMode mode,
        uint                                                comboRouteId,
        bool*                                               outOptAreaTargeted)
    {
        bool ret = false;
        try
        {
            ret = _useActionHook!.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        }
        catch (Exception ex) { _log.Error($"DamageMeter: UseAction original failed — {ex.Message}"); }

        try
        {
            if (ActiveSession != null)
            {
                var name = GetActionName(actionId);
                var json = "\"e\":\"use\"," +
                           "\"at\":" + (uint)actionType + "," +
                           "\"aid\":" + actionId + "," +
                           "\"an\":\"" + CombatLog.Esc(name) + "\"," +
                           "\"tgt\":" + targetId + "," +
                           "\"mode\":" + (uint)mode + "," +
                           "\"combo\":" + comboRouteId + "," +
                           "\"result\":" + (ret ? "true" : "false");
                _combatLog.Write(json);
            }
        }
        catch (Exception ex) { _log.Error($"DamageMeter: UseAction log failed — {ex.Message}"); }

        return ret;
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
        // Reset per-packet snapshot registers. Any local-cast Damage entry
        // inside ProcessEffects will refill these; the same packet's
        // ApplyStatusEffectTarget entry reads them.
        _packetInitDamage = 0;
        _packetInitCrit   = false;
        _packetInitDh     = false;

        try
        {
            if (header->NumTargets > 0)
            {
                // Retroactive session start. The InCombat ConditionFlag flips a
                // few hundred ms AFTER the first damage exchange — long enough
                // to lose an opening Stormbite (instant cast, applies DoT and
                // ~11k damage before the in-combat state goes live). Confirmed
                // against ACT + in-game combat log: Stormbite landed but the
                // plugin had ActiveSession == null and dropped the effect.
                // Fix: if local is the caster AND any effect entry is damage,
                // start the session here. The framework tick still handles the
                // common path; this just closes the pre-combat race.
                if (ActiveSession == null && HasLocalCasterDamage(casterEntityId, effects, header->NumTargets))
                    StartSession();

                if (ActiveSession != null)
                    ProcessEffects(casterEntityId, casterPtr, header, effects, targetEntityIds);
            }
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

    /// <summary>True if the caster is the local player and at least one effect
    /// entry on the first target is a damage kind. Cheap scan — only inspects
    /// the first target's 8 entries.</summary>
    private unsafe bool HasLocalCasterDamage(
        uint casterEntityId,
        ActionEffectHandler.TargetEffects* effects,
        ushort numTargets)
    {
        var localId = _objectTable.LocalPlayer?.EntityId ?? 0;
        if (localId == 0 || casterEntityId != localId) return false;
        var effectsBase = (byte*)effects;
        for (int e = 0; e < EffectsPerTarget; e++)
        {
            var kind = (EffectKind)effectsBase[e * EffectSize];
            if (kind == EffectKind.Damage || kind == EffectKind.BlockedDamage || kind == EffectKind.ParriedDamage)
                return true;
        }
        return false;
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
        var actionName = GetActionName(actionId);

        // ── Attribution: Limit Break > Pet > original caster ─────────────────
        // Limit Break actions are routed to a shared pseudo-combatant. Otherwise
        // if the caster is a pet (OwnerId points to a real entity), credit the
        // owner instead — fixes DRK Living Shadow, SMN Bahamut, SCH Eos, MCH
        // Automaton Queen.
        CombatantData? casterData;
        bool isLimitBreak = IsLimitBreakAction(actionId);
        if (isLimitBreak)
        {
            casterData = GetOrCreateLimitBreakCombatant(ActiveSession);
        }
        else
        {
            uint       effectiveCasterId  = casterEntityId;
            Character* effectiveCasterPtr = casterPtr;
            if (casterPtr != null)
            {
                var ownerId = casterPtr->OwnerId;
                if (ownerId != 0 && ownerId != NoOwnerSentinel)
                {
                    effectiveCasterId = ownerId;
                    var ownerObj = _objectTable.FirstOrDefault(o => o.EntityId == ownerId);
                    effectiveCasterPtr = ownerObj != null
                        ? (Character*)ownerObj.Address
                        : null;
                }
            }
            casterData = GetOrCreateCombatant(ActiveSession, effectiveCasterId, effectiveCasterPtr);
        }

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
                bool isSourceEntry = (param4 & 0x80) != 0;
                var casterName = casterData?.Name ?? $"#{casterEntityId}";
                var targetName = targetData?.Name ?? $"#{targetId}";
                var casterType = casterData?.Type.ToString() ?? "null";
                var targetType = targetData?.Type.ToString() ?? "null";
                _log.Debug($"[DM] kind={(byte)kind}(0x{(byte)kind:X2} {kind}) " +
                           $"action={actionId}({actionName}) val={value}" +
                           (isCritical ? " CRIT" : "") + (isDirectHit ? " DH" : "") +
                           $" caster={casterName}[{casterType}] target={targetName}[{targetType}]" +
                           (targetId == casterEntityId ? " SELF" : ""));

                // JSONL log entry per effect entry — captures kind, value, flags,
                // caster/target IDs. The log file is the source of truth for
                // diagnosing "why didn't X show up in the meter".
                try
                {
                    var json = "\"e\":\"effect\"," +
                               "\"kind\":" + (byte)kind + "," +
                               "\"aid\":" + actionId + "," +
                               "\"an\":\"" + CombatLog.Esc(actionName) + "\"," +
                               "\"caster\":" + casterEntityId + "," +
                               "\"target\":" + targetId + "," +
                               "\"effCaster\":" + (casterData?.EntityId ?? 0) + "," +
                               "\"val\":" + value + "," +
                               "\"crit\":" + (isCritical ? "true" : "false") + "," +
                               "\"dh\":" + (isDirectHit ? "true" : "false") + "," +
                               "\"src\":" + (isSourceEntry ? "true" : "false") + "," +
                               "\"slot\":" + e + "," +
                               "\"isAA\":" + (IsAutoAttackAction(actionId) ? "true" : "false");
                    _combatLog.Write(json);
                }
                catch { /* never fail combat over a log entry */ }

                switch (kind)
                {
                    case EffectKind.Damage:
                    case EffectKind.BlockedDamage:
                    case EffectKind.ParriedDamage:
                    {
                        bool killingBlow = IsKillingBlow(targetId, value);
                        bool isSelfHit   = targetId == casterEntityId;

                        // Remember the initial-hit characteristics for this
                        // packet so the ApplyStatusEffectTarget slot (which
                        // arrives later in the same effect array) can hand the
                        // simulator a clean stat snapshot.
                        var localIdHit = _objectTable.LocalPlayer?.EntityId ?? 0;
                        if (casterEntityId == localIdHit && !isSelfHit)
                        {
                            _packetInitDamage = value;
                            _packetInitCrit   = isCritical;
                            _packetInitDh     = isDirectHit;

                            // Rolling-average crit/DH rate from observed local
                            // hits. Used by the simulator for per-tick rolls.
                            _localTotalHits++;
                            if (isCritical)  _localCritHits++;
                            if (isDirectHit) _localDhHits++;
                            if (_localTotalHits >= 5)
                            {
                                _localCritRate = (double)_localCritHits / _localTotalHits;
                                _localDhRate   = (double)_localDhHits   / _localTotalHits;
                            }
                        }

                        // Only push auto-attack values into the dedup buffer — those
                        // are the only ActionEffects that share FlyTextKind with DoT
                        // ticks. Direct hits fire `Damage*` FlyText which the plugin
                        // doesn't dedup against, so pushing them just created false
                        // collisions that swallowed real DoT ticks.
                        if (IsAutoAttackAction(actionId))
                            RecordRecentHit(_recentDamageHits, value, tickMs);

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
                        // Target died on this hit — stop any DoTs we're simulating
                        // against it. Without this the scheduler ticks past the
                        // kill: a 57s pull showed +14% overcount before this gate
                        // (49/48 sim ticks vs ACT's 43/42 actual).
                        if (killingBlow)
                            _dotSim.OnTargetDied(targetId);
                        break;
                    }

                    case EffectKind.Heal:
                    {
                        // Push the heal into the FlyText dedup buffer regardless
                        // of attribution, so the matching FlyText is skipped.
                        RecordRecentHit(_recentHealHits, value, tickMs);

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

                    case EffectKind.ApplyStatusEffectTarget:
                    {
                        // The user just applied a status to an enemy. If the
                        // caster is the local player AND we have a recorded
                        // initial hit value from the same packet, hand it to
                        // the DoT simulator: the simulator will schedule the
                        // tick stream and produce per-tick damage events the
                        // framework loop drains every frame.
                        var localId = _objectTable.LocalPlayer?.EntityId ?? 0;
                        if (casterEntityId == localId && targetId != casterEntityId
                            && targetData?.Type == CombatantType.Enemy
                            && DoTSimulator.IsSimulatorRelevant(actionId))
                        {
                            _dotSim.OnApply(
                                targetId:        targetId,
                                casterId:        localId,
                                actionId:        actionId,
                                initialHitValue: _packetInitDamage,
                                initialCrit:     _packetInitCrit,
                                initialDh:       _packetInitDh,
                                casterCritRate:  _localCritRate,
                                casterDhRate:    _localDhRate,
                                tickMs:          tickMs);
                        }

                        // Keep the legacy generic tracker for now so the old
                        // FlyText fallback (if it ever fires) still has a
                        // place to attribute. Harmless when the simulator is
                        // running.
                        if (casterEntityId == localId && targetId != casterEntityId
                            && targetData?.Type == CombatantType.Enemy)
                        {
                            RegisterActiveDot(targetId, actionId, actionName, tickMs);
                        }
                        break;
                    }
                }
            }
        }
    }

    // ── Active-DoT tracking (local player only) ──────────────────────────────
    private void RegisterActiveDot(uint targetId, uint actionId, string actionName, long tickMs)
    {
        // Same (target, action) re-applied (e.g. Iron Jaws refresh) — bump the
        // expiry forward and reset the tick clock so the next tick credits to
        // this fresh application.
        for (int i = 0; i < _activeDots.Count; i++)
        {
            var d = _activeDots[i];
            if (d.TargetId == targetId && d.ActionId == actionId)
            {
                d.AppliedAtMs  = tickMs;
                d.LastTickAtMs = tickMs;
                d.ExpiresAtMs  = tickMs + DefaultDotDurationMs;
                return;
            }
        }
        _activeDots.Add(new ActiveDot
        {
            TargetId     = targetId,
            ActionId     = actionId,
            ActionName   = actionName,
            AppliedAtMs  = tickMs,
            LastTickAtMs = tickMs,
            ExpiresAtMs  = tickMs + DefaultDotDurationMs,
        });
    }

    /// <summary>Returns the active DoT whose last tick is the most overdue, or
    /// null if no DoT is active. Also prunes expired entries.</summary>
    private ActiveDot? PickActiveDotForTick(long tickMs)
    {
        for (int i = _activeDots.Count - 1; i >= 0; i--)
            if (_activeDots[i].ExpiresAtMs < tickMs)
                _activeDots.RemoveAt(i);

        ActiveDot? oldest = null;
        foreach (var d in _activeDots)
            if (oldest == null || d.LastTickAtMs < oldest.LastTickAtMs)
                oldest = d;
        return oldest;
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

    // ── FlyText DoT/HoT tick capture ──────────────────────────────────────────
    //
    // FFXIV doesn't deliver status-tick damage through ActionEffectHandler.Receive
    // (the hook above). Instead, the game's status-effect tick processor fires the
    // floating "DoT damage" number directly via the FlyText subsystem. Dalamud
    // exposes that as IFlyTextGui.FlyTextCreated.
    //
    // FlyTextKind.AutoAttackOrDot{,Dh,Crit,CritDh} covers both auto-attacks AND
    // DoT ticks. We disambiguate by the `icon` field: DoT ticks always carry the
    // applied status effect's icon (non-zero), while auto-attacks pass `icon == 0`.
    //
    // FlyText events do NOT carry source / target entity IDs. So we can only
    // credit the local player. Party-member DoT attribution is the §9.7 gap —
    // tracked in RESEARCH.md for a future Ravahn-DoTSimulator port.
    //
    // Healing FlyText kinds (Healing, HealingCrit) cover both direct heals and
    // HoT ticks — the icon trick works the same way.
    private void OnFlyTextCreated(
        ref FlyTextKind kind,
        ref int val1,
        ref int val2,
        ref SeString text1,
        ref SeString text2,
        ref uint color,
        ref uint icon,
        ref uint damageTypeIcon,
        ref float yOffset,
        ref bool handled)
    {
        try
        {
            if (ActiveSession == null) return;

            bool isDamageTick = kind == FlyTextKind.AutoAttackOrDot
                             || kind == FlyTextKind.AutoAttackOrDotDh
                             || kind == FlyTextKind.AutoAttackOrDotCrit
                             || kind == FlyTextKind.AutoAttackOrDotCritDh;
            bool isHealTick   = kind == FlyTextKind.Healing
                             || kind == FlyTextKind.HealingCrit;

            if (!isDamageTick && !isHealTick)
            {
                // Still log uninteresting kinds so the combat log captures every
                // FlyText event — useful when an expected ability is missing.
                try
                {
                    _combatLog.Write(
                        "\"e\":\"flytext\"," +
                        "\"kind\":\"" + kind + "\"," +
                        "\"v1\":" + val1 + "," +
                        "\"v2\":" + val2 + "," +
                        "\"icon\":" + icon + "," +
                        "\"dti\":" + damageTypeIcon + "," +
                        "\"handled\":false");
                }
                catch { }
                return;
            }
            if (val1 <= 0) return;

            var tickMs = (long)(DateTime.UtcNow - ActiveSession.StartTime).TotalMilliseconds;

            // Value-based dedup against the ring buffer ProcessEffects fills as
            // it records auto-attack hits. A FlyText that matches a recent
            // auto-attack ActionEffect IS that ActionEffect's flytext — skip it.
            // No match = a tick the hook didn't see, i.e. a DoT/HoT.
            var dedupBuf  = isHealTick ? _recentHealHits : _recentDamageHits;
            bool dedupHit = TryConsumeRecentHit(dedupBuf, val1, tickMs);

            try
            {
                _combatLog.Write(
                    "\"e\":\"flytext\"," +
                    "\"kind\":\"" + kind + "\"," +
                    "\"v1\":" + val1 + "," +
                    "\"v2\":" + val2 + "," +
                    "\"icon\":" + icon + "," +
                    "\"dti\":" + damageTypeIcon + "," +
                    "\"dedup\":" + (dedupHit ? "true" : "false") + "," +
                    "\"credited\":" + (dedupHit ? "false" : "true"));
            }
            catch { }

            if (dedupHit) return;

            var local = _objectTable.LocalPlayer;
            var localId = local?.EntityId ?? 0;
            if (localId == 0) return;

            unsafe
            {
                var localPtr = (Character*)(local?.Address ?? IntPtr.Zero);
                var caster = GetOrCreateCombatant(ActiveSession, localId, localPtr);
                if (caster == null) return;

                long value = val1;

                if (isDamageTick)
                {
                    // CREDITING DISABLED. BROKEN.md §6 + §7: FlyText for damage
                    // can arrive 4–6+ seconds after its ActionEffect because the
                    // game queues the popups visually. The dedup window can't
                    // be stretched far enough without making collisions worse;
                    // any FlyText that escapes is at this point untrustworthy.
                    // Worse, the previous per-DoT fallback then credited those
                    // leaked auto-attack values to whichever DoT was active —
                    // Stormbite/Caustic Bite rows got polluted with Shot crits.
                    //
                    // DoT damage capture is being moved entirely onto the chat
                    // log path (IChatGui). For now we drop unmatched damage
                    // FlyText silently rather than mis-credit anything.
                }
                else // isHealTick
                {
                    // FlyText doesn't tell us overheal, so log full as actual heal.
                    // The HoT target is unknown from the event; we can't compute
                    // overheal without it. Accept the inflation; refinement later.
                    caster.TotalHealingDone += value;
                    caster.HealingEvents.Add((tickMs, value));
                    RecordAbility(caster.HealingByAbility, HotPseudoActionId,
                        "Heal over Time", value);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"DamageMeter: FlyText hook error — {ex.Message}");
        }
    }

    // ── Limit Break detection ─────────────────────────────────────────────────
    // Looks up the action's ActionCategory in Lumina. Category 8 = Limit Break.
    // Cached because Lumina row reads are expensive and we hit this per effect.
    private bool IsLimitBreakAction(uint actionId)
    {
        if (_limitBreakCache.TryGetValue(actionId, out var cached)) return cached;
        bool result = false;
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var row   = sheet?.GetRow(actionId);
            result = row?.ActionCategory.RowId == 8;
        }
        catch { /* unknown action id — leave false */ }
        _limitBreakCache[actionId] = result;
        return result;
    }

    // ActionCategory.RowId == 1 in Lumina = AutoAttack. These (and only these)
    // produce FlyTextKind.AutoAttackOrDot{*}, the same kind DoT ticks use.
    private bool IsAutoAttackAction(uint actionId)
    {
        if (_autoAttackCache.TryGetValue(actionId, out var cached)) return cached;
        bool result = false;
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var row   = sheet?.GetRow(actionId);
            result = row?.ActionCategory.RowId == 1;
        }
        catch { /* unknown action id — leave false */ }
        _autoAttackCache[actionId] = result;
        return result;
    }

    private CombatantData GetOrCreateLimitBreakCombatant(CombatSession session)
    {
        if (session.Combatants.TryGetValue(LimitBreakEntityId, out var existing))
            return existing;
        var data = new CombatantData
        {
            EntityId   = LimitBreakEntityId,
            Name       = "Limit Break",
            World      = "",
            ClassJobId = 0,
            Type       = CombatantType.PartyMember, // grouped with party so it shows in the main bar
        };
        session.Combatants[LimitBreakEntityId] = data;
        return data;
    }

    // ── FlyText/ActionEffect dedup helpers ────────────────────────────────────
    private static void RecordRecentHit(List<(long Value, long TickMs)> buf, long value, long tickMs)
    {
        TrimExpired(buf, tickMs);
        buf.Add((value, tickMs));
    }

    private static bool TryConsumeRecentHit(List<(long Value, long TickMs)> buf, long value, long tickMs)
    {
        TrimExpired(buf, tickMs);
        for (int i = 0; i < buf.Count; i++)
        {
            if (buf[i].Value == value)
            {
                buf.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private static void TrimExpired(List<(long Value, long TickMs)> buf, long nowMs)
    {
        while (buf.Count > 0 && nowMs - buf[0].TickMs > DedupWindowMs)
            buf.RemoveAt(0);
    }

    // ── Chat log → DoT capture (diagnostic stage) ─────────────────────────────
    //
    // BROKEN.md §6 explains why we're moving DoT capture off FlyText. The
    // in-game combat log shows DoT ticks reliably regardless of pop-up text
    // settings, and `IChatGui.ChatMessage` mirrors it. For now we just dump
    // every chat message during an active session into the combat-log JSONL
    // so the structure of a real Stormbite/Caustic Bite tick line is visible.
    // Once we have a captured sample, we'll write the typed parser and route
    // DoT damage through this path instead of FlyText.
    // ── Log-message stream → DoT tick capture ────────────────────────────────
    //
    // ILogMessage is the lower-level event. It fires for every combat log
    // entry the game generates, including ones the user's chat filter
    // suppresses from the visible chat window. It also carries structured
    // data: SourceEntity, TargetEntity, and a Parameters list (no string
    // parsing needed). The chat-log capture in the 09:52 Tower of Zot pull
    // showed zero DoT tick chat lines — strongly suggesting the user has
    // continuous-damage entries filtered out client-side. LogMessage should
    // catch them anyway.
    //
    // Diagnostic only for this commit: dump LogMessageId, source/target
    // names + ObjStrIds, and the parameter list, so the next pull tells us
    // exactly which LogMessageId(s) carry Stormbite/Caustic Bite ticks and
    // what the parameter order is. Real attribution wires in after that.
    private void OnLogMessage(Dalamud.Game.Chat.ILogMessage msg)
    {
        try
        {
            if (ActiveSession == null) return;

            var srcName = msg.SourceEntity?.Name.ToString() ?? "";
            var tgtName = msg.TargetEntity?.Name.ToString() ?? "";
            var srcId   = msg.SourceEntity?.ObjStrId ?? 0;
            var tgtId   = msg.TargetEntity?.ObjStrId ?? 0;

            // Parameters are unsigned-int-ish values; the damage value, action
            // id, and similar tend to be in here. We dump up to the first 8
            // to keep payload size sensible.
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            int n = 0;
            if (msg.Parameters != null)
            {
                foreach (var p in msg.Parameters)
                {
                    if (n++ > 0) sb.Append(',');
                    sb.Append(p);
                    if (n >= 8) break;
                }
            }
            sb.Append(']');

            _combatLog.Write(
                "\"e\":\"log\"," +
                "\"id\":" + msg.LogMessageId + "," +
                "\"srcName\":\"" + CombatLog.Esc(srcName) + "\"," +
                "\"srcId\":" + srcId + "," +
                "\"tgtName\":\"" + CombatLog.Esc(tgtName) + "\"," +
                "\"tgtId\":" + tgtId + "," +
                "\"params\":" + sb);
        }
        catch (Exception ex) { _log.Error($"DamageMeter: LogMessage capture failed — {ex.Message}"); }
    }

    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage chat)
    {
        try
        {
            if (ActiveSession == null) return;

            // No filter — capture every chat type while a session is active so
            // we can identify the exact LogKind for DoT/HoT tick lines. Filter
            // refinement is a later step once the structure is known.
            var type        = chat.LogKind;
            var raw         = (uint)type;
            var senderText  = chat.Sender?.TextValue ?? "";
            var messageText = chat.Message?.TextValue ?? "";

            // Compact payload-type list helps identify structure (e.g. is the
            // damage value its own payload, is there a StatusPayload, etc.).
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            bool first = true;
            if (chat.Message != null)
            {
                foreach (var p in chat.Message.Payloads)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(p.Type).Append('"');
                }
            }
            sb.Append(']');

            _combatLog.Write(
                "\"e\":\"chat\"," +
                "\"type\":" + raw + "," +
                "\"typeName\":\"" + CombatLog.Esc(type.ToString()) + "\"," +
                "\"src\":" + (int)chat.SourceKind + "," +
                "\"tgt\":" + (int)chat.TargetKind + "," +
                "\"sender\":\"" + CombatLog.Esc(senderText) + "\"," +
                "\"msg\":\"" + CombatLog.Esc(messageText) + "\"," +
                "\"payloads\":" + sb);
        }
        catch (Exception ex) { _log.Error($"DamageMeter: Chat log capture failed — {ex.Message}"); }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _framework.Update             -= OnFrameworkUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _flyTextGui.FlyTextCreated    -= OnFlyTextCreated;
        _chatGui.ChatMessage          -= OnChatMessage;
        _chatGui.LogMessage           -= OnLogMessage;
        if (ActiveSession != null) EndSession();
        _hook?.Dispose();
        _useActionHook?.Dispose();
        _combatLog.Dispose();
        _log.Info("DamageMeter: CombatTracker disposed.");
    }
}
