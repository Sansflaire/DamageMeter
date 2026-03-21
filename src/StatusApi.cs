using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DamageMeter;

/// <summary>
/// Local HTTP API — exposes all DamageMeter state as JSON on http://localhost:17778/.
///
/// Endpoints:
///   GET /version                          → plugin version + loaded flag
///   GET /state                            → full snapshot (version + combat state + config)
///   GET /session                          → active session full detail (null if not in combat)
///   GET /session/summary                  → active session totals only (lighter than full detail)
///   GET /session/combatants               → all combatants in active session ordered by damage dealt
///   GET /session/combatant/{entityId}     → one combatant from active session, with ability breakdowns
///   GET /history                          → all stored sessions (temp + saved), metadata only
///   GET /history/temp                     → temp session list, metadata only
///   GET /history/saved                    → saved session list, metadata only
///   GET /history/{id}                     → one stored session, full detail + combatants
///   GET /history/{id}/combatants          → combatants from a stored session, ordered by damage dealt
///   GET /history/{id}/combatant/{eId}     → one combatant from a stored session, with ability breakdowns
///   GET /config                           → all configuration values + per-meter bar colors
///   GET /get/all                          → alias for /config (matches FFXIV-TV convention)
///
///   GET /set/meter?v=DamageDealt          → change current meter type
///   GET /set/showfullvalues?v=true        → toggle full numeric values
///   GET /set/showpercentage?v=true        → toggle % of group total
///   GET /set/showplayerserver?v=true      → toggle @Server suffix
///   GET /set/showfullname?v=true          → toggle full vs initials display
///   GET /set/showjobicon?v=true           → toggle job icon in meter rows
///   GET /set/lockwindow?v=true            → lock/unlock window position + size
///   GET /set/opacity?v=0.92              → window opacity (0.1 – 1.0)
///   GET /set/rowheight?v=22              → meter row height in px (16 – 40)
///   GET /set/maxtemphistory?v=20         → max auto-saved sessions (1 – 50)
///
///   GET /action/session/save?id={id}     → move a temp session to saved
///   GET /action/session/delete?id={id}   → delete a session (temp or saved)
///
///   GET /meter/image                     → current meter canvas as PNG (image/png)
/// </summary>
internal sealed class StatusApi : IDisposable
{
    private const string Prefix = "http://localhost:17778/";

    private readonly HttpListener _listener = new();
    private readonly Thread       _thread;
    private volatile bool         _running;

    private readonly Plugin _plugin;

    private CombatTracker Tracker => _plugin.Tracker;
    private Configuration Config  => _plugin.Config;

    internal StatusApi(Plugin plugin)
    {
        _plugin = plugin;
        _listener.Prefixes.Add(Prefix);
        try
        {
            _listener.Start();
            _running = true;
            _thread  = new Thread(Loop) { IsBackground = true, Name = "DamageMeter StatusApi" };
            _thread.Start();
            Plugin.Log.Info($"DamageMeter: StatusApi listening on {Prefix}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"DamageMeter: StatusApi failed to start — {ex.Message}");
            _thread = new Thread(() => { }); // dummy so field is always assigned
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }

    // ── Request loop ──────────────────────────────────────────────────────────

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }
            try { Handle(ctx); }
            catch { /* never crash the background thread */ }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";

        // PNG image endpoint — handled separately (binary response)
        if (path == "/meter/image")
        {
            var png = _plugin._mainWindow.Meter.GetPngBytes();
            if (png == null) { Respond(ctx, 503, "{\"error\":\"no frame rendered yet\"}"); return; }
            RespondPng(ctx, png);
            return;
        }

        string? json;

        if      (path.StartsWith("/set/"))     json = HandleSet(path, ctx.Request.Url);
        else if (path.StartsWith("/action/"))  json = HandleAction(path, ctx.Request.Url);
        else if (path.StartsWith("/history/")) json = HandleHistory(path);
        else if (path.StartsWith("/session/")) json = HandleSession(path);
        else
            json = path switch
            {
                "/version" or "/status" or "" => BuildVersion(),
                "/state"                       => BuildState(),
                "/session"                     => BuildActiveSession(),
                "/history"                     => BuildHistory(),
                "/config" or "/get/all" or "/get" => BuildConfig(),
                _                              => null,
            };

        if (json == null) { Respond(ctx, 404, "{\"error\":\"not found\"}"); return; }
        Respond(ctx, 200, json);
    }

    // ── /session sub-routes ───────────────────────────────────────────────────

    private string? HandleSession(string path)
    {
        if (path == "/session/combatants") return BuildActiveSessionCombatants();
        if (path == "/session/summary")    return BuildActiveSessionSummary();

        // /session/combatant/{entityId}
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[1] == "combatant" && uint.TryParse(parts[2], out uint eid))
        {
            var s = Tracker.ActiveSession;
            if (s == null) return "{\"active\":false}";
            return BuildCombatantDetail(s, eid);
        }

        return null;
    }

    // ── /history sub-routes ───────────────────────────────────────────────────

    private string? HandleHistory(string path)
    {
        if (path == "/history/temp")  return BuildHistoryTemp();
        if (path == "/history/saved") return BuildHistorySaved();

        // /history/{id}  or  /history/{id}/combatants  or  /history/{id}/combatant/{entityId}
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // parts: [0]="history", [1]=sessionId, [2]?="combatants"/"combatant", [3]?=entityId
        if (parts.Length < 2) return null;

        var session = FindSession(parts[1]);
        if (session == null) return "{\"error\":\"session not found\"}";

        if (parts.Length == 2) return BuildSessionDetail(session);
        if (parts.Length == 3 && parts[2] == "combatants") return BuildSessionCombatants(session);
        if (parts.Length == 4 && parts[2] == "combatant" && uint.TryParse(parts[3], out uint eid2))
            return BuildCombatantDetail(session, eid2);

        return null;
    }

    // ── /action endpoints ─────────────────────────────────────────────────────

    private string HandleAction(string path, Uri? url)
    {
        var q = ParseQuery(url?.Query);
        return path switch
        {
            "/action/session/save"   when q.TryGetValue("id", out var sid) => ActionSaveSession(sid),
            "/action/session/delete" when q.TryGetValue("id", out var did) => ActionDeleteSession(did),
            _ => "{\"error\":\"unknown action or missing parameter\"}"
        };
    }

    private string ActionSaveSession(string id)
    {
        var session = FindSession(id);
        if (session == null) return "{\"error\":\"session not found\"}";
        if (session.IsSaved) return "{\"saved\":true,\"alreadySaved\":true}";
        Tracker.SaveSession(session);
        return "{\"saved\":true,\"alreadySaved\":false}";
    }

    private string ActionDeleteSession(string id)
    {
        var session = FindSession(id);
        if (session == null) return "{\"error\":\"session not found\"}";
        Tracker.DeleteSession(session);
        return "{\"deleted\":true}";
    }

    // ── /set endpoints ────────────────────────────────────────────────────────

    private string HandleSet(string path, Uri? url)
    {
        var q = ParseQuery(url?.Query);
        var c = Config;

        switch (path)
        {
            case "/set/meter":
                if (q.TryGetValue("v", out var mtStr) && Enum.TryParse<MeterType>(mtStr, true, out var mt))
                { c.CurrentMeter = mt; Save(); }
                return $"{{\"meter\":{Q(c.CurrentMeter.ToString())}}}";

            case "/set/showfullvalues":
                if (TryBoolQ(q, "v", out bool sfv)) { c.ShowFullValues = sfv; Save(); }
                return $"{{\"showFullValues\":{B(c.ShowFullValues)}}}";

            case "/set/showpercentage":
                if (TryBoolQ(q, "v", out bool sp)) { c.ShowPercentage = sp; Save(); }
                return $"{{\"showPercentage\":{B(c.ShowPercentage)}}}";

            case "/set/showplayerserver":
                if (TryBoolQ(q, "v", out bool sps)) { c.ShowPlayerServer = sps; Save(); }
                return $"{{\"showPlayerServer\":{B(c.ShowPlayerServer)}}}";

            case "/set/showfullname":
                if (TryBoolQ(q, "v", out bool sfn)) { c.ShowFullName = sfn; Save(); }
                return $"{{\"showFullName\":{B(c.ShowFullName)}}}";

            case "/set/showjobicon":
                if (TryBoolQ(q, "v", out bool sji)) { c.ShowJobIcon = sji; Save(); }
                return $"{{\"showJobIcon\":{B(c.ShowJobIcon)}}}";

            case "/set/lockwindow":
                if (TryBoolQ(q, "v", out bool lw)) { c.LockWindow = lw; Save(); }
                return $"{{\"lockWindow\":{B(c.LockWindow)}}}";

            case "/set/opacity":
                if (TryFloatQ(q, "v", out float op)) { c.Opacity = Math.Clamp(op, 0.1f, 1f); Save(); }
                return $"{{\"opacity\":{F(c.Opacity)}}}";

            case "/set/rowheight":
                if (TryFloatQ(q, "v", out float rh)) { c.RowHeight = Math.Clamp(rh, 16f, 40f); Save(); }
                return $"{{\"rowHeight\":{F(c.RowHeight)}}}";

            case "/set/maxtemphistory":
                if (q.TryGetValue("v", out var mthStr) && int.TryParse(mthStr, out int maxH))
                { c.MaxTempHistory = Math.Clamp(maxH, 1, 50); Save(); }
                return $"{{\"maxTempHistory\":{c.MaxTempHistory}}}";

            default:
                return "{\"error\":\"unknown set endpoint\"}";
        }
    }

    // ── Top-level section builders ────────────────────────────────────────────

    private string BuildVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        return $"{{\"version\":\"{v}\",\"loaded\":true,\"port\":17778}}";
    }

    private string BuildState()
    {
        var v     = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        var store = Tracker.Store;
        var s     = Tracker.ActiveSession;
        return $$"""
        {
          "version": "{{v}}",
          "loaded": true,
          "inCombat": {{B(s != null)}},
          "tempSessions": {{store.TempSessions.Count}},
          "savedSessions": {{store.SavedSessions.Count}},
          "config": {{BuildConfig()}},
          "activeSession": {{(s != null ? BuildSessionDetail(s) : "null")}}
        }
        """;
    }

    private string BuildActiveSession()
    {
        var s = Tracker.ActiveSession;
        return s != null ? BuildSessionDetail(s) : "{\"active\":false}";
    }

    private string BuildActiveSessionSummary()
    {
        var s = Tracker.ActiveSession;
        return s != null ? BuildSessionSummaryJson(s) : "{\"active\":false}";
    }

    private string BuildActiveSessionCombatants()
    {
        var s = Tracker.ActiveSession;
        return s != null ? BuildSessionCombatants(s) : "{\"active\":false,\"combatants\":[]}";
    }

    private string BuildHistory()
    {
        var store = Tracker.Store;
        var all   = store.TempSessions.Concat(store.SavedSessions)
                        .OrderByDescending(s => s.StartTime)
                        .Select(SessionMetaJson);
        return $$"""
        {
          "tempCount": {{store.TempSessions.Count}},
          "savedCount": {{store.SavedSessions.Count}},
          "sessions": [{{string.Join(",", all)}}]
        }
        """;
    }

    private string BuildHistoryTemp()
    {
        var sessions = Tracker.Store.TempSessions
            .OrderByDescending(s => s.StartTime)
            .Select(SessionMetaJson).ToList();
        return $"{{\"count\":{sessions.Count},\"sessions\":[{string.Join(",", sessions)}]}}";
    }

    private string BuildHistorySaved()
    {
        var sessions = Tracker.Store.SavedSessions
            .OrderByDescending(s => s.StartTime)
            .Select(SessionMetaJson).ToList();
        return $"{{\"count\":{sessions.Count},\"sessions\":[{string.Join(",", sessions)}]}}";
    }

    private string BuildConfig()
    {
        var c = Config;
        var barColors = string.Join(",", c.BarColors.Select(kvp =>
            $"{{\"type\":{Q(kvp.Key.ToString())},\"displayName\":{Q(kvp.Key.DisplayName())},\"abgr\":{kvp.Value},\"hex\":{Q($"#{kvp.Value:X8}")}}}"));
        return $$"""
        {
          "meter": {{Q(c.CurrentMeter.ToString())}},
          "showFullValues": {{B(c.ShowFullValues)}},
          "showPercentage": {{B(c.ShowPercentage)}},
          "showPlayerServer": {{B(c.ShowPlayerServer)}},
          "showFullName": {{B(c.ShowFullName)}},
          "showJobIcon": {{B(c.ShowJobIcon)}},
          "lockWindow": {{B(c.LockWindow)}},
          "opacity": {{F(c.Opacity)}},
          "rowHeight": {{F(c.RowHeight)}},
          "maxTempHistory": {{c.MaxTempHistory}},
          "windowStyle": {{Q(c.Style.ToString())}},
          "barColors": [{{barColors}}]
        }
        """;
    }

    // ── Session builders ──────────────────────────────────────────────────────

    private static string SessionMetaJson(CombatSession s)
    {
        return $$"""
        {
          "id": {{Q(s.Id)}},
          "zoneName": {{Q(s.ZoneName)}},
          "startTime": {{Q(s.StartTime.ToString("O"))}},
          "endTime": {{(s.EndTime.HasValue ? Q(s.EndTime.Value.ToString("O")) : "null")}},
          "durationSeconds": {{s.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}},
          "formattedDuration": {{Q(s.FormattedDuration)}},
          "isActive": {{B(s.IsActive)}},
          "isSaved": {{B(s.IsSaved)}},
          "combatantCount": {{s.Combatants.Count}}
        }
        """;
    }

    private static string BuildSessionDetail(CombatSession s)
    {
        var dur      = s.DurationSeconds;
        var totals   = string.Join(",", Enum.GetValues<MeterType>().Select(mt =>
            $"{{\"type\":{Q(mt.ToString())},\"displayName\":{Q(mt.DisplayName())},\"total\":{s.GetTotal(mt)}}}"));
        var combatants = string.Join(",", s.Combatants.Values
            .OrderByDescending(c => c.TotalDamageDealt)
            .Select(c => CombatantSummaryJson(c, dur)));
        return $$"""
        {
          "id": {{Q(s.Id)}},
          "zoneName": {{Q(s.ZoneName)}},
          "startTime": {{Q(s.StartTime.ToString("O"))}},
          "endTime": {{(s.EndTime.HasValue ? Q(s.EndTime.Value.ToString("O")) : "null")}},
          "durationSeconds": {{dur.ToString("F1", CultureInfo.InvariantCulture)}},
          "formattedDuration": {{Q(s.FormattedDuration)}},
          "isActive": {{B(s.IsActive)}},
          "isSaved": {{B(s.IsSaved)}},
          "combatantCount": {{s.Combatants.Count}},
          "totals": [{{totals}}],
          "combatants": [{{combatants}}]
        }
        """;
    }

    private static string BuildSessionSummaryJson(CombatSession s)
    {
        var totals = string.Join(",", Enum.GetValues<MeterType>().Select(mt =>
            $"{{\"type\":{Q(mt.ToString())},\"displayName\":{Q(mt.DisplayName())},\"total\":{s.GetTotal(mt)}}}"));
        return $$"""
        {
          "id": {{Q(s.Id)}},
          "zoneName": {{Q(s.ZoneName)}},
          "durationSeconds": {{s.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}},
          "formattedDuration": {{Q(s.FormattedDuration)}},
          "isActive": {{B(s.IsActive)}},
          "combatantCount": {{s.Combatants.Count}},
          "totals": [{{totals}}]
        }
        """;
    }

    private static string BuildSessionCombatants(CombatSession s)
    {
        var dur        = s.DurationSeconds;
        var combatants = string.Join(",", s.Combatants.Values
            .OrderByDescending(c => c.TotalDamageDealt)
            .Select(c => CombatantSummaryJson(c, dur)));
        return $"{{\"sessionId\":{Q(s.Id)},\"count\":{s.Combatants.Count},\"combatants\":[{combatants}]}}";
    }

    private static string BuildCombatantDetail(CombatSession s, uint entityId)
    {
        if (!s.Combatants.TryGetValue(entityId, out var c))
            return "{\"error\":\"combatant not found\"}";

        var dur             = s.DurationSeconds;
        var damageAbilities = string.Join(",", c.DamageByAbility.Values
            .OrderByDescending(a => a.TotalAmount).Select(AbilityStatsJson));
        var healAbilities   = string.Join(",", c.HealingByAbility.Values
            .OrderByDescending(a => a.TotalAmount).Select(AbilityStatsJson));
        var takenAbilities  = string.Join(",", c.DamageTakenByAbility.Values
            .OrderByDescending(a => a.TotalAmount).Select(AbilityStatsJson));

        return $$"""
        {
          "sessionId": {{Q(s.Id)}},
          "entityId": {{c.EntityId}},
          "name": {{Q(c.Name)}},
          "world": {{Q(c.World)}},
          "classJobId": {{c.ClassJobId}},
          "type": {{Q(c.Type.ToString())}},
          "durationSeconds": {{dur.ToString("F1", CultureInfo.InvariantCulture)}},
          "totalDamageDealt": {{c.TotalDamageDealt}},
          "totalHealingDone": {{c.TotalHealingDone}},
          "totalOverhealingDone": {{c.TotalOverhealingDone}},
          "totalDamageTaken": {{c.TotalDamageTaken}},
          "totalAvoidableDamageTaken": {{c.TotalAvoidableDamageTaken}},
          "dps": {{c.GetDps(dur).ToString("F2", CultureInfo.InvariantCulture)}},
          "hps": {{c.GetHps(dur).ToString("F2", CultureInfo.InvariantCulture)}},
          "damageByAbility": [{{damageAbilities}}],
          "healingByAbility": [{{healAbilities}}],
          "damageTakenByAbility": [{{takenAbilities}}]
        }
        """;
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    private static string CombatantSummaryJson(CombatantData c, double dur)
    {
        return $$"""
        {
          "entityId": {{c.EntityId}},
          "name": {{Q(c.Name)}},
          "world": {{Q(c.World)}},
          "classJobId": {{c.ClassJobId}},
          "type": {{Q(c.Type.ToString())}},
          "totalDamageDealt": {{c.TotalDamageDealt}},
          "totalHealingDone": {{c.TotalHealingDone}},
          "totalOverhealingDone": {{c.TotalOverhealingDone}},
          "totalDamageTaken": {{c.TotalDamageTaken}},
          "totalAvoidableDamageTaken": {{c.TotalAvoidableDamageTaken}},
          "dps": {{c.GetDps(dur).ToString("F2", CultureInfo.InvariantCulture)}},
          "hps": {{c.GetHps(dur).ToString("F2", CultureInfo.InvariantCulture)}}
        }
        """;
    }

    private static string AbilityStatsJson(AbilityStats a)
    {
        return $$"""
        {
          "actionId": {{a.ActionId}},
          "name": {{Q(a.Name)}},
          "hits": {{a.Hits}},
          "totalAmount": {{a.TotalAmount}},
          "totalOverheal": {{a.TotalOverheal}},
          "average": {{a.Average.ToString("F1", CultureInfo.InvariantCulture)}},
          "minHit": {{a.MinHit}},
          "maxHit": {{a.MaxHit}},
          "overhealPercent": {{a.OverhealPercent.ToString("F1", CultureInfo.InvariantCulture)}}
        }
        """;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CombatSession? FindSession(string id)
    {
        var store = Tracker.Store;
        return store.TempSessions.FirstOrDefault(s => s.Id == id)
            ?? store.SavedSessions.FirstOrDefault(s => s.Id == id);
    }

    private void Save() => _plugin.SaveConfig();

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&'))
        {
            var idx = part.IndexOf('=');
            if (idx < 0) result[part] = "";
            else result[part[..idx]] = Uri.UnescapeDataString(part[(idx + 1)..]);
        }
        return result;
    }

    private static bool TryFloatQ(Dictionary<string, string> q, string key, out float val)
    {
        if (q.TryGetValue(key, out var s) &&
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            return true;
        val = 0f;
        return false;
    }

    private static bool TryBoolQ(Dictionary<string, string> q, string key, out bool val)
    {
        if (q.TryGetValue(key, out var s))
        {
            if (s == "1" || s.Equals("true",  StringComparison.OrdinalIgnoreCase)) { val = true;  return true; }
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) { val = false; return true; }
        }
        val = false;
        return false;
    }

    private static string Q(string? s)
        => s == null ? "null"
                     : $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")}\"";

    private static string B(bool v) => v ? "true" : "false";
    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static void Respond(HttpListenerContext ctx, int statusCode, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode       = statusCode;
        ctx.Response.ContentType      = "application/json";
        ctx.Response.ContentLength64  = bytes.Length;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void RespondPng(HttpListenerContext ctx, byte[] png)
    {
        ctx.Response.StatusCode       = 200;
        ctx.Response.ContentType      = "image/png";
        ctx.Response.ContentLength64  = png.Length;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.OutputStream.Write(png, 0, png.Length);
        ctx.Response.OutputStream.Close();
    }
}
