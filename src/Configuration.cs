using System.Collections.Generic;
using Dalamud.Configuration;

namespace DamageMeter;

public enum WindowStyle
{
    Classic,  // Dark background, bold title bar
    Minimal,  // Borderless, semi-transparent
    Modern    // Rounded corners, subtle gradient background
}

public enum ViewMode
{
    Chart,  // Existing bar meter
    Graph,  // Line graph of selected metric over time, per combatant
}

[System.Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── Meter ────────────────────────────────────────────────────────────────
    public MeterType CurrentMeter { get; set; } = MeterType.DamageDealt;
    public ViewMode  CurrentView  { get; set; } = ViewMode.Chart;

    // ── Display ──────────────────────────────────────────────────────────────
    /// Show the exact numeric value next to the bar.
    public bool ShowFullValues  { get; set; } = true;
    /// Show each player's share of the group total (e.g. "43%").
    public bool ShowPercentage  { get; set; } = true;
    /// Show "@Server" suffix on player names.
    public bool ShowPlayerServer { get; set; } = true;
    /// Show player's full name. If false, uses initials only.
    public bool ShowFullName    { get; set; } = true;
    /// Show job icon to the left of each row.
    public bool ShowJobIcon     { get; set; } = true;
    /// Lock the window in place (no dragging/resizing).
    public bool LockWindow      { get; set; } = false;

    // ── Filters ───────────────────────────────────────────────────────────────
    /// Include enemy combatants in the meter.
    public bool ShowEnemyGroup     { get; set; } = true;
    /// Include friendly (non-party) players in the meter.
    public bool ShowFriendlyGroup  { get; set; } = true;
    /// Show "Total X:" stat line in the encounter header.
    public bool ShowEncounterTotal { get; set; } = true;
    /// Show group accordion headers (Party / Friendly / Enemies).
    public bool ShowGroupHeaders   { get; set; } = true;
    /// Show the "DAMAGE METER" title bar strip at the top.
    public bool ShowTitleBar       { get; set; } = true;

    // ── Colors (ImGui ABGR uint — 0xAABBGGRR) ────────────────────────────────
    // Default: red for damage, green for healing, blue for damage taken,
    //          teal for overhealing, orange for avoidable damage.
    public Dictionary<MeterType, uint> BarColors { get; set; } = new()
    {
        [MeterType.DamageDealt]          = 0xCC2828C8,  // red
        [MeterType.DPS]                  = 0xCC2828C8,  // red
        [MeterType.HealingDone]          = 0xCC28C828,  // green
        [MeterType.HPS]                  = 0xCC28C828,  // green
        [MeterType.Overhealing]          = 0xCC28C828,  // green
        [MeterType.DamageTaken]          = 0xCCC82828,  // blue
        [MeterType.AvoidableDamageTaken] = 0xCCC82828,  // blue
    };

    // ── Window ────────────────────────────────────────────────────────────────
    public WindowStyle Style   { get; set; } = WindowStyle.Modern;
    public float       Opacity { get; set; } = 0.92f;
    public float       RowHeight { get; set; } = 22f;

    // ── History ────────────────────────────────────────────────────────────────
    /// Maximum number of temporary (auto) sessions to keep before pruning oldest.
    public int MaxTempHistory { get; set; } = 20;

    // ── Internal ──────────────────────────────────────────────────────────────
    public void MigrateIfNeeded()
    {
        // v1 → future: place migrations here
    }

    // Helpers -----------------------------------------------------------------
    public uint GetBarColor(MeterType type)
        => BarColors.TryGetValue(type, out var c) ? c : 0xCC888888;
}
