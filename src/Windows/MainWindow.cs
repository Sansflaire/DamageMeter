using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace DamageMeter.Windows;

/// <summary>
/// The main damage-meter window.
/// Draws a sorted list of combatants as colored bars with job icons,
/// names, and values. Alliance raid groups are collapsible.
/// </summary>
public sealed class MainWindow : IDisposable
{
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;
    private CombatTracker Tracker => _plugin.Tracker;

    // Icon cache: classJobId → ISharedImmediateTexture (call GetWrapOrDefault() to render)
    private readonly Dictionary<byte, ISharedImmediateTexture?> _iconCache = new();

    // Alliance group collapse state: key = group label
    private readonly Dictionary<string, bool> _groupCollapsed = new();

    private const float TitleBarHeight = 26f;
    private const float DropdownHeight = 22f;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow(Plugin plugin)
    {
        _plugin = plugin;
    }

    // ── Draw ──────────────────────────────────────────────────────────────────
    public void Draw()
    {
        if (!IsVisible) return;

        ApplyWindowStyle();

        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (Config.LockWindow)
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        ImGui.SetNextWindowSizeConstraints(new Vector2(280, 100), new Vector2(800, 2000));
        ImGui.SetNextWindowSize(new Vector2(380, 260), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Config.Opacity);

        if (!ImGui.Begin("DAMAGE METER###DamageMeterMain", ref _isVisible, flags))
        {
            ImGui.End();
            return;
        }

        DrawToolbar();
        ImGui.Separator();
        DrawMeterBars();

        ImGui.End();
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────
    private void DrawToolbar()
    {
        // Meter type dropdown
        var label = Config.CurrentMeter.DisplayName();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
        if (ImGui.BeginCombo("##MeterType", label))
        {
            foreach (MeterType mt in Enum.GetValues<MeterType>())
            {
                var selected = mt == Config.CurrentMeter;
                if (ImGui.Selectable(mt.DisplayName(), selected))
                {
                    Config.CurrentMeter = mt;
                    _plugin.SaveConfig();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();

        // History button
        if (ImGui.SmallButton("History"))
            _plugin._historyWindow.IsVisible = !_plugin._historyWindow.IsVisible;

        ImGui.SameLine();

        // Settings button
        if (ImGui.SmallButton("##SettingsBtn"))
            _plugin._settingsWindow.IsVisible = !_plugin._settingsWindow.IsVisible;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings");

        // Active indicator
        var tracker = Tracker;
        if (tracker.ActiveSession != null)
        {
            ImGui.SameLine();
            var dur = tracker.ActiveSession.FormattedDuration;
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), $"● {dur}");
        }
    }

    // ── Meter bars ────────────────────────────────────────────────────────────
    private void DrawMeterBars()
    {
        // Prefer pinned session from history, then active, then most recent
        var session = _plugin._historyWindow.PinnedSession
                   ?? Tracker.ActiveSession
                   ?? GetLastSession();
        if (session == null)
        {
            ImGui.TextDisabled("No combat data. Start a fight to begin tracking.");
            return;
        }

        var meterType = Config.CurrentMeter;
        var sorted    = session.GetSortedCombatants(meterType);
        var total     = session.GetTotal(meterType);
        var dur       = session.DurationSeconds;

        if (sorted.Count == 0)
        {
            ImGui.TextDisabled("No combatants recorded yet.");
            return;
        }

        // Determine if we should group by alliance (>8 players)
        if (sorted.Count > 8)
            DrawAllianceGrouped(sorted, meterType, total, dur, session);
        else
            DrawRows(sorted, meterType, total, dur, null);
    }

    private void DrawAllianceGrouped(
        List<CombatantData> sorted,
        MeterType meterType, long total, double dur,
        CombatSession session)
    {
        // Group: party (in IPartyList) vs alliances
        var partyIds = new System.Collections.Generic.HashSet<uint>();
        foreach (var member in Plugin.PartyList)
            partyIds.Add(member.EntityId);

        var myParty   = new List<CombatantData>();
        var othersCat = new List<CombatantData>();

        foreach (var c in sorted)
        {
            if (partyIds.Contains(c.EntityId)) myParty.Add(c);
            else othersCat.Add(c);
        }

        DrawGroupSection("My Party", myParty, meterType, total, dur);
        if (othersCat.Count > 0)
            DrawGroupSection("Alliance", othersCat, meterType, total, dur);
    }

    private void DrawGroupSection(
        string label,
        List<CombatantData> combatants,
        MeterType meterType, long total, double dur)
    {
        _groupCollapsed.TryGetValue(label, out var collapsed);

        // Arrow toggle
        var arrow = collapsed ? "▶" : "▼";
        if (ImGui.SmallButton($"{arrow}##grp_{label}"))
        {
            _groupCollapsed[label] = !collapsed;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted($"{label} ({combatants.Count})");

        if (!collapsed)
            DrawRows(combatants, meterType, total, dur, label);
    }

    private void DrawRows(
        List<CombatantData> combatants,
        MeterType meterType, long total, double dur,
        string? indent)
    {
        var rowH     = Config.RowHeight;
        var avail    = ImGui.GetContentRegionAvail().X;
        if (indent != null) avail -= 12f;

        var barColor = Config.GetBarColor(meterType);
        var dl       = ImGui.GetWindowDrawList();
        var showSvr  = Config.ShowPlayerServer;
        var initials = !Config.ShowFullName;
        var showPct  = Config.ShowPercentage;
        var showVal  = Config.ShowFullValues;

        for (int i = 0; i < combatants.Count; i++)
        {
            var c   = combatants[i];
            var val = c.GetValue(meterType, dur);
            var pct = total > 0 ? (float)val / total : 0f;

            if (indent != null)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12f);

            var rowMin = ImGui.GetCursorScreenPos();
            var rowMax = new Vector2(rowMin.X + avail, rowMin.Y + rowH);

            // Reserve row space
            ImGui.Dummy(new Vector2(avail, rowH));
            if (ImGui.IsItemHovered())
            {
                // Tooltip with full stats on hover
                ImGui.BeginTooltip();
                ImGui.Text($"{c.Name} @ {c.World}");
                ImGui.Text($"Damage: {FormatNumber(c.TotalDamageDealt)}");
                ImGui.Text($"Healing: {FormatNumber(c.TotalHealingDone)}");
                ImGui.Text($"Overhealing: {FormatNumber(c.TotalOverhealingDone)}");
                ImGui.Text($"Damage Taken: {FormatNumber(c.TotalDamageTaken)}");
                ImGui.EndTooltip();
            }

            // Dark row background (alternating)
            uint rowBg = (i % 2 == 0) ? 0x20FFFFFFu : 0x10FFFFFFu;
            dl.AddRectFilled(rowMin, rowMax, rowBg);

            // Colored bar fill
            var barEnd = new Vector2(rowMin.X + avail * pct, rowMax.Y - 1);
            dl.AddRectFilled(rowMin, barEnd, barColor);

            // Job icon
            var iconX  = rowMin.X + 2f;
            var iconSz = rowH - 4f;
            if (Config.ShowJobIcon)
            {
                var jobIcon = GetJobIcon(c.ClassJobId)?.GetWrapOrDefault();
                if (jobIcon != null)
                {
                    dl.AddImage(
                        jobIcon.Handle,
                        new Vector2(iconX, rowMin.Y + 2f),
                        new Vector2(iconX + iconSz, rowMin.Y + 2f + iconSz));
                }
            }

            // Name text
            var textOffset  = Config.ShowJobIcon ? iconSz + 6f : 4f;
            var nameStr     = c.DisplayName(showSvr, initials);
            var textY       = rowMin.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
            dl.AddText(new Vector2(rowMin.X + textOffset, textY), 0xFFFFFFFF, nameStr);

            // Right-side: value + percentage
            var rightParts = new System.Text.StringBuilder();
            if (showVal)  rightParts.Append(FormatValue(val, meterType));
            if (showPct)  rightParts.Append($" ({pct * 100:F0}%)");
            var rightStr  = rightParts.ToString().Trim();
            var rightSize = ImGui.CalcTextSize(rightStr);
            dl.AddText(
                new Vector2(rowMax.X - rightSize.X - 4f, textY),
                0xFFFFFFFF,
                rightStr);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private CombatSession? GetLastSession()
    {
        var store = Tracker.Store;
        if (store.TempSessions.Count > 0)
            return store.TempSessions[^1];
        return null;
    }

    private ISharedImmediateTexture? GetJobIcon(byte classJobId)
    {
        if (_iconCache.TryGetValue(classJobId, out var cached)) return cached;
        try
        {
            // Job icons: base offset 62100 + classJobId
            // e.g. PLD (job 19) → icon 62119
            var iconId  = (uint)(62100 + classJobId);
            var shared  = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
            _iconCache[classJobId] = shared;
            return shared;
        }
        catch
        {
            _iconCache[classJobId] = null;
            return null;
        }
    }

    private static string FormatValue(long value, MeterType type)
    {
        return type switch
        {
            MeterType.DPS or MeterType.HPS => FormatNumber(value) + "/s",
            _                              => FormatNumber(value)
        };
    }

    private static string FormatNumber(long n)
    {
        return n switch
        {
            >= 1_000_000 => $"{n / 1_000_000.0:F2}M",
            >= 1_000     => $"{n / 1_000.0:F1}K",
            _            => n.ToString()
        };
    }

    private void ApplyWindowStyle()
    {
        switch (Config.Style)
        {
            case WindowStyle.Classic:
                // Default ImGui dark style — no overrides needed
                break;
            case WindowStyle.Minimal:
                ImGui.SetNextWindowBgAlpha(Config.Opacity * 0.7f);
                break;
            case WindowStyle.Modern:
                // Slightly rounded feel; bg is set via alpha
                break;
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        // ISharedImmediateTexture is managed by Dalamud — do not dispose individually
        _iconCache.Clear();
    }
}
