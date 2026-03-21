using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace DamageMeter.Windows;

/// <summary>
/// Settings popup window. Organized into sections:
/// Display, Bar Colors, Window Style, History.
/// </summary>
public sealed class SettingsWindow : IDisposable
{
    private bool _isVisible;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;

    public SettingsWindow(Plugin plugin)
    {
        _plugin = plugin;
    }

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(440, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.95f);

        if (!ImGui.Begin("Damage Meter — Settings###DamageMeterSettings", ref _isVisible))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##SettingsTabs"))
        {
            if (ImGui.BeginTabItem("Display"))    { DrawDisplayTab();    ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Filters"))    { DrawFiltersTab();    ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Bar Colors")) { DrawColorsTab();     ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Window"))     { DrawWindowTab();     ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("History"))    { DrawHistoryTab();    ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // ── Display tab ───────────────────────────────────────────────────────────
    private void DrawDisplayTab()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Name Display");
        ImGui.Separator();

        var showFull = Config.ShowFullName;
        if (ImGui.Checkbox("Full player names", ref showFull))
        {
            Config.ShowFullName = showFull;
            Save();
        }

        using (new DisabledScope(Config.ShowFullName))
        {
            ImGui.SameLine(200);
            ImGui.TextDisabled("(disabled when full names on)");
        }

        var initOnly = !Config.ShowFullName;
        if (ImGui.Checkbox("Initials only  (e.g. T.M.)", ref initOnly))
        {
            Config.ShowFullName = !initOnly;
            Save();
        }

        var showSvr = Config.ShowPlayerServer;
        if (ImGui.Checkbox("Show @Server suffix", ref showSvr))
        {
            Config.ShowPlayerServer = showSvr;
            Save();
        }

        var showIcon = Config.ShowJobIcon;
        if (ImGui.Checkbox("Show job icon", ref showIcon))
        {
            Config.ShowJobIcon = showIcon;
            Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Values");
        ImGui.Separator();

        var showVal = Config.ShowFullValues;
        if (ImGui.Checkbox("Show full numeric value", ref showVal))
        {
            Config.ShowFullValues = showVal;
            Save();
        }

        var showPct = Config.ShowPercentage;
        if (ImGui.Checkbox("Show % of group total", ref showPct))
        {
            Config.ShowPercentage = showPct;
            Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Row");
        ImGui.Separator();

        var rowH = Config.RowHeight;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Row height (px)", ref rowH, 16f, 40f))
        {
            Config.RowHeight = rowH;
            Save();
        }
    }

    // ── Filters tab ───────────────────────────────────────────────────────────
    private void DrawFiltersTab()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Groups");
        ImGui.Separator();

        var showEnemy = Config.ShowEnemyGroup;
        if (ImGui.Checkbox("Show enemy group", ref showEnemy))
        {
            Config.ShowEnemyGroup = showEnemy;
            Save();
        }

        var showFriendly = Config.ShowFriendlyGroup;
        if (ImGui.Checkbox("Show friendly (non-party) group", ref showFriendly))
        {
            Config.ShowFriendlyGroup = showFriendly;
            Save();
        }

        var showGroupHeaders = Config.ShowGroupHeaders;
        if (ImGui.Checkbox("Show group accordion headers", ref showGroupHeaders))
        {
            Config.ShowGroupHeaders = showGroupHeaders;
            Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Header");
        ImGui.Separator();

        var showTitleBar = Config.ShowTitleBar;
        if (ImGui.Checkbox("Show title bar (\"DAMAGE METER\" strip)", ref showTitleBar))
        {
            Config.ShowTitleBar = showTitleBar;
            Save();
        }

        var showTotal = Config.ShowEncounterTotal;
        if (ImGui.Checkbox("Show total stat line in encounter header", ref showTotal))
        {
            Config.ShowEncounterTotal = showTotal;
            Save();
        }
    }

    // ── Colors tab ────────────────────────────────────────────────────────────
    private void DrawColorsTab()
    {
        ImGui.TextWrapped("Click a color swatch to edit. Colors apply to bar fill for each meter type.");
        ImGui.Spacing();

        foreach (MeterType mt in Enum.GetValues<MeterType>())
        {
            var current = Config.GetBarColor(mt);
            var vec = ImGui.ColorConvertU32ToFloat4(current);
            if (ImGui.ColorEdit4(mt.DisplayName() + "##col_" + mt, ref vec,
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview))
            {
                Config.BarColors[mt] = ImGui.ColorConvertFloat4ToU32(vec);
                Save();
            }
        }
    }

    // ── Window tab ────────────────────────────────────────────────────────────
    private void DrawWindowTab()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Style");
        ImGui.Separator();

        foreach (WindowStyle style in Enum.GetValues<WindowStyle>())
        {
            var selected = Config.Style == style;
            if (ImGui.RadioButton(style.ToString(), selected))
            {
                Config.Style = style;
                Save();
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Behaviour");
        ImGui.Separator();

        var locked = Config.LockWindow;
        if (ImGui.Checkbox("Lock window (no move / resize)", ref locked))
        {
            Config.LockWindow = locked;
            Save();
        }

        var opacity = Config.Opacity;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Opacity", ref opacity, 0.2f, 1f))
        {
            Config.Opacity = opacity;
            Save();
        }
    }

    // ── History tab ───────────────────────────────────────────────────────────
    private void DrawHistoryTab()
    {
        ImGui.TextWrapped(
            "Recent sessions are kept automatically up to the limit below. " +
            "When full, the oldest is deleted. Manually saved sessions are never pruned.");
        ImGui.Spacing();

        var maxH = Config.MaxTempHistory;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Max recent sessions", ref maxH, 1, 50))
        {
            Config.MaxTempHistory = maxH;
            Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
            $"Stored: {_plugin.Tracker.Store.TempSessions.Count} recent, " +
            $"{_plugin.Tracker.Store.SavedSessions.Count} saved.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.6f, 0.1f, 0.1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f));
        if (ImGui.Button("Clear ALL recent sessions"))
        {
            _plugin.Tracker.Store.TempSessions.Clear();
            _plugin.Tracker.SaveStore();
        }
        ImGui.PopStyleColor(2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void Save() => _plugin.SaveConfig();

    public void Dispose() { }

    // Simple RAII helper for disabled state
    private readonly struct DisabledScope : IDisposable
    {
        private readonly bool _wasDisabled;
        public DisabledScope(bool disable)
        {
            _wasDisabled = disable;
            if (disable) ImGui.BeginDisabled();
        }
        public void Dispose() { if (_wasDisabled) ImGui.EndDisabled(); }
    }
}
