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
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly Plugin _plugin;
    private Configuration Config  => _plugin.Config;
    private CombatTracker Tracker => _plugin.Tracker;

    private readonly MeterCanvas _meter;
    internal MeterCanvas Meter => _meter;

    // Right-click detail state
    private uint           _detailEntityId;
    private CombatSession? _detailSession;

    // Scroll state
    private float _scrollY = 0f;
    private const float ToolbarH = 26f;

    // Layout state shared between DrawCanvasHeader and DrawCanvasBody
    private Vector2 _imgOrigin;
    private Vector2 _bodyOrigin;
    private float   _headerH;
    private float   _texH;
    private float   _bodyTexH;
    private float   _bodyViewH;
    private float   _maxScroll;
    private int     _canvasW;
    private CombatSession? _frameSession;

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

        // Dynamic min size: always fit at least 4 rows + 1 group header + toolbar + canvas header
        const float WinPadV  = 10f; // 5px top + 5px bottom window padding
        const float MinRows  = 4f;
        const float MinWidth = 280f;
        float dynHeaderH = MeterCanvas.GetEffectiveHeaderH(new MeterCanvas.DisplayOptions
        {
            ShowTitleBar       = Config.ShowTitleBar,
            ShowEncounterTotal = Config.ShowEncounterTotal,
        });
        float dynRowH   = Config.Style == WindowStyle.Minimal ? MeterCanvas.MinRowH : MeterCanvas.RowH;
        float dynGroupH = Config.ShowGroupHeaders ? MeterCanvas.GroupH : 0f;
        float minHeight = dynHeaderH + 1f + dynGroupH + MinRows * dynRowH + ToolbarH + WinPadV;

        ImGui.SetNextWindowSizeConstraints(new Vector2(MinWidth, minHeight), new Vector2(1000, 3000));
        ImGui.SetNextWindowSize(new Vector2(420, 380), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Config.Opacity);

        const float WinPad = 5f;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,  new Vector2(WinPad, WinPad));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,    Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.5f);
        ImGui.PushStyleColor(ImGuiCol.Border,    new Vector4(0.38f, 0.22f, 0.24f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg,  new Vector4(0x10 / 255f, 0x0C / 255f, 0x0D / 255f, 1f));
        bool open = ImGui.Begin("###DamageMeterMain", ref _isVisible, flags);
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);

        if (!open) { ImGui.End(); return; }

        DrawCanvasHeader();   // renders SkiaSharp canvas + draws header slice
        DrawToolbar();        // toolbar always sits right below the header
        if (Config.CurrentView == ViewMode.Graph)
            DrawGraphBody();  // line-graph alternative to the bar body
        else
            DrawCanvasBody(); // body slice + scrollbar + hit-test buttons
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
        const float BtnView     = 44f;   // "Chart" / "Graph" — each
        const float BtnHistory  = 62f;
        const float BtnSettings = 68f;
        const float BtnSpacing  =  4f;
        const float RightMargin =  8f;

        float avail  = ImGui.GetContentRegionAvail().X;
        float comboW = avail - BtnView * 2 - BtnHistory - BtnSettings - BtnSpacing * 4f - RightMargin;

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f);

        // Chart / Graph view toggle — push a highlighted color when active.
        DrawViewToggleButton("Chart##tb", ViewMode.Chart, BtnView);
        ImGui.SameLine(0, BtnSpacing);
        DrawViewToggleButton("Graph##tb", ViewMode.Graph, BtnView);

        ImGui.SameLine(0, BtnSpacing);
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

    private void DrawViewToggleButton(string label, ViewMode mode, float width)
    {
        bool active = Config.CurrentView == mode;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        0xFF3A3A70);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xFF4A4A80);
        }
        if (ImGui.Button(label, new Vector2(width, 0)))
        {
            Config.CurrentView = mode;
            _plugin.SaveConfig();
        }
        if (active) ImGui.PopStyleColor(2);
    }

    // ── Phase 1: render canvas + draw header slice ────────────────────────────
    // Stores layout state in fields for DrawCanvasBody to consume.
    private void DrawCanvasHeader()
    {
        _frameSession  = GetDisplaySession();
        var metric     = Config.CurrentMeter;
        var dur        = _frameSession?.DurationSeconds ?? 0;
        bool pinned    = _plugin._historyWindow.PinnedSession != null;
        uint localId   = Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0;
        var avail      = ImGui.GetContentRegionAvail();
        _canvasW       = (int)Math.Max(1, avail.X);

        // Build group data
        var groups = new List<MeterCanvas.GroupData>();
        if (_frameSession != null)
        {
            var party    = _frameSession.GetSortedByType(metric, CombatantType.PartyMember);
            var friendly = _frameSession.GetSortedByType(metric, CombatantType.FriendlyPlayer);
            var enemies  = _frameSession.GetSortedByType(metric, CombatantType.Enemy);

            if (party.Count    > 0) groups.Add(new MeterCanvas.GroupData { Label = "Party",    Combatants = party,    Accent = MeterCanvas.GroupAccent(CombatantType.PartyMember) });
            if (friendly.Count > 0 && Config.ShowFriendlyGroup) groups.Add(new MeterCanvas.GroupData { Label = "Friendly", Combatants = friendly, Accent = MeterCanvas.GroupAccent(CombatantType.FriendlyPlayer) });
            if (enemies.Count  > 0 && Config.ShowEnemyGroup)    groups.Add(new MeterCanvas.GroupData { Label = "Enemies",  Combatants = enemies,  Accent = MeterCanvas.GroupAccent(CombatantType.Enemy) });

            if (groups.Count == 0 && _frameSession.Combatants.Count > 0)
            {
                var all = _frameSession.GetSortedByType(metric, CombatantType.Unknown);
                if (all.Count == 0)
                    all = _frameSession.Combatants.Values.OrderByDescending(c => c.GetValue(metric, dur)).ToList();
                if (all.Count > 0)
                    groups.Add(new MeterCanvas.GroupData { Label = "Combatants", Combatants = all, Accent = MeterCanvas.GroupAccent(CombatantType.Unknown) });
            }
        }

        const float SbTrackW = 8f;
        var opts = new MeterCanvas.DisplayOptions
        {
            ShowFullName       = Config.ShowFullName,
            ShowPlayerServer   = Config.ShowPlayerServer,
            ShowJobIcon        = Config.ShowJobIcon,
            ShowPercentage     = Config.ShowPercentage,
            BarColorAbgr       = Config.GetBarColor(metric),
            Style              = Config.Style,
            ShowEncounterTotal = Config.ShowEncounterTotal,
            ShowGroupHeaders   = Config.ShowGroupHeaders,
            ShowTitleBar       = Config.ShowTitleBar,
        };

        _headerH = MeterCanvas.GetEffectiveHeaderH(opts);

        // Use last frame's TotalHeight to decide whether a scrollbar is needed
        float prevTexH   = _meter.TotalHeight > 0 ? _meter.TotalHeight : 40f;
        float prevBodyTH = Math.Max(0f, prevTexH - _headerH);
        float prevBodyVH = Math.Max(0f, avail.Y - ToolbarH - _headerH);
        opts.ScrollbarW  = prevBodyTH > prevBodyVH ? SbTrackW : 0f;

        _meter.Render(_canvasW, _frameSession, groups, metric, dur, pinned, localId, opts);
        _texH    = _meter.TotalHeight > 0 ? _meter.TotalHeight : 40f;
        _bodyTexH  = Math.Max(0f, _texH - _headerH);
        _bodyViewH = Math.Max(0f, avail.Y - ToolbarH - _headerH);
        _maxScroll = Math.Max(0f, _bodyTexH - _bodyViewH);

        // Mouse wheel (processed here so scroll updates before body is drawn)
        if (ImGui.IsWindowHovered() && _maxScroll > 0f)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
                _scrollY = Math.Clamp(_scrollY - wheel * MeterCanvas.RowH, 0f, _maxScroll);
        }
        _scrollY = Math.Clamp(_scrollY, 0f, _maxScroll);

        if (!_meter.Handle.HasValue) { ImGui.TextDisabled("Rendering…"); return; }

        _imgOrigin = ImGui.GetCursorScreenPos();

        // Draw the header slice — toolbar will be placed right after this by Draw()
        if (_headerH > 0f)
            ImGui.Image(_meter.Handle.Value, new Vector2(_canvasW, _headerH),
                new Vector2(0f, 0f), new Vector2(1f, _headerH / _texH));

        // Title bar overlay buttons (drag + close) — placed over the header image
        if (Config.ShowTitleBar)
        {
            float titleBarH = MeterCanvas.TitleBarH;
            var dl = ImGui.GetWindowDrawList();

            if (!Config.LockWindow)
            {
                ImGui.SetCursorScreenPos(_imgOrigin);
                ImGui.InvisibleButton("##titleDrag", new Vector2(_canvasW - 22f, titleBarH));
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
            }

            var closeTL = new Vector2(_imgOrigin.X + _canvasW - 20f, _imgOrigin.Y + 4f);
            ImGui.SetCursorScreenPos(closeTL);
            if (ImGui.InvisibleButton("##closeBtn", new Vector2(18f, 18f)))
                _isVisible = false;
            bool hoverClose = ImGui.IsItemHovered();
            if (hoverClose) dl.AddRectFilled(closeTL, closeTL + new Vector2(18f, 18f), 0x66FF4444);
            dl.AddText(closeTL + new Vector2(4f, 2f), hoverClose ? 0xFFFFFFFF : 0x88AAAACC, "x");
        }

        // Restore cursor to end of header so DrawToolbar renders immediately below it
        ImGui.SetCursorScreenPos(new Vector2(_imgOrigin.X, _imgOrigin.Y + _headerH));
    }

    // ── Phase 2: draw body slice + scrollbar + hit-test ───────────────────────
    private void DrawCanvasBody()
    {
        if (!_meter.Handle.HasValue) return;

        _bodyOrigin = ImGui.GetCursorScreenPos();

        if (_bodyViewH > 0f)
        {
            float availBodyContent = Math.Max(0f, _bodyTexH - _scrollY);
            float displayBodyH     = Math.Min(_bodyViewH, availBodyContent);
            if (displayBodyH > 0f)
            {
                float uv0y = (_headerH + _scrollY) / _texH;
                float uv1y = Math.Min(1f, (_headerH + _scrollY + displayBodyH) / _texH);
                ImGui.Image(_meter.Handle.Value, new Vector2(_canvasW, displayBodyH),
                    new Vector2(0f, uv0y), new Vector2(1f, uv1y));
            }
        }

        var dl = ImGui.GetWindowDrawList();

        // Scrollbar thumb
        if (_maxScroll > 0f && _bodyViewH > 0f)
        {
            const float sbW = 4f;
            float barH      = Math.Max(20f, _bodyViewH * (_bodyViewH / _bodyTexH));
            float barY      = _bodyOrigin.Y + (_scrollY / _maxScroll) * (_bodyViewH - barH);
            float barX      = _imgOrigin.X + _canvasW - sbW - 2f;
            dl.AddRectFilled(new Vector2(barX, barY), new Vector2(barX + sbW, barY + barH),
                0x55FFFFFF, 2f);
        }

        // ── Left-click → accordion group toggle ──────────────────────────────
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsPopupOpen("##CombatantDetail"))
        {
            var mp = ImGui.GetMousePos();
            if (mp.X >= _imgOrigin.X && mp.X < _imgOrigin.X + _canvasW &&
                mp.Y >= _bodyOrigin.Y && mp.Y < _bodyOrigin.Y + _bodyViewH)
            {
                float canvasY = (mp.Y - _bodyOrigin.Y) + _headerH + _scrollY;
                var grp = _meter.HitTestGroup(canvasY);
                if (grp != null) _meter.ToggleGroup(grp);
            }
        }

        // ── Right-click → detail popup ────────────────────────────────────────
        if (_frameSession != null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            var mp = ImGui.GetMousePos();
            if (mp.X >= _imgOrigin.X && mp.X < _imgOrigin.X + _canvasW &&
                mp.Y >= _bodyOrigin.Y && mp.Y < _bodyOrigin.Y + _bodyViewH)
            {
                float canvasY = (mp.Y - _bodyOrigin.Y) + _headerH + _scrollY;
                var hit = _meter.HitTest(canvasY);
                if (hit != null)
                {
                    _detailEntityId = hit.EntityId;
                    _detailSession  = _frameSession;
                    ImGui.OpenPopup("##CombatantDetail");
                }
            }
        }
    }

    // ── Graph view body ───────────────────────────────────────────────────────
    // Time-series line chart. X-axis = elapsed seconds since session start, Y-axis
    // = cumulative value of the active MeterType per combatant. One line per top-N
    // combatant, color-coded. For per-second metrics (DPS / HPS / DTPS), the value
    // shown is the rolling cumulative-divided-by-elapsed at each bin so the line
    // converges to the player's average rate.
    private static readonly uint[] GraphLineColors =
    {
        0xFF6464FFu, // red-orange
        0xFFFFC864u, // sky-blue
        0xFF64C864u, // green
        0xFF64C8FFu, // amber
        0xFFFF64C8u, // purple
        0xFF64FFFFu, // yellow
        0xFFFFFF64u, // cyan
        0xFFC864FFu, // pink
    };

    private void DrawGraphBody()
    {
        _frameSession = GetDisplaySession();
        var session   = _frameSession;
        var bodyTL    = ImGui.GetCursorScreenPos();
        var avail     = ImGui.GetContentRegionAvail();
        // Leave room for the window border at the bottom.
        var bodySize  = new Vector2(avail.X, Math.Max(80f, avail.Y));
        var dl        = ImGui.GetWindowDrawList();

        dl.AddRectFilled(bodyTL, bodyTL + bodySize, 0xFF14101A);

        if (session == null || session.Combatants.Count == 0)
        {
            var msg = "No data to graph.";
            var sz  = ImGui.CalcTextSize(msg);
            dl.AddText(bodyTL + (bodySize - sz) * 0.5f, 0xFF808080, msg);
            ImGui.Dummy(bodySize);
            return;
        }

        var dur = session.DurationSeconds;
        if (dur < 1.0)
        {
            var msg = "Fight too short to graph (need ≥ 1s).";
            var sz  = ImGui.CalcTextSize(msg);
            dl.AddText(bodyTL + (bodySize - sz) * 0.5f, 0xFF808080, msg);
            ImGui.Dummy(bodySize);
            return;
        }

        var metric = Config.CurrentMeter;

        // Only metrics with per-event time data are graphable. Others can be
        // added later by tracking DamageTakenEvents / OverhealEvents.
        bool isDamageBased = metric == MeterType.DamageDealt || metric == MeterType.DPS;
        bool isHealBased   = metric == MeterType.HealingDone || metric == MeterType.HPS;
        if (!isDamageBased && !isHealBased)
        {
            var msg = $"Graph not supported for '{metric.DisplayName()}' yet.\nPick Damage Dealt / DPS / Healing Done / HPS.";
            var sz  = ImGui.CalcTextSize(msg);
            dl.AddText(bodyTL + (bodySize - sz) * 0.5f, 0xFFA0A0A0, msg);
            ImGui.Dummy(bodySize);
            return;
        }
        bool isRateMetric = metric == MeterType.DPS || metric == MeterType.HPS;

        // Pick top-8 combatants by total of the selected metric.
        var combatants = session.Combatants.Values
            .Where(c => (isDamageBased ? c.TotalDamageDealt : c.TotalHealingDone) > 0)
            .OrderByDescending(c => c.GetValue(metric, dur))
            .Take(8)
            .ToList();

        if (combatants.Count == 0)
        {
            var msg = "No combatants with " + (isDamageBased ? "damage" : "healing") + " yet.";
            var sz  = ImGui.CalcTextSize(msg);
            dl.AddText(bodyTL + (bodySize - sz) * 0.5f, 0xFF808080, msg);
            ImGui.Dummy(bodySize);
            return;
        }

        // Plot region.
        const float PadL = 50f, PadR = 12f, PadT = 28f, PadB = 22f;
        var plotTL    = bodyTL + new Vector2(PadL, PadT);
        var plotSize  = new Vector2(bodySize.X - PadL - PadR, bodySize.Y - PadT - PadB);
        if (plotSize.X < 40 || plotSize.Y < 40)
        {
            ImGui.Dummy(bodySize);
            return;
        }

        // Sample the plot at one bin per pixel (capped) so lines stay smooth at
        // any window width without doing more work than there are pixels.
        int nBins = Math.Clamp((int)plotSize.X, 32, 400);

        // Build series; track global max for the Y scale.
        var series = new List<(CombatantData c, double[] vals, uint color)>();
        double maxV = 0;
        for (int i = 0; i < combatants.Count; i++)
        {
            var c    = combatants[i];
            var arr  = ComputeSeries(c, isDamageBased, isRateMetric, dur, nBins);
            var hi   = 0.0;
            for (int k = 0; k < arr.Length; k++) if (arr[k] > hi) hi = arr[k];
            if (hi > maxV) maxV = hi;
            series.Add((c, arr, GraphLineColors[i % GraphLineColors.Length]));
        }
        if (maxV <= 0) maxV = 1;

        // Y-axis grid + labels.
        const int yTicks = 4;
        for (int i = 0; i <= yTicks; i++)
        {
            var frac = (float)i / yTicks;
            var y    = plotTL.Y + plotSize.Y * (1f - frac);
            var v    = (long)(maxV * frac);
            uint gridColor = i == 0 ? 0xFF40304Cu : 0xFF2A2030u;
            dl.AddLine(new Vector2(plotTL.X, y), new Vector2(plotTL.X + plotSize.X, y), gridColor);
            var label = FormatNumber(v) + (isRateMetric ? "/s" : "");
            var lsz   = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(plotTL.X - 4 - lsz.X, y - lsz.Y * 0.5f), 0xFFA0A0A0, label);
        }

        // X-axis labels (5 ticks).
        for (int i = 0; i <= 4; i++)
        {
            var x = plotTL.X + plotSize.X * i / 4;
            var t = dur * i / 4;
            int s = (int)t;
            var label = $"{s / 60}:{s % 60:D2}";
            var lsz   = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(x - lsz.X * 0.5f, plotTL.Y + plotSize.Y + 4), 0xFFA0A0A0, label);
        }

        // Plot lines.
        foreach (var (c, vals, color) in series)
        {
            var prev = new Vector2(plotTL.X, plotTL.Y + plotSize.Y);
            for (int i = 0; i < nBins; i++)
            {
                var x = plotTL.X + plotSize.X * ((float)(i + 1) / nBins);
                var y = plotTL.Y + plotSize.Y * (1f - (float)(vals[i] / maxV));
                dl.AddLine(prev, new Vector2(x, y), color, 1.8f);
                prev = new Vector2(x, y);
            }
        }

        // Legend strip across the top of the plot.
        var legX = bodyTL.X + PadL;
        var legY = bodyTL.Y + 6;
        const float swatch = 12f;
        foreach (var (c, _, color) in series)
        {
            var name  = c.DisplayName(false, initialsOnly: true);
            var label = $"{name}  {FormatNumber((long)c.GetValue(metric, dur))}";
            var lsz   = ImGui.CalcTextSize(label);
            if (legX + swatch + 4 + lsz.X + 12 > bodyTL.X + bodySize.X) break;
            dl.AddRectFilled(new Vector2(legX, legY + 2),
                             new Vector2(legX + swatch, legY + swatch + 2), color);
            dl.AddText(new Vector2(legX + swatch + 4, legY), 0xFFFFFFFF, label);
            legX += swatch + 4 + lsz.X + 12;
        }

        ImGui.Dummy(bodySize);
    }

    // Returns the cumulative value of the selected metric at nBins evenly-spaced
    // time samples from 0..dur. For rate metrics (DPS / HPS), divides by elapsed.
    // Events are appended in time order, so we use a single forward sweep.
    private static double[] ComputeSeries(
        CombatantData c, bool isDamageBased, bool isRateMetric, double dur, int nBins)
    {
        var events = isDamageBased ? c.DamageEvents : c.HealingEvents;
        var result = new double[nBins];
        long running = 0;
        int  evIdx   = 0;
        double durMs = dur * 1000.0;
        for (int i = 0; i < nBins; i++)
        {
            var binMs = (i + 1) * durMs / nBins;
            while (evIdx < events.Count && events[evIdx].TickMs <= binMs)
            {
                running += events[evIdx].Amount;
                evIdx++;
            }
            double elapsedSec = (i + 1) * dur / nBins;
            result[i] = isRateMetric && elapsedSec > 0 ? running / elapsedSec : running;
        }
        return result;
    }

    // ── Detail popup (right-click) ────────────────────────────────────────────
    private void DrawDetailPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 560), ImGuiCond.Always);
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
                DrawAbilityTable(c.DamageByAbility, c.TotalDamageDealt, dur, "DPS", showOverheal: false);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Healing Done ({c.HealingByAbility.Count})"))
            {
                DrawAbilityTable(c.HealingByAbility, c.TotalHealingDone, dur, "HPS", showOverheal: true);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Damage Taken ({c.DamageTakenByAbility.Count})"))
            {
                DrawAbilityTable(c.DamageTakenByAbility, c.TotalDamageTaken, dur, "DTPS", showOverheal: false);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.EndPopup();
    }

    private void DrawAbilityTable(
        Dictionary<uint, AbilityStats> abilities, long grandTotal,
        double durationSeconds, string perSecLabel, bool showOverheal)
    {
        if (abilities.Count == 0) { ImGui.TextDisabled("No data recorded."); return; }

        var sorted = abilities.Values.OrderByDescending(a => a.TotalAmount).ToList();

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
                       | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit
                       | ImGuiTableFlags.Sortable;

        int colCount = showOverheal ? 8 : 7;
        if (!ImGui.BeginTable("##AbilityTable", colCount, tableFlags,
            new Vector2(0, 380f))) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Ability",     ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Hits",        ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Total",       ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn(perSecLabel,   ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Avg",         ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Min",         ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Max",         ImGuiTableColumnFlags.WidthFixed, 70);
        if (showOverheal) ImGui.TableSetupColumn("Overheal", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        foreach (var a in sorted)
        {
            var pct = grandTotal > 0 ? (float)a.TotalAmount / grandTotal * 100f : 0f;
            var perSec = durationSeconds > 0 ? a.TotalAmount / durationSeconds : 0;

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
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(durationSeconds > 0 ? FormatNumber((long)perSec) : "-");
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(FormatNumber((long)a.Average));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(a.MinHit > 0 ? FormatNumber(a.MinHit) : "-");
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(FormatNumber(a.MaxHit));

            if (showOverheal)
            {
                ImGui.TableSetColumnIndex(7);
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
