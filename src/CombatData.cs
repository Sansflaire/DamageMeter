using System;
using System.Collections.Generic;
using System.Linq;

namespace DamageMeter;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum MeterType
{
    DamageDealt,
    DPS,
    HealingDone,
    HPS,
    Overhealing,
    DamageTaken,
    AvoidableDamageTaken
}

public static class MeterTypeExtensions
{
    public static string DisplayName(this MeterType m) => m switch
    {
        MeterType.DamageDealt          => "Damage Dealt",
        MeterType.DPS                  => "DPS",
        MeterType.HealingDone          => "Healing Done",
        MeterType.HPS                  => "HPS",
        MeterType.Overhealing          => "Overhealing",
        MeterType.DamageTaken          => "Damage Taken",
        MeterType.AvoidableDamageTaken => "Avoidable Dmg Taken",
        _                              => m.ToString()
    };
}

public enum CombatantType
{
    PartyMember,    // In the player's current party (IPartyList)
    FriendlyPlayer, // Player character not in party (alliance, bystander)
    Enemy,          // NPC / monster
    Unknown
}

// ── Per-ability stats ─────────────────────────────────────────────────────────

/// <summary>Tracks damage or healing done by a single ability.</summary>
[Serializable]
public class AbilityStats
{
    public uint   ActionId    { get; set; }
    public string Name        { get; set; } = "";
    public long   TotalAmount { get; set; }
    public long   TotalOverheal { get; set; } // meaningful for healing entries only
    public int    Hits        { get; set; }
    public long   MinHit      { get; set; }   // set on first hit
    public long   MaxHit      { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public double Average => Hits > 0 ? (double)TotalAmount / Hits : 0;

    [Newtonsoft.Json.JsonIgnore]
    public double OverhealPercent =>
        (TotalAmount + TotalOverheal) > 0
            ? (double)TotalOverheal / (TotalAmount + TotalOverheal) * 100
            : 0;

    public void Record(long amount, long overheal = 0, bool skipMin = false)
    {
        TotalAmount   += amount;
        TotalOverheal += overheal;
        Hits++;
        // skipMin = true for killing blows where the recorded value is capped at target's
        // remaining HP rather than the true hit power; MinHit stays 0 until a surviving hit.
        if (!skipMin)
            MinHit = MinHit == 0 ? amount : Math.Min(MinHit, amount);
        MaxHit = Math.Max(MaxHit, amount);
    }
}

// ── Per-combatant data ────────────────────────────────────────────────────────

/// <summary>All combat data for one entity over one fight.</summary>
[Serializable]
public class CombatantData
{
    public uint          EntityId   { get; set; }
    public string        Name       { get; set; } = "";
    public string        World      { get; set; } = "";
    public byte          ClassJobId { get; set; }
    public CombatantType Type       { get; set; } = CombatantType.Unknown;

    // ── Totals ────────────────────────────────────────────────────────────────
    public long TotalDamageDealt          { get; set; }
    public long TotalHealingDone          { get; set; }
    public long TotalOverhealingDone      { get; set; }
    public long TotalDamageTaken          { get; set; }
    public long TotalAvoidableDamageTaken { get; set; }

    // ── Per-ability breakdown ─────────────────────────────────────────────────
    /// Abilities this entity used to deal damage (keyed by ActionId).
    public Dictionary<uint, AbilityStats> DamageByAbility     { get; set; } = new();
    /// Abilities this entity used to heal (keyed by ActionId).
    public Dictionary<uint, AbilityStats> HealingByAbility    { get; set; } = new();
    /// Abilities that hit this entity (keyed by ActionId of the incoming attack).
    public Dictionary<uint, AbilityStats> DamageTakenByAbility { get; set; } = new();

    // ── Event log for DPS/HPS curves ─────────────────────────────────────────
    public List<(long TickMs, long Amount)> DamageEvents  { get; set; } = new();
    public List<(long TickMs, long Amount)> HealingEvents { get; set; } = new();

    // ── Computed ──────────────────────────────────────────────────────────────
    public string DisplayName(bool showServer, bool initialsOnly)
    {
        var name = initialsOnly ? Initials(Name) : Name;
        return showServer && !string.IsNullOrEmpty(World) ? $"{name}@{World}" : name;
    }

    private static string Initials(string fullName)
    {
        var parts = fullName.Split(' ');
        if (parts.Length >= 2)
            return $"{parts[0][0]}.{parts[1][0]}.";
        return fullName.Length > 0 ? $"{fullName[0]}." : fullName;
    }

    public double GetDps(double durationSeconds)
        => durationSeconds > 0 ? TotalDamageDealt / durationSeconds : 0;

    public double GetHps(double durationSeconds)
        => durationSeconds > 0 ? TotalHealingDone / durationSeconds : 0;

    public long GetValue(MeterType type, double durationSeconds) => type switch
    {
        MeterType.DamageDealt          => TotalDamageDealt,
        MeterType.DPS                  => (long)GetDps(durationSeconds),
        MeterType.HealingDone          => TotalHealingDone,
        MeterType.HPS                  => (long)GetHps(durationSeconds),
        MeterType.Overhealing          => TotalOverhealingDone,
        MeterType.DamageTaken          => TotalDamageTaken,
        MeterType.AvoidableDamageTaken => TotalAvoidableDamageTaken,
        _                              => 0
    };
}

// ── Session ───────────────────────────────────────────────────────────────────

/// <summary>One combat encounter (start → end).</summary>
[Serializable]
public class CombatSession
{
    public string    Id        { get; set; } = "";
    public string    ZoneName  { get; set; } = "Unknown Zone";
    public DateTime  StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime   { get; set; }
    public bool      IsSaved   { get; set; }
    public bool      IsSummary { get; set; }
    public int       PullCount { get; set; } // > 0 means this is an aggregated instance summary

    public Dictionary<uint, CombatantData> Combatants { get; set; } = new();

    [Newtonsoft.Json.JsonIgnore]
    public bool IsActive => EndTime == null;

    [Newtonsoft.Json.JsonIgnore]
    public double DurationSeconds =>
        (EndTime ?? DateTime.UtcNow).Subtract(StartTime).TotalSeconds;

    public string FormattedDuration
    {
        get
        {
            var s = (int)DurationSeconds;
            return $"{s / 60}:{s % 60:D2}";
        }
    }

    public long GetTotal(MeterType type)
        => Combatants.Values.Sum(c => c.GetValue(type, DurationSeconds));

    /// <summary>Returns combatants of all types sorted descending by value.</summary>
    public List<CombatantData> GetSortedCombatants(MeterType type)
    {
        var dur = DurationSeconds;
        return Combatants.Values
            .OrderByDescending(c => c.GetValue(type, dur))
            .ToList();
    }

    /// <summary>Returns combatants filtered to one type, sorted descending.</summary>
    public List<CombatantData> GetSortedByType(MeterType type, CombatantType ctype)
    {
        var dur = DurationSeconds;
        return Combatants.Values
            .Where(c => c.Type == ctype)
            .OrderByDescending(c => c.GetValue(type, dur))
            .ToList();
    }

    public static string MakeId(string zoneName, DateTime dt)
        => $"{Sanitize(zoneName)}_{dt:yyyy-MM-dd_HH-mm-ss}";

    private static string Sanitize(string name)
        => string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c == '_')).Replace(" ", "_");
}

// ── Store ─────────────────────────────────────────────────────────────────────

[Serializable]
public class SessionStore
{
    public List<CombatSession> TempSessions  { get; set; } = new();
    public List<CombatSession> SavedSessions { get; set; } = new();
}
