using System;
using System.Collections.Generic;
using System.Linq;

namespace DamageMeter;

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
        MeterType.DamageDealt         => "Damage Dealt",
        MeterType.DPS                 => "DPS",
        MeterType.HealingDone         => "Healing Done",
        MeterType.HPS                 => "HPS",
        MeterType.Overhealing         => "Overhealing",
        MeterType.DamageTaken         => "Damage Taken",
        MeterType.AvoidableDamageTaken => "Avoidable Dmg Taken",
        _                             => m.ToString()
    };
}

/// <summary>
/// Stores all combat data for a single combatant over one fight.
/// </summary>
[Serializable]
public class CombatantData
{
    public uint   EntityId   { get; set; }
    public string Name       { get; set; } = "";
    public string World      { get; set; } = "";
    public byte   ClassJobId { get; set; }

    // Totals
    public long TotalDamageDealt          { get; set; }
    public long TotalHealingDone          { get; set; }
    public long TotalOverhealingDone      { get; set; }
    public long TotalDamageTaken          { get; set; }
    public long TotalAvoidableDamageTaken { get; set; } // proxy: AoE hits (3+ targets)

    // Event log for DPS/HPS calculations (timestamp = ticks since combat start)
    public List<(long TickMs, long Amount)> DamageEvents  { get; set; } = new();
    public List<(long TickMs, long Amount)> HealingEvents { get; set; } = new();

    // Display helpers
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

    /// <summary>Returns DPS over the duration of the combat (seconds).</summary>
    public double GetDps(double durationSeconds)
        => durationSeconds > 0 ? TotalDamageDealt / durationSeconds : 0;

    /// <summary>Returns HPS over the duration of the combat (seconds).</summary>
    public double GetHps(double durationSeconds)
        => durationSeconds > 0 ? TotalHealingDone / durationSeconds : 0;

    /// <summary>Returns the value for the given meter type.</summary>
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

/// <summary>
/// A single combat session (one pull/fight).
/// </summary>
[Serializable]
public class CombatSession
{
    // ID format: ZoneName_yyyy-MM-dd_HH-mm-ss
    public string   Id        { get; set; } = "";
    public string   ZoneName  { get; set; } = "Unknown Zone";
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime  { get; set; }
    public bool     IsSaved   { get; set; } // manually saved = permanent

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

    /// <summary>Sum of a meter value across all combatants (for percentage calculation).</summary>
    public long GetTotal(MeterType type)
        => Combatants.Values.Sum(c => c.GetValue(type, DurationSeconds));

    /// <summary>Returns combatants sorted descending by the selected meter value.</summary>
    public List<CombatantData> GetSortedCombatants(MeterType type)
    {
        var dur = DurationSeconds;
        return Combatants.Values
            .OrderByDescending(c => c.GetValue(type, dur))
            .ToList();
    }

    public static string MakeId(string zoneName, DateTime dt)
        => $"{SanitizeName(zoneName)}_{dt:yyyy-MM-dd_HH-mm-ss}";

    private static string SanitizeName(string name)
        => string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c == '_')).Replace(" ", "_");
}

/// <summary>Container for all persisted sessions.</summary>
[Serializable]
public class SessionStore
{
    /// <summary>Temporary sessions (oldest pruned when over MaxTemp).</summary>
    public List<CombatSession> TempSessions  { get; set; } = new();
    /// <summary>Manually saved sessions (never pruned automatically).</summary>
    public List<CombatSession> SavedSessions { get; set; } = new();
}
