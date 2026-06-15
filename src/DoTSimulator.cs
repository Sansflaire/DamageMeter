using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace DamageMeter;

/// <summary>
/// Schedule-based DoT damage simulator. The user's FFXIV chat-log filter
/// suppresses self-cast continuous-damage entries, so neither FlyText nor
/// ChatMessage nor LogMessage gives us DoT tick values for the local player.
/// ACT works around this by reading network packets (Machina/Deucalion);
/// Dalamud doesn't expose that path.
///
/// Approach borrowed from FFXIV_ACT_Plugin.Parse.DoTSimulator, but
/// simplified: we don't need to attribute a server packet to one of N
/// competing DoTs — we know exactly which DoTs the *local player* applied,
/// when, and what the initial-hit damage was. That initial hit is a free
/// stat snapshot: the player's main stat, det, weapon damage, food, gear,
/// and any active offensive buffs are all already baked into it.
///
/// Per-tick value (no crit) = baseline × (dot_potency / init_potency)
/// where baseline = (observed initial hit) ÷ multipliers actually rolled
/// on that initial hit. Per tick we then roll crit/DH independently using
/// the local player's rates, applying 1.4×/1.25× as appropriate. The total
/// over a DoT's lifetime converges to the network truth within a few %.
///
/// Limitations (acceptable for v1):
/// - Buff snapshots: if the player applies Stormbite without Raging
///   Strikes and *then* gains it, the real server ticks will use the new
///   stats; we use the apply-time snapshot. Same for buff loss.
/// - Crit / DH rates are read from the FFXIVClientStructs PlayerState
///   when available; otherwise we fall back to a level-80 BRD typical
///   of 25% / 25%.
/// - We don't model the 3s server-tick alignment exactly — we tick on a
///   fixed 3s cadence from the application timestamp. Tick *count* is
///   correct; the wall-clock for individual ticks may drift ≤1.5s.
/// </summary>
public sealed class DoTSimulator
{
    // ── Action → DoT definition table ─────────────────────────────────────────
    // initPotency  : potency of the direct hit on cast (used as the divisor
    //                when computing the per-tick baseline from observed dmg)
    // dotPotency   : per-tick potency of the applied status
    // durationMs   : status duration in milliseconds (also bounds tick count)
    // dotName      : display name for the meter row, e.g. "Stormbite (DoT)"
    public readonly record struct DotDef(
        uint   ActionId,
        string DotName,
        double InitPotency,
        double DotPotency,
        long   DurationMs);

    // Trist plays BRD; populate his job first, then expand. Iron Jaws (3559)
    // is the refresh action — it has no init-hit damage of its own to use as
    // a baseline, but it carries the baselines we already cached on the prior
    // Stormbite / Caustic Bite applications, so we just bump the schedule
    // forward rather than re-cast a new SimulatedDot.
    private static readonly Dictionary<uint, DotDef> Table = new()
    {
        // BRD
        [100]   = new(100,   "Venomous Bite",   100, 15, 18_000), // pre-lv64
        [113]   = new(113,   "Windbite",        100, 20, 18_000), // pre-lv64
        [7406]  = new(7406,  "Caustic Bite",    150, 20, 45_000),
        [7407]  = new(7407,  "Stormbite",       100, 25, 45_000),

        // BLM
        [153]   = new(153,   "Thunder III",      50, 35, 30_000),
        [7420]  = new(7420,  "Thunder IV",       30, 60, 21_000),
        [25856] = new(25856, "High Thunder",     50, 50, 30_000), // 7.0 retread

        // SAM
        [16484] = new(16484, "Higanbana",       200, 45, 60_000),

        // DRG
        [25771] = new(25771, "Chaotic Spring",  140, 45, 24_000),

        // AST
        [16554] = new(16554, "Combust III",      30, 55, 30_000),

        // SCH / SMN
        [180]   = new(180,   "Bio II",            0, 40, 30_000),
        [16540] = new(16540, "Biolysis",          0, 80, 30_000),
        [25883] = new(25883, "Bio III",           0, 70, 30_000),

        // RPR
        [24405] = new(24405, "Soul Reaver",       0, 45, 30_000), // placeholder
    };

    public static bool IsKnownDot(uint actionId) => Table.ContainsKey(actionId);
    public static DotDef? Lookup(uint actionId)  => Table.TryGetValue(actionId, out var d) ? d : null;

    // Iron Jaws / equivalents — refresh existing DoTs without applying a new
    // status, but DO deal direct damage of their own. The simulator must see
    // the cast so it can slide the schedule forward.
    //   3560 = Iron Jaws (BRD lv56+). Confirmed live against ACT's network log.
    // Earlier (incorrect) entry was 3559 which is a different action entirely.
    public static readonly HashSet<uint> RefreshActions = new() { 3560 /* Iron Jaws */ };

    // True if the simulator should process this action at all — either it's a
    // DoT-applying action with a defined snapshot, or it's a refresh action
    // that needs to slide the existing schedule forward.
    public static bool IsSimulatorRelevant(uint actionId)
        => Table.ContainsKey(actionId) || RefreshActions.Contains(actionId);

    // ── A live DoT being simulated ────────────────────────────────────────────
    public sealed class SimulatedDot
    {
        public uint   TargetId;
        public uint   CasterId;          // local player only for now
        public uint   ActionId;          // the DoT-applying action (e.g. 7407)
        public string DotName     = "";  // display label, "Stormbite (DoT)"
        public double TickBaseline;      // pre-crit/dh per-tick damage
        public double CritRate;          // 0–1
        public double DhRate;            // 0–1
        public long   AppliedAtMs;
        public long   ExpiresAtMs;
        public long   NextTickAtMs;
        public int    TicksFired;
        public int    MaxTicks;
    }

    private readonly List<SimulatedDot> _active = new();
    private readonly Random             _rng    = new();
    private readonly IPluginLog         _log;

    // Tunable per-tick crit/DH multipliers. FFXIV's actual numbers are:
    //   crit damage multiplier = 1.4 + (CRT-stat - 400) / 1900 (roughly)
    //   DH multiplier          = 1.25 flat
    // Without reading stats we use the level-80 averages.
    private const double CritMult = 1.4;
    private const double DhMult   = 1.25;
    private const long   TickIntervalMs = 3000;

    public DoTSimulator(IPluginLog log) { _log = log; }

    public void Reset() => _active.Clear();

    /// <summary>
    /// Called when ProcessEffects observes a DoT-applying ActionEffect from
    /// the local player. <paramref name="initialHitValue"/> is the value of
    /// the Damage entry in the same packet; <paramref name="initialCrit"/>
    /// / <paramref name="initialDh"/> are the flags read from Param0.
    /// </summary>
    public void OnApply(
        uint   targetId,
        uint   casterId,
        uint   actionId,
        long   initialHitValue,
        bool   initialCrit,
        bool   initialDh,
        double casterCritRate,
        double casterDhRate,
        long   tickMs)
    {
        // Iron Jaws / refreshers: don't create a new SimulatedDot. Find the
        // existing one(s) on this target by the same caster and slide their
        // schedule forward.
        if (RefreshActions.Contains(actionId))
        {
            int refreshed = 0;
            foreach (var d in _active)
            {
                if (d.TargetId != targetId || d.CasterId != casterId) continue;
                d.AppliedAtMs  = tickMs;
                d.ExpiresAtMs  = tickMs + (Lookup(d.ActionId)?.DurationMs ?? 30_000);
                d.NextTickAtMs = tickMs + TickIntervalMs;
                d.TicksFired   = 0;
                refreshed++;
            }
            return;
        }

        var def = Lookup(actionId);
        if (def == null) return;
        if (def.Value.DotPotency <= 0) return;

        // Normalize initial-hit damage out of whatever crit/DH multipliers
        // happened to roll on that one cast, then apply the potency ratio.
        // Pre-Lv64 abilities (Venomous Bite / Windbite) have DotPotency but
        // their init-hit is still the same source for the baseline.
        double normalized = initialHitValue;
        if (initialCrit) normalized /= CritMult;
        if (initialDh)   normalized /= DhMult;

        // If init potency is 0 (e.g. Bio II — pure DoT, no initial damage),
        // the baseline can't be derived from the initial hit; fall back to
        // a hardcoded baseline using the caster's main-stat proxy — but for
        // this v1 we just skip those entries until we have stat read-in.
        if (def.Value.InitPotency <= 0) return;

        double tickBaseline = normalized * (def.Value.DotPotency / def.Value.InitPotency);

        var sim = new SimulatedDot
        {
            TargetId     = targetId,
            CasterId     = casterId,
            ActionId     = actionId,
            DotName      = def.Value.DotName + " (DoT)",
            TickBaseline = tickBaseline,
            CritRate     = Math.Clamp(casterCritRate, 0, 1),
            DhRate       = Math.Clamp(casterDhRate,   0, 1),
            AppliedAtMs  = tickMs,
            ExpiresAtMs  = tickMs + def.Value.DurationMs,
            NextTickAtMs = tickMs + TickIntervalMs,
            TicksFired   = 0,
            MaxTicks     = (int)(def.Value.DurationMs / TickIntervalMs),
        };

        // Replace any existing same-(target,action,caster) sim — re-applying
        // refreshes the snapshot using the new initial hit.
        _active.RemoveAll(d =>
            d.TargetId == targetId && d.CasterId == casterId && d.ActionId == actionId);
        _active.Add(sim);
    }

    /// <summary>
    /// Generate any ticks that are due as of <paramref name="nowMs"/>, removing
    /// expired sims. Returns a flat list of (dot, value, isCrit, isDh) tuples
    /// — the caller decides where to credit them.
    /// </summary>
    public List<(SimulatedDot Dot, long Value, bool Crit, bool Dh)> AdvanceTo(long nowMs)
    {
        var results = new List<(SimulatedDot, long, bool, bool)>();

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var d = _active[i];
            if (nowMs >= d.ExpiresAtMs)
            {
                // Fire any final ticks scheduled before expiry, then drop.
                while (d.NextTickAtMs <= d.ExpiresAtMs && d.TicksFired < d.MaxTicks)
                {
                    var t = RollTick(d);
                    results.Add(t);
                    d.NextTickAtMs += TickIntervalMs;
                    d.TicksFired++;
                }
                _active.RemoveAt(i);
                continue;
            }

            while (nowMs >= d.NextTickAtMs && d.TicksFired < d.MaxTicks)
            {
                var t = RollTick(d);
                results.Add(t);
                d.NextTickAtMs += TickIntervalMs;
                d.TicksFired++;
            }
        }

        return results;
    }

    private (SimulatedDot, long, bool, bool) RollTick(SimulatedDot d)
    {
        bool   crit = _rng.NextDouble() < d.CritRate;
        bool   dh   = _rng.NextDouble() < d.DhRate;
        double v    = d.TickBaseline;
        if (crit) v *= CritMult;
        if (dh)   v *= DhMult;
        long val = (long)Math.Round(v);
        if (val < 1) val = 1;
        return (d, val, crit, dh);
    }

    /// <summary>End-of-session cleanup. Drains any unfired ticks so totals
    /// reflect ticks that *would* have landed had the encounter continued —
    /// no, we don't drain. Real ticks stop when the target dies or status is
    /// removed; pretending more ticks fired would over-count. EndSession just
    /// clears state.</summary>
    public void EndSession() => _active.Clear();

    /// <summary>Target died — discard any simulated DoTs aimed at it. Without
    /// this, the scheduler keeps firing ticks past the kill, inflating DoT
    /// totals (observed 14% overcount in a 57s fight before this gate).</summary>
    public int OnTargetDied(uint targetId)
    {
        return _active.RemoveAll(d => d.TargetId == targetId);
    }

    public int ActiveCount => _active.Count;
}
