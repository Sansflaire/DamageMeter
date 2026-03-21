using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace DamageMeter.Windows;

/// <summary>
/// Main meter window.
/// The bar area is rendered via MeterCanvas (SkiaSharp / Panache pipeline).
/// Right-click any row for a full ability-by-ability breakdown popup.
/// </summary>
public sealed class MainWindow : IDisposable
{
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly Plugin _plugin;
    private Configuration Config  => _plugin.Config;
    private CombatTracker Tracker => _plugin.Tracker;

    private readonly MeterCanvas _meter;
    internal MeterCanvas Meter => _meter;

    // Right-click detail state
    private uint           _detailEntityId;
    private CombatSession? _detailSession;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow(Plugin plugin)
    {
        _plugin = plugin;
        _meter  = new MeterCanvas(Plugin.TextureProvider);
    }

    // ── Draw ──────────────────────────────────────────────────────────────────
    public void Draw()
    {
        if (!_isVisible) return;

        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse;
        if (Config.LockWindow)
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        ImGui.SetNextWindowSizeConstraints(new Vector2(300, 120), new Vector2(1000, 3000));
        ImGui.SetNextWindowSize(new Vector2(420, 380), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Config.Opacity);

        // Zero padding so the canvas image is flush with the window edges
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   Vector2.Zero);
        bool open = ImGui.Begin("###DamageMeterMain", ref _isVisible, flags);
        ImGui.PopStyleVar(2);

        if (!open) { ImGui.End(); return; }

        DrawMeterCanvas();   // canvas first — fills window from top
        DrawToolbar();       // toolbar at bottom
        DrawDetailPopup();

        ImGui.End();
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────
    private void DrawToolbar()
    {
        // Draw a dark background strip behind the toolbar
        var dl       = ImGui.GetWindowDrawList();
        var stripTL  = ImGui.GetCursorScreenPos();
        var stripBR  = stripTL + new Vector2(ImGui.GetContentRegionAvail().X, 26f);
        dl.AddRectFilled(stripTL, stripBR, 0xFF0D0D1A);
        dl.AddLine(stripTL, new Vector2(stripBR.X, stripTL.Y), 0xFF282840);  // top border

        // Restore spacing for interactive toolbar widgets
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(6, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(4, 0));

        // Dark-themed combo + buttons
        ImGui.PushStyleColor(ImGuiCol.FrameBg,         0xFF1A1A2E);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,  0xFF252540);
        ImGui.PushStyleColor(ImGuiCol.Button,          0xFF1A1A2E);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,   0xFF2A2A50);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,    0xFF3A3A70);

        // Fixed button widths so layout is predictable regardless of window size
        const float BtnHistory  = 62f;
        const float BtnSettings = 68f;
        const float BtnSpacing  =  4f;
        const float RightMargin =  8f;

        float avail  = ImGui.GetContentRegionAvail().X;
        float comboW = avail - BtnHistory - BtnSettings - BtnSpacing * 2f - RightMargin;

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f);
        ImGui.SetNextItemWidth(Math.Max(40f, comboW));
        if (ImGui.BeginCombo("##MeterType", Config.CurrentMeter.DisplayName()))
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

        ImGui.SameLine(0, BtnSpacing);
        if (ImGui.Button("History##tb", new Vector2(BtnHistory, 0)))
            _plugin._historyWindow.IsVisible = !_plugin._historyWindow.IsVisible;

        ImGui.SameLine(0, BtnSpacing);
        if (ImGui.Button("Settings##tb", new Vector2(BtnSettings, 0)))
            _plugin._settingsWindow.IsVisible = !_plugin._settingsWindow.IsVisible;

        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2f);
    }

    // ── Panache meter canvas ──────────────────────────────────────────────────
    private void DrawMeterCanvas()
    {
        var session  = GetDisplaySession();
        var metric   = Config.CurrentMeter;
        var dur      = session?.DurationSeconds ?? 0;
        bool pinned       = _plugin._historyWindow.PinnedSession != null;
        uint localEntityId = Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0;
        var avail    = ImGui.GetContentRegionAvail();
        int w        = (int)Math.Max(1, avail.X);

        // Build group data
        var groups = new List<MeterCanvas.GroupData>();
        if (session != null)
        {
            var party    = session.GetSortedByType(metric, CombatantType.PartyMember);
            var friendly = session.GetSortedByType(metric, CombatantType.FriendlyPlayer);
            var enemies  = session.GetSortedByType(metric, CombatantType.Enemy);

            if (party.Count    > 0) groups.Add(new MeterCanvas.GroupData { Label = "Party",    Combatants = party,    Accent = MeterCanvas.GroupAccent(CombatantType.PartyMember) });
            if (friendly.Count > 0) groups.Add(new MeterCanvas.GroupData { Label = "Friendly", Combatants = friendly, Accent = MeterCanvas.GroupAccent(CombatantType.FriendlyPlayer) });
            if (enemies.Count  > 0) groups.Add(new MeterCanvas.GroupData { Label = "Enemies",  Combatants = enemies,  Accent = MeterCanvas.GroupAccent(CombatantType.Enemy) });

            // Unknown-type fallback for historical sessions
            if (groups.Count == 0 && session.Combatants.Count > 0)
            {
                var all = session.GetSortedByType(metric, CombatantType.Unknown);
                if (all.Count == 0)
                    all = session.Combatants.Values.OrderByDescending(c => c.GetValue(metric, dur)).ToList();
                if (all.Count > 0)
                    groups.Add(new MeterCanvas.GroupData { Label = "Combatants", Combatants = all, Accent = MeterCanvas.GroupAccent(CombatantType.Unknown) });
            }
        }

        var opts = new MeterCanvas.DisplayOptions
        {
            ShowFullName     = Config.ShowFullName,
            ShowPlayerServer = Config.ShowPlayerServer,
            ShowJobIcon      = Config.ShowJobIcon,
            ShowPercentage   = Config.ShowPercentage,
            BarColorAbgr     = Config.GetBarColor(metric),
            Style            = Config.Style,
        };

        _meter.Render(w, session, groups, metric, dur, pinned, localEntityId, opts);

        float texH       = _meter.TotalHeight > 0 ? _meter.TotalHeight : 40f;
        float titleBarH  = MeterCanvas.TitleBarH;
        var   imgOrigin  = ImGui.GetCursorScreenPos();

        if (!_meter.Handle.HasValue) { ImGui.TextDisabled("Rendering…"); return; }

        ImGui.Image(_meter.Handle.Value, new Vector2(w, texH));

        var dl = ImGui.GetWindowDrawList();

        // ── Drag handle over title bar (leaves 22px for close button) ──────
        if (!Config.LockWindow)
        {
            ImGui.SetCursorScreenPos(imgOrigin);
            ImGui.InvisibleButton("##titleDrag", new Vector2(w - 22f, titleBarH));
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }

        // ── Close button (top-right of title bar) ────────────────────────────
        var closeTL = new Vector2(imgOrigin.X + w - 20f, imgOrigin.Y + 4f);
        ImGui.SetCursorScreenPos(closeTL);
        if (ImGui.InvisibleButton("##closeBtn", new Vector2(18f, 18f)))
            _isVisible = false;
        bool hoverClose = ImGui.IsItemHovered();
        if (hoverClose) dl.AddRectFilled(closeTL, closeTL + new Vector2(18f, 18f), 0x66FF4444);
        dl.AddText(closeTL + new Vector2(4f, 2f), hoverClose ? 0xFFFFFFFF : 0x88AAAACC, "x");

        // ── Right-click → detail popup (skip title bar area) ─────────────────
        if (session != null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            var mp = ImGui.GetMousePos();
            if (mp.X >= imgOrigin.X && mp.X < imgOrigin.X + w &&
                mp.Y >= imgOrigin.Y + titleBarH && mp.Y < imgOrigin.Y + texH)
            {
                var hit = _meter.HitTest(mp.Y - imgOrigin.Y);
                if (hit != null)
                {
                    _detailEntityId = hit.EntityId;
                    _detailSession  = session;
                    ImGui.OpenPopup("##CombatantDetail");
                }
            }
        }
    }

    // ── Detail popup (right-click) ────────────────────────────────────────────
    private void DrawDetailPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 560), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##CombatantDetail", ImGuiWindowFlags.NoResize)) return;

        if (_detailSession == null
            || !_detailSession.Combatants.TryGetValue(_detailEntityId, out var c))
        {
            ImGui.Text("No data available.");
            ImGui.EndPopup();
            return;
        }

        var dur = _detailSession.DurationSeconds;

        // ── Header ────────────────────────────────────────────────────────────
        ImGui.BeginGroup();
        var displayName = string.IsNullOrEmpty(c.World) ? c.Name : $"{c.Name}@{c.World}";
        ImGui.TextColored(new Vector4(1f, 1f, 0.6f, 1f), displayName);
        var typeLabel = c.Type switch
        {
            CombatantType.PartyMember    => "Party Member",
            CombatantType.FriendlyPlayer => "Friendly Player",
            CombatantType.Enemy          => "Enemy",
            _                            => "Unknown"
        };
        ImGui.TextDisabled($"{typeLabel}   ●   {_detailSession.FormattedDuration}");
        ImGui.EndGroup();

        ImGui.Separator();

        // ── Stat summary ──────────────────────────────────────────────────────
        ImGui.TextColored(new Vector4(1f,   0.4f, 0.4f, 1f), $"DMG  {FormatNumber(c.TotalDamageDealt)}");
        ImGui.SameLine(0, 20);
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1f), $"HEAL {FormatNumber(c.TotalHealingDone)}");
        ImGui.SameLine(0, 20);
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1f), $"OHEAL {FormatNumber(c.TotalOverhealingDone)}");
        ImGui.SameLine(0, 20);
        ImGui.TextColored(new Vector4(0.5f, 0.7f, 1f,   1f), $"TAKEN {FormatNumber(c.TotalDamageTaken)}");
        ImGui.TextDisabled($"DPS {FormatNumber((long)c.GetDps(dur))}/s   HPS {FormatNumber((long)c.GetHps(dur))}/s");

        ImGui.Separator();

        // ── Tabs ──────────────────────────────────────────────────────────────
        if (ImGui.BeginTabBar("##DetailTabs"))
        {
            if (ImGui.BeginTabItem($"Damage Dealt ({c.DamageByAbility.Count})"))
            {
                DrawAbilityTable(c.DamageByAbility, c.TotalDamageDealt, showOverheal: false);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Healing Done ({c.HealingByAbility.Count})"))
            {
                DrawAbilityTable(c.HealingByAbility, c.TotalHealingDone, showOverheal: true);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Damage Taken ({c.DamageTakenByAbility.Count})"))
            {
                DrawAbilityTable(c.DamageTakenByAbility, c.TotalDamageTaken, showOverheal: false);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.EndPopup();
    }

    private void DrawAbilityTable(
        Dictionary<uint, AbilityStats> abilities, long grandTotal, bool showOverheal)
    {
        if (abilities.Count == 0) { ImGui.TextDisabled("No data recorded."); return; }

        var sorted = abilities.Values.OrderByDescending(a => a.TotalAmount).ToList();

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
                       | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit
                       | ImGuiTableFlags.Sortable;

        int colCount = showOverheal ? 7 : 6;
        if (!ImGui.BeginTable("##AbilityTable", colCount, tableFlags,
            new Vector2(0, ImGui.GetContentRegionAvail().Y - 4))) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Ability",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Hits",     ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Total",    ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Avg",      ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Min",      ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Max",      ImGuiTableColumnFlags.WidthFixed, 70);
        if (showOverheal) ImGui.TableSetupColumn("Overheal", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        foreach (var a in sorted)
        {
            var pct = grandTotal > 0 ? (float)a.TotalAmount / grandTotal * 100f : 0f;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var cellMin  = ImGui.GetCursorScreenPos();
            var cellW    = ImGui.GetContentRegionAvail().X;
            var cellH    = ImGui.GetTextLineHeightWithSpacing();
            var barColor = showOverheal ? 0xAA33CC33u : 0xAA3333CCu;
            ImGui.GetWindowDrawList().AddRectFilled(
                cellMin,
                new Vector2(cellMin.X + cellW * (pct / 100f), cellMin.Y + cellH),
                barColor);
            ImGui.TextUnformatted($"{a.Name}  ({pct:F1}%)");

            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(a.Hits.ToString());
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(FormatNumber(a.TotalAmount));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(FormatNumber((long)a.Average));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(a.Hits > 0 ? FormatNumber(a.MinHit) : "-");
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(FormatNumber(a.MaxHit));

            if (showOverheal)
            {
                ImGui.TableSetColumnIndex(6);
                if (a.TotalOverheal > 0)
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                        $"{FormatNumber(a.TotalOverheal)} ({a.OverhealPercent:F0}%)");
                else
                    ImGui.TextDisabled("-");
            }
        }

        ImGui.EndTable();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private CombatSession? GetDisplaySession()
        => _plugin._historyWindow.PinnedSession
        ?? Tracker.ActiveSession
        ?? Tracker.Store.TempSessions.LastOrDefault();

    internal static string FormatNumber(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F2}M",
        >= 1_000     => $"{n / 1_000.0:F1}K",
        _            => n.ToString()
    };

    public void Dispose()
    {
        _meter.Dispose();
    }
}
