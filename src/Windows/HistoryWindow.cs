using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace DamageMeter.Windows;

/// <summary>
/// Displays past combat sessions (temporary and saved).
/// Lets the user load a session into the main meter view,
/// manually save a temp session, or delete it.
/// </summary>
public sealed class HistoryWindow : IDisposable
{
    private bool _isVisible;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly Plugin _plugin;
    private Configuration Config  => _plugin.Config;
    private CombatTracker Tracker => _plugin.Tracker;

    /// <summary>If set, the main window will display this session instead of the active one.</summary>
    public CombatSession? PinnedSession { get; private set; }

    /// <summary>External clear — used by MainWindow's "Live" button and by the CombatTracker
    /// when a new session starts, so the user automatically returns to live data the moment
    /// they re-enter combat.</summary>
    public void ClearPin() => PinnedSession = null;

    private int _selectedIndex = -1;
    private bool _showTemp  = true;
    private bool _showSaved = true;

    public HistoryWindow(Plugin plugin)
    {
        _plugin = plugin;
    }

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(440, 400), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Config.Opacity);

        if (!ImGui.Begin("Damage Meter — History###DamageMeterHistory", ref _isVisible))
        {
            ImGui.End();
            return;
        }

        DrawHeader();
        ImGui.Separator();
        DrawSessionList();

        ImGui.End();
    }

    // ── Header ────────────────────────────────────────────────────────────────
    private void DrawHeader()
    {
        ImGui.Checkbox("Recent", ref _showTemp);
        ImGui.SameLine();
        ImGui.Checkbox("Saved", ref _showSaved);
        ImGui.SameLine();

        if (PinnedSession != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"Viewing: {PinnedSession.Id}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Unpin"))
                PinnedSession = null;
        }
    }

    // ── Session list ──────────────────────────────────────────────────────────
    private void DrawSessionList()
    {
        if (ImGui.BeginChild("##SessionList", new Vector2(0, -1), false))
        {
            int idx = 0;

            if (_showSaved && Tracker.Store.SavedSessions.Count > 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "★ Saved");
                ImGui.Separator();
                DrawSessions(Tracker.Store.SavedSessions, ref idx, true);
                ImGui.Spacing();
            }

            if (_showTemp && Tracker.Store.TempSessions.Count > 0)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    $"Recent (max {Config.MaxTempHistory})");
                ImGui.Separator();
                DrawSessions(Tracker.Store.TempSessions, ref idx, false);
            }

            if (Tracker.Store.TempSessions.Count == 0 && Tracker.Store.SavedSessions.Count == 0)
                ImGui.TextDisabled("No sessions recorded yet.");
        }
        ImGui.EndChild();
    }

    private void DrawSessions(List<CombatSession> sessions, ref int idx, bool isSaved)
    {
        // Draw newest first
        for (int i = sessions.Count - 1; i >= 0; i--)
        {
            var s        = sessions[i];
            var selected = _selectedIndex == idx;
            var label    = s.IsSummary
                ? $"Σ {s.ZoneName}  [{s.FormattedDuration}]  {s.PullCount} pulls  {s.StartTime.ToLocalTime():HH:mm}##sess_{idx}"
                : $"{s.ZoneName}  [{s.FormattedDuration}]  {s.StartTime.ToLocalTime():HH:mm}##sess_{idx}";

            if (s.IsSummary)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));

            if (ImGui.Selectable(label, selected))
                _selectedIndex = idx;

            if (s.IsSummary)
                ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text($"Zone: {s.ZoneName}");
                ImGui.Text($"Started: {s.StartTime.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                ImGui.Text($"Duration: {s.FormattedDuration}");
                ImGui.Text($"Players tracked: {s.Combatants.Count}");
                ImGui.EndTooltip();
            }

            if (selected)
            {
                ImGui.Indent(8f);

                if (ImGui.SmallButton($"View##v_{idx}"))
                {
                    PinnedSession = s;
                    _plugin._mainWindow.IsVisible = true;
                }

                if (!isSaved)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Save##save_{idx}"))
                    {
                        Tracker.SaveSession(s);
                        _selectedIndex = -1;
                    }
                }

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.6f, 0.1f, 0.1f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                bool deleted = ImGui.SmallButton($"Delete##del_{idx}");
                ImGui.PopStyleColor(2);

                ImGui.Unindent(8f);

                if (deleted)
                {
                    if (PinnedSession == s) PinnedSession = null;
                    Tracker.DeleteSession(s);
                    _selectedIndex = -1;
                    break; // list modified — exit loop
                }
            }

            idx++;
        }
    }

    public void Dispose() { }
}
