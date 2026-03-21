using System;
using System.Linq;

using Dalamud.Plugin.Ipc;

using Newtonsoft.Json;

namespace DamageMeter;

/// <summary>
/// Exposes DamageMeter live data to other plugins via Dalamud IPC.
/// Consumed by DamageMeterUmbra to avoid a direct cross-context DLL reference.
///
/// Providers:
///   "DamageMeter.GetActiveSummary"  → Func&lt;string&gt;  JSON summary of the active session
///   "DamageMeter.GetPartyMarkers"   → Func&lt;string&gt;  JSON array of party member combat data
/// </summary>
public sealed class IpcBridge : IDisposable
{
    private readonly Plugin _plugin;
    private readonly ICallGateProvider<string> _providerActiveSummary;
    private readonly ICallGateProvider<string> _providerPartyMarkers;

    public IpcBridge(Plugin plugin)
    {
        _plugin = plugin;

        _providerActiveSummary = Plugin.PluginInterface.GetIpcProvider<string>("DamageMeter.GetActiveSummary");
        _providerActiveSummary.RegisterFunc(GetActiveSummary);

        _providerPartyMarkers = Plugin.PluginInterface.GetIpcProvider<string>("DamageMeter.GetPartyMarkers");
        _providerPartyMarkers.RegisterFunc(GetPartyMarkers);

        Plugin.Log.Info("DamageMeter: IPC bridge initialized.");
    }

    // ── DamageMeter.GetActiveSummary ──────────────────────────────────────────
    //
    // Returns JSON:
    //   { "isActive": false }
    // or:
    //   {
    //     "isActive": true,
    //     "formattedDuration": "5:23",
    //     "localEntityId": 12345,
    //     "localClassJobId": 19,
    //     "local": { "dps":16200,"hps":0,"totalDamage":5000000,"totalHealing":0,"totalDamageTaken":45000 },
    //     "top":   { "dps":18450,"hps":3200,"totalDamage":6000000,"totalHealing":500000,"totalDamageTaken":200000 }
    //   }

    private string GetActiveSummary()
    {
        try
        {
            var session = _plugin.Tracker.ActiveSession;
            if (session == null) return """{"isActive":false}""";

            var dur     = session.DurationSeconds;
            var localId = Plugin.ObjectTable?.LocalPlayer?.EntityId ?? 0;
            session.Combatants.TryGetValue(localId, out var local);

            var allByDps  = session.GetSortedCombatants(MeterType.DPS);
            var allByHps  = session.GetSortedCombatants(MeterType.HPS);
            var allByDmg  = session.GetSortedCombatants(MeterType.DamageDealt);
            var allByHeal = session.GetSortedCombatants(MeterType.HealingDone);
            var allByTake = session.GetSortedCombatants(MeterType.DamageTaken);

            var result = new
            {
                isActive          = true,
                formattedDuration = session.FormattedDuration,
                localEntityId     = localId,
                localClassJobId   = local?.ClassJobId ?? 0,
                local = new
                {
                    dps            = (long)(local?.GetDps(dur) ?? 0),
                    hps            = (long)(local?.GetHps(dur) ?? 0),
                    totalDamage    = local?.TotalDamageDealt    ?? 0L,
                    totalHealing   = local?.TotalHealingDone    ?? 0L,
                    totalDamageTaken = local?.TotalDamageTaken  ?? 0L,
                },
                top = new
                {
                    dps            = allByDps.Count  > 0 ? (long)allByDps[0].GetDps(dur)   : 0L,
                    hps            = allByHps.Count  > 0 ? (long)allByHps[0].GetHps(dur)   : 0L,
                    totalDamage    = allByDmg.Count  > 0 ? allByDmg[0].TotalDamageDealt    : 0L,
                    totalHealing   = allByHeal.Count > 0 ? allByHeal[0].TotalHealingDone   : 0L,
                    totalDamageTaken = allByTake.Count > 0 ? allByTake[0].TotalDamageTaken : 0L,
                }
            };

            return JsonConvert.SerializeObject(result);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DamageMeter IPC GetActiveSummary error: {ex.Message}");
            return """{"isActive":false}""";
        }
    }

    // ── DamageMeter.GetPartyMarkers ───────────────────────────────────────────
    //
    // Returns JSON array (empty when not in combat):
    //   [{ "entityId":12345,"name":"...","classJobId":19,"dps":18450.0,"hps":0.0,
    //      "totalDamage":5000000,"totalHealing":0 }, ...]

    private string GetPartyMarkers()
    {
        try
        {
            var session = _plugin.Tracker.ActiveSession;
            if (session == null) return "[]";

            var dur = session.DurationSeconds;
            var markers = session.Combatants.Values
                .Where(c => c.Type == CombatantType.PartyMember)
                .Select(c => new
                {
                    entityId     = c.EntityId,
                    name         = c.Name,
                    classJobId   = (int)c.ClassJobId,
                    dps          = c.GetDps(dur),
                    hps          = c.GetHps(dur),
                    totalDamage  = c.TotalDamageDealt,
                    totalHealing = c.TotalHealingDone,
                })
                .ToList();

            return JsonConvert.SerializeObject(markers);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"DamageMeter IPC GetPartyMarkers error: {ex.Message}");
            return "[]";
        }
    }

    public void Dispose()
    {
        _providerActiveSummary.UnregisterFunc();
        _providerPartyMarkers.UnregisterFunc();
        Plugin.Log.Info("DamageMeter: IPC bridge disposed.");
    }
}
