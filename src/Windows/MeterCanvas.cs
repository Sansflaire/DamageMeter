using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using DamageMeter.Panache;

using SkiaSharp;

namespace DamageMeter.Windows;

/// <summary>
/// Full Panache-style SkiaSharp canvas for the damage meter.
/// Renders the encounter header, group headers, and combatant rows as a single
/// GPU texture displayed via ImGui.Image(). The Panache pipeline:
///   SKCanvas → RenderSurface (CPU RGBA) → TextureManager → ImGui.Image()
/// </summary>
public sealed class MeterCanvas : IDisposable
{
    // ── Section heights ───────────────────────────────────────────────────────
    public  const float TitleBarH  = 28f;  // exposed: "DAMAGE METER" title strip
    private const float EncounterH = 64f;  // encounter info — centered single line
    public  const float HeaderH    = TitleBarH + EncounterH; // 92px total header
    private const float DividerH   =  1f;
    private const float GroupH     = 30f;  // per-group title row
    public  const float RowH       = 66f;  // per-combatant row (Modern/Classic)
    public  const float MinRowH    = 26f;  // per-combatant row (Minimal — single line)
    private const float GroupGap   =  6f;

    // ── Card layout ───────────────────────────────────────────────────────────
    private const float CardMargin =  3f;  // inset of card from row allocation
    private const float CardR      =  7f;  // card corner radius
    private const float CardH      = RowH - CardMargin * 2; // 60px card height

    // ── Bar layout (within card) ──────────────────────────────────────────────
    private const float BarH       = 10f;
    private const float BarPadX    =  8f;  // bar left/right padding within card
    private const float BarPadB    =  5f;  // bar bottom padding within card
    private const float BarR       =  5f;  // bar corner radius (pill shape)

    // ── Icon badge ────────────────────────────────────────────────────────────
    private const float BadgeW     = 28f;  // job icon — in rounded box
    private const float BadgeH     = 28f;
    private const float BadgeR     =  5f;

    // ── Row layout (left → right within card) ─────────────────────────────────
    private const float StripeW    =  4f;
    private const float LeftPad    =  8f;
    private const float RankW      = 27f;
    private const float RightPad   = 10f;
    private const float PctW       = 36f;
    private const float ValW       = 72f;

    // ── Font sizes ────────────────────────────────────────────────────────────
    private const float FtHeader = 14f;
    private const float FtZone   = 11f;
    private const float FtTimer  = 22f;
    private const float FtMain   = 13f;
    private const float FtSub    = 11f;
    private const float FtRank   = 17f;
    private const float FtName   = 13f;
    private const float FtValue  = 16f;

    // ── Color palette — metallic rose-grey scheme ─────────────────────────────
    // Background layers
    private static readonly SKColor BgDeep    = new(0x10, 0x0C, 0x0D, 0xFF);  // near-black warm charcoal
    private static readonly SKColor BgGroup   = new(0x1C, 0x16, 0x17, 0xFF);  // group header strip
    private static readonly SKColor BgHeader1 = new(0x1A, 0x14, 0x15, 0xFF);  // encounter grad top
    private static readonly SKColor BgHeader2 = new(0x13, 0x0F, 0x10, 0xFF);  // encounter grad bottom

    // Metallic card layers (replaces BgEven/BgOdd with a gradient system)
    private static readonly SKColor MetalBase   = new(0x2A, 0x1E, 0x20, 0xFF);  // dark rose-pewter base
    private static readonly SKColor MetalWarm   = new(0xB4, 0x68, 0x38, 0xA8);  // copper-orange (left radial)
    private static readonly SKColor MetalCool   = new(0x08, 0x05, 0x06, 0xA0);  // dark right edge (lighter → darker)
    private static readonly SKColor MetalSpec   = new(0x0C, 0x08, 0x09, 0xFF);  // top edge dark vignette
    private static readonly SKColor MetalGlow   = new(0xA0, 0x58, 0x50, 0xFF);  // bloom tint (fixed rose)

    // Text — warm neutral instead of blue-tinted
    private static readonly SKColor TextPrim   = new(0xF2, 0xEB, 0xEC, 0xFF);  // warm white
    private static readonly SKColor TextMuted  = new(0x96, 0x88, 0x8A, 0xFF);  // warm muted grey
    private static readonly SKColor TextDim    = new(0x64, 0x59, 0x5B, 0xFF);  // warm dim
    private static readonly SKColor TextLive   = new(0x30, 0xFF, 0x70, 0xFF);  // green (keep)
    private static readonly SKColor TextEnded  = new(0x8A, 0x82, 0x84, 0xFF);  // neutral warm grey
    private static readonly SKColor TextTimer  = new(0xFF, 0xCC, 0x44, 0xFF);  // gold (keep)
    private static readonly SKColor TextZone   = new(0xC4, 0xB8, 0xBA, 0xFF);  // warm silver

    private static readonly SKColor Gold   = new(0xFF, 0xB8, 0x00, 0xFF);
    private static readonly SKColor Silver = new(0xC8, 0xC0, 0xC2, 0xFF);
    private static readonly SKColor Bronze = new(0xC8, 0x78, 0x28, 0xFF);

    private static readonly SKColor AccentParty    = new(0x44, 0x8C, 0xFF, 0xFF);
    private static readonly SKColor AccentFriendly = new(0x44, 0xCC, 0x88, 0xFF);
    private static readonly SKColor AccentEnemy    = new(0xFF, 0x44, 0x44, 0xFF);
    private static readonly SKColor AccentOther    = new(0x88, 0x88, 0xCC, 0xFF);

    private static readonly SKColor TankCol    = new(0x3B, 0x84, 0xFF, 0xFF);
    private static readonly SKColor HealerCol  = new(0x28, 0xCC, 0x58, 0xFF);
    private static readonly SKColor MeleeCol   = new(0xEE, 0x44, 0x44, 0xFF);
    private static readonly SKColor RangedCol  = new(0xEE, 0xA0, 0x20, 0xFF);
    private static readonly SKColor CasterCol  = new(0xCC, 0x44, 0xEE, 0xFF);
    private static readonly SKColor UnknownCol = new(0x44, 0x55, 0x66, 0xFF);

    private static readonly SKColor LocalAccent = new(0x44, 0xEE, 0xFF, 0xFF);

    // ── Display options ───────────────────────────────────────────────────────
    public struct DisplayOptions
    {
        public bool ShowFullName;
        public bool ShowPlayerServer;
        public bool ShowJobIcon;
        public bool ShowPercentage;
        public uint BarColorAbgr;
        public WindowStyle Style;
    }

    // ── Group input ───────────────────────────────────────────────────────────
    public struct GroupData
    {
        public string              Label;
        public List<CombatantData> Combatants;
        public SKColor             Accent;
    }

    // ── Rendering infrastructure ──────────────────────────────────────────────
    private RenderSurface?  _surface;
    private readonly TextureManager _tex;
    private readonly SKPaint _p = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    // ── Job icon cache ────────────────────────────────────────────────────────
    private readonly Dictionary<byte, SKImage?> _jobIconCache = new();

    // ── Hit-test tables ───────────────────────────────────────────────────────
    private readonly List<(float Y, float H, CombatantData? Data)>    _hitRows    = new();
    private readonly List<(float Y, float H, string Label)>            _groupHits  = new();

    // ── Animation state ───────────────────────────────────────────────────────
    private DateTime _lastRenderTick = DateTime.UtcNow;
    private float    _animTime       = 0f;

    // Shake — triggered when a combatant's rank changes
    private readonly Dictionary<uint, int>   _prevRanks   = new();
    private readonly Dictionary<uint, float> _shakeTimers = new();

    // Slide-in — triggered when a new combatant first appears
    private readonly HashSet<uint>           _seenEntities  = new();
    private readonly Dictionary<uint, float> _slideProgress = new(); // 0→1

    // Accordion — per-group collapse/expand animation
    private readonly Dictionary<string, bool>  _groupCollapsed = new(); // true = collapsed
    private readonly Dictionary<string, float> _groupExpandT   = new(); // 0=collapsed,1=expanded

    public float        TotalHeight { get; private set; }
    public ImTextureID? Handle      => _tex.Handle;

    public MeterCanvas(ITextureProvider tp) => _tex = new TextureManager(tp);

    // ── Main render entry ─────────────────────────────────────────────────────
    public void Render(int width, CombatSession? session, List<GroupData> groups, MeterType metric, double dur,
                       bool isPinned = false, uint localEntityId = 0, DisplayOptions opts = default)
    {
        // ── Frame time ────────────────────────────────────────────────────────
        var now = DateTime.UtcNow;
        float dt = (float)(now - _lastRenderTick).TotalSeconds;
        dt = Math.Min(dt, 0.1f); // cap at 100ms to avoid jumps after pause
        _lastRenderTick = now;
        _animTime += dt;

        // ── Detect new entities (slide-in) ────────────────────────────────────
        foreach (var g in groups)
            foreach (var c in g.Combatants)
                if (_seenEntities.Add(c.EntityId))
                    _slideProgress[c.EntityId] = 0f;

        // ── Advance slide progress ────────────────────────────────────────────
        const float SlideDuration = 0.35f;
        foreach (var id in _slideProgress.Keys.ToList())
        {
            _slideProgress[id] = Math.Min(1f, _slideProgress[id] + dt / SlideDuration);
            if (_slideProgress[id] >= 1f) _slideProgress.Remove(id);
        }

        // ── Advance shake timers ──────────────────────────────────────────────
        foreach (var id in _shakeTimers.Keys.ToList())
        {
            _shakeTimers[id] = Math.Max(0f, _shakeTimers[id] - dt);
            if (_shakeTimers[id] == 0f) _shakeTimers.Remove(id);
        }

        // ── Initialize & animate accordion groups ─────────────────────────────
        foreach (var g in groups)
        {
            if (!_groupExpandT.ContainsKey(g.Label))
                _groupExpandT[g.Label] = 1f; // default expanded
        }
        const float AccordionSpeed = 8f;
        foreach (var label in _groupExpandT.Keys.ToList())
        {
            bool collapsed = _groupCollapsed.TryGetValue(label, out bool c) && c;
            float target  = collapsed ? 0f : 1f;
            float current = _groupExpandT[label];
            float delta   = target - current;
            if (MathF.Abs(delta) < 0.001f) { _groupExpandT[label] = target; continue; }
            _groupExpandT[label] = current + delta * Math.Min(1f, AccordionSpeed * dt);
        }

        // ── Compute & pre-detect rank changes ────────────────────────────────
        var currentRanks = new Dictionary<uint, int>();
        foreach (var g in groups)
            for (int i = 0; i < g.Combatants.Count; i++)
                currentRanks[g.Combatants[i].EntityId] = i + 1;

        foreach (var (id, newRank) in currentRanks)
        {
            if (_prevRanks.TryGetValue(id, out int prev) && prev != newRank)
                _shakeTimers[id] = 0.45f; // trigger shake
            _prevRanks[id] = newRank;
        }

        _hitRows.Clear();
        _groupHits.Clear();

        float rowH   = opts.Style == WindowStyle.Minimal ? MinRowH : RowH;
        float totalH = ComputeHeight(groups, session, rowH);
        TotalHeight = totalH;

        int w = Math.Max(1, width);
        int h = Math.Max(1, (int)MathF.Ceiling(totalH));

        if (_surface == null || _surface.Width != w || _surface.Height != h)
        {
            _surface?.Dispose();
            _surface = new RenderSurface(w, h);
        }

        var canvas = _surface.Canvas;
        canvas.Clear(BgDeep);

        // Compute group total for encounter header
        double groupTotal = 0;
        foreach (var g in groups)
            foreach (var c in g.Combatants)
                groupTotal += c.GetValue(metric, dur);

        DrawEncounterHeader(canvas, session, w, metric, dur, isPinned, groupTotal, opts);
        float y = HeaderH + DividerH;

        bool firstGroup = true;
        foreach (var group in groups)
        {
            if (group.Combatants.Count == 0) continue;
            if (!firstGroup) y += GroupGap;
            firstGroup = false;

            float expandT = _groupExpandT.TryGetValue(group.Label, out float et) ? et : 1f;
            bool  collapsed = _groupCollapsed.TryGetValue(group.Label, out bool gc) && gc;

            double topVal = group.Combatants.Max(c => c.GetValue(metric, dur));

            DrawGroupHeader(canvas, group, w, y, topVal, metric, dur, opts, collapsed, expandT);
            _groupHits.Add((y, GroupH, group.Label));
            _hitRows.Add((y, GroupH, null));
            y += GroupH;

            if (expandT > 0.001f)
            {
                float rowsH    = group.Combatants.Count * rowH;
                float visibleH = rowsH * expandT;

                // Clip rows to animated accordion height
                int clipSave = canvas.Save();
                canvas.ClipRect(SKRect.Create(0, y, w, visibleH));

                float rowY = y;
                for (int i = 0; i < group.Combatants.Count; i++)
                {
                    var c   = group.Combatants[i];
                    var val = c.GetValue(metric, dur);
                    var pct = topVal > 0 ? val / topVal : 0.0;

                    DrawRow(canvas, c, i + 1, i, group.Accent, w, rowY, val, pct, metric, localEntityId, opts, dt, rowH);
                    _hitRows.Add((rowY, rowH, c));
                    rowY += rowH;
                }

                canvas.RestoreToCount(clipSave);
                y += visibleH;
            }
        }

        if (firstGroup)
        {
            float msgY = HeaderH + DividerH + 24f;
            Draw(canvas, "No encounter data yet.", w / 2f, msgY, FtSub, false, TextMuted, Align.Center);
        }

        _tex.Upload(_surface);
    }

    // ── Group public API (for MainWindow click handling) ──────────────────────
    public string? HitTestGroup(float imageY)
    {
        foreach (var (ry, rh, label) in _groupHits)
            if (imageY >= ry && imageY < ry + rh) return label;
        return null;
    }

    public void ToggleGroup(string label)
    {
        bool nowCollapsed = !(_groupCollapsed.TryGetValue(label, out bool c) && c);
        _groupCollapsed[label] = nowCollapsed;
        if (!_groupExpandT.ContainsKey(label))
            _groupExpandT[label] = nowCollapsed ? 0f : 1f;
    }

    // ── PNG export for StatusApi ──────────────────────────────────────────────
    public byte[]? GetPngBytes() => _surface?.GetPngBytes();

    // ── Hit test (right-click detail) ─────────────────────────────────────────
    public CombatantData? HitTest(float imageY)
    {
        foreach (var (ry, rh, data) in _hitRows)
            if (imageY >= ry && imageY < ry + rh) return data;
        return null;
    }

    // ── Height computation (respects accordion) ────────────────────────────────
    private float ComputeHeight(List<GroupData> groups, CombatSession? session, float rowH)
    {
        float h = HeaderH + DividerH;
        bool first = true;
        foreach (var g in groups)
        {
            if (g.Combatants.Count == 0) continue;
            if (!first) h += GroupGap;
            first = false;
            float expandT = _groupExpandT.TryGetValue(g.Label, out float et) ? et : 1f;
            h += GroupH + g.Combatants.Count * rowH * expandT;
        }
        if (first && session != null) h += 30f;
        return h;
    }

    // ── Encounter header ──────────────────────────────────────────────────────
    private void DrawEncounterHeader(SKCanvas canvas, CombatSession? session, int w, MeterType metric,
                                     double dur, bool isPinned, double groupTotal, DisplayOptions opts)
    {
        bool isMinimal = opts.Style == WindowStyle.Minimal;
        bool isModern  = opts.Style == WindowStyle.Modern;

        // Title bar — dark warm metallic (same palette across all styles)
        if (isMinimal)
        {
            _p.Color = new SKColor(0x0E, 0x0A, 0x0B, 0xFF);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
        }
        else
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, 0),
                new[] { new SKColor(0x22, 0x18, 0x1A, 0xFF), new SKColor(0x18, 0x14, 0x16, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
            _p.Shader = null;
        }

        // Top-edge specular stripe (rose-gold → dusty rose)
        if (!isMinimal)
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, 0),
                new[] { new SKColor(0xC8, 0x78, 0x60, 0xFF), new SKColor(0xA0, 0x70, 0x78, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, 2f), _p);
            _p.Shader = null;
        }

        float titleMidY = TitleBarH * 0.5f + 1f;
        if (!isMinimal)
            Draw(canvas, "DAMAGE METER", w * 0.5f, titleMidY, 11f, true, new SKColor(0xD8, 0xC4, 0xC6, 0xCC), Align.Center);
        if (isPinned)
            Draw(canvas, "PINNED", w - 6f, titleMidY, FtSub, true, new SKColor(0xFF, 0xCC, 0x44, 0xFF), Align.Right);

        // Bottom border of title bar
        _p.Color = new SKColor(0x38, 0x28, 0x2A, 0xFF);
        canvas.DrawRect(SKRect.Create(0, TitleBarH - 1f, w, 1f), _p);

        // Encounter header
        float encY = TitleBarH;
        if (isMinimal)
        {
            _p.Color = new SKColor(0x12, 0x0E, 0x0F, 0xFF);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
        }
        else
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, encY), new SKPoint(0, encY + EncounterH),
                new[] { BgHeader1, BgHeader2 },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
            _p.Shader = null;
        }

        // Bottom divider
        _p.Color = new SKColor(0x3A, 0x28, 0x2A, 0xFF);
        canvas.DrawRect(SKRect.Create(0, HeaderH, w, DividerH), _p);

        if (session == null)
        {
            Draw(canvas, "Waiting for combat…", w / 2f, encY + EncounterH * 0.65f, FtMain, false, TextMuted, Align.Center);
            return;
        }

        float cx   = w * 0.5f;
        float midY = encY + EncounterH * 0.65f;

        string statusDot = session.IsActive ? "● " : "■ ";
        SKColor dotCol   = session.IsActive ? TextLive : TextEnded;
        float subY       = encY + EncounterH * 0.30f;
        Draw(canvas, statusDot,        cx - 4f, subY, 9f,    false, dotCol,    Align.Right);
        Draw(canvas, session.ZoneName, cx,      subY, FtZone, false, TextMuted, Align.Left);

        string metricLabel = $"Total {metric.DisplayName()}:";
        string bigVal      = groupTotal > 0 ? FormatVal((long)groupTotal, metric) : "—";
        string timerStr    = $"  ({session.FormattedDuration})";

        using var fontMain = Font(FtMain, false);
        using var fontBig  = Font(FtTimer, true);
        using var fontSub2 = Font(FtMain, false);
        float labelW  = fontMain.MeasureText(metricLabel + " ");
        float valW2   = fontBig.MeasureText(bigVal);
        float timerW  = fontSub2.MeasureText(timerStr);
        float totalLineW = labelW + valW2 + timerW;
        float startX  = cx - totalLineW * 0.5f;

        Draw(canvas, metricLabel + " ",  startX + labelW * 0.5f,                    midY + 6f, FtMain,  false, TextMuted, Align.Center);
        Draw(canvas, bigVal,             startX + labelW + valW2 * 0.5f,            midY + 6f, FtTimer, true,  TextTimer, Align.Center);
        Draw(canvas, timerStr,           startX + labelW + valW2 + timerW * 0.5f,   midY + 6f, FtMain,  false, TextMuted, Align.Center);
    }

    // ── Group header row ──────────────────────────────────────────────────────
    private void DrawGroupHeader(SKCanvas canvas, GroupData group, int w, float y,
                                  double topVal, MeterType metric, double dur, DisplayOptions opts,
                                  bool collapsed, float expandT)
    {
        bool isMinimal = opts.Style == WindowStyle.Minimal;

        if (isMinimal)
        {
            _p.Color = new SKColor(0x14, 0x10, 0x11, 0xFF);
            canvas.DrawRect(SKRect.Create(0, y, w, GroupH), _p);
            // Bottom separator
            _p.Color = new SKColor(0x28, 0x20, 0x22, 0xFF);
            canvas.DrawRect(SKRect.Create(0, y + GroupH - 1f, w, 1f), _p);
        }
        else
        {
            // Group header — dark warm metallic strip with subtle accent bleed
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w * 0.35f, 0),
                new[] { new SKColor(0x28, 0x1C, 0x1E, 0xFF), BgGroup },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, y, w, GroupH), _p);
            _p.Shader = null;
        }

        float midY = y + GroupH * 0.5f;

        // Accordion chevron (▶ collapsed / ▼ expanded) — animated opacity
        string chevron = collapsed ? "▶" : "▼";
        Draw(canvas, chevron, LeftPad, midY, 10f, false,
             TextPrim.WithAlpha((byte)(collapsed ? 0xCC : 0xAA)), Align.Left);

        // Group label + count
        using var gFont  = Font(FtMain, true);
        float labelW2    = gFont.MeasureText(group.Label);
        Draw(canvas, group.Label,                     LeftPad + 14f,             midY, FtMain, true,  TextPrim,  Align.Left);
        Draw(canvas, $"  ({group.Combatants.Count})", LeftPad + 14f + labelW2,   midY, FtSub,  false, TextMuted, Align.Left);

        // Top value
        if (topVal > 0)
            Draw(canvas, FormatVal((long)topVal, metric), w - RightPad, midY, FtSub, false, TextMuted, Align.Right);
    }

    // ── Combatant row (card style) ────────────────────────────────────────────
    private void DrawRow(
        SKCanvas canvas, CombatantData c, int rank, int rowIdx,
        SKColor accent, int w, float y, double val, double pct, MeterType metric,
        uint localEntityId, DisplayOptions opts, float dt, float rowH = RowH)
    {
        bool    isLocal  = localEntityId != 0 && c.EntityId == localEntityId;
        SKColor barColor = EnsureBright(AbgrToSkColor(opts.BarColorAbgr));

        // ── Card bounds ───────────────────────────────────────────────────────
        float cardX = CardMargin;
        float cardY = y + CardMargin;
        float cardW = w - CardMargin * 2;
        // CardH is const 60f

        var cardRect = SKRect.Create(cardX, cardY, cardW, CardH);

        // ── Shake offset ──────────────────────────────────────────────────────
        float shakeX = 0f, shakeY = 0f;
        if (_shakeTimers.TryGetValue(c.EntityId, out float shakeT) && shakeT > 0f)
        {
            float progress  = shakeT / 0.45f; // 1→0 over duration
            float intensity = 4f * progress;
            shakeX = MathF.Sin(_animTime * 53f + rank) * intensity;
            shakeY = MathF.Sin(_animTime * 37f + rank * 1.7f) * intensity * 0.5f;
        }

        // ── Slide-in offset ───────────────────────────────────────────────────
        float slideX = 0f;
        if (_slideProgress.TryGetValue(c.EntityId, out float slideT))
            slideX = (1f - EaseOutCubic(slideT)) * -cardW;

        // ── Clip to row allocation, then apply transforms ─────────────────────
        int rowSave = canvas.Save();
        canvas.ClipRect(SKRect.Create(0, y, w, rowH));

        bool hasTransform = shakeX != 0f || shakeY != 0f || slideX != 0f;
        if (hasTransform)
            canvas.Translate(shakeX + slideX, shakeY);

        if (opts.Style == WindowStyle.Minimal)
        {
            DrawRowMinimal(canvas, c, rank, rowIdx, w, y, rowH, val, pct, metric, opts, barColor);
            canvas.RestoreToCount(rowSave);
            return;
        }

        // ── Modern / Classic style ────────────────────────────────────────────

        // ── 1. Card metallic base ─────────────────────────────────────────────
        _p.Color  = MetalBase;
        _p.Shader = null;
        canvas.DrawRoundRect(cardRect, CardR, CardR, _p);

        // ── 2. Horizontal metallic gradient (copper-warm left → silver-cool right)
        _p.Shader = SKShader.CreateLinearGradient(
            new SKPoint(cardX, 0), new SKPoint(cardX + cardW, 0),
            new[] { MetalWarm, new SKColor(0, 0, 0, 0), MetalCool },
            new[] { 0f, 0.42f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(cardRect, CardR, CardR, _p);
        _p.Shader = null;

        // ── 2b. Top specular highlight strip ──────────────────────────────────
        _p.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, cardY), new SKPoint(0, cardY + CardH * 0.40f),
            new[] { MetalSpec.WithAlpha(0x60), new SKColor(0, 0, 0, 0) },
            SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(cardRect, CardR, CardR, _p);
        _p.Shader = null;

        // ── 2c. Rank tint overlay (gold/silver/bronze/grey) — party only ─────
        if (c.Type == CombatantType.PartyMember)
        {
            SKColor rankTint = rank switch
            {
                1 => Gold.WithAlpha(0x40),
                2 => Silver.WithAlpha(0x30),
                3 => Bronze.WithAlpha(0x38),
                _ => new SKColor(0x88, 0x80, 0x82, 0x18),
            };
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(cardX, 0), new SKPoint(cardX + cardW * 0.55f, 0),
                new[] { rankTint, new SKColor(0, 0, 0, 0) },
                SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(cardRect, CardR, CardR, _p);
            _p.Shader = null;
        }

        // ── 3. Dot matrix background (left 50%, fading to transparent) ────────
        {
            const float dotR    = 0.8f;
            const float dotStep = 5f;
            const byte  dotBase = 0x60;
            float dotFadeEnd = cardX + cardW * 0.5f; // stop at 50% width
            int dotSave = canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(cardRect, CardR), SKClipOperation.Intersect, true);
            for (float dy = cardY + dotStep; dy < cardY + CardH; dy += dotStep)
            {
                for (float dx = cardX + dotStep; dx < dotFadeEnd; dx += dotStep)
                {
                    float t     = (dx - cardX) / (dotFadeEnd - cardX); // 0 at left, 1 at fadeEnd
                    byte  alpha = (byte)(dotBase * (1f - t));
                    _p.Color = new SKColor(0x10, 0x08, 0x09, alpha);
                    canvas.DrawCircle(dx, dy, dotR, _p);
                }
            }
            canvas.RestoreToCount(dotSave);
        }

        // ── 4. Dark border stroke ─────────────────────────────────────────────
        _p.Style       = SKPaintStyle.Stroke;
        _p.StrokeWidth = 1.2f;
        _p.Color       = new SKColor(0x08, 0x05, 0x06, 0xCC);
        canvas.DrawRoundRect(cardRect, CardR, CardR, _p);
        _p.Style = SKPaintStyle.Fill;

        // ── 4. Bar layout ─────────────────────────────────────────────────────
        float barX     = cardX + BarPadX;
        float barMaxW  = cardW - BarPadX * 2;
        float barY     = cardY + CardH - BarPadB - BarH;

        // Fill bar (rounded, proportional — no ghost track)
        if (pct > 0.001)
        {
            float barFillW = barMaxW * (float)pct;
            var   barRect  = SKRect.Create(barX, barY, barFillW, BarH);

            // Top-to-bottom gradient: lighter barColor → darker barColor
            SKColor barLight = new(
                (byte)Math.Min(255, barColor.Red   + (255 - barColor.Red)   * 0.22f),
                (byte)Math.Min(255, barColor.Green + (255 - barColor.Green) * 0.22f),
                (byte)Math.Min(255, barColor.Blue  + (255 - barColor.Blue)  * 0.22f),
                0xFF);
            SKColor barDark = new(
                (byte)(barColor.Red   * 0.45f),
                (byte)(barColor.Green * 0.45f),
                (byte)(barColor.Blue  * 0.45f),
                0xFF);
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, barY), new SKPoint(0, barY + BarH),
                new[] { barLight, barDark },
                SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(barRect, BarR, BarR, _p);
            _p.Shader = null;

            // Lighter bar color outline
            _p.Style       = SKPaintStyle.Stroke;
            _p.StrokeWidth = 0.8f;
            _p.Color       = barLight.WithAlpha(0x70);
            canvas.DrawRoundRect(barRect, BarR, BarR, _p);
            _p.Style = SKPaintStyle.Fill;

            // Breathing Pulse Glow (Panache technique) — lighter barColor → white
            DrawBarPulseGlow(canvas, barRect, BarR, barColor, _animTime);
        }

        // ── 5. Rank number (top-left of card) ────────────────────────────────
        float rankRightX = cardX + LeftPad + RankW;
        float topMidY    = cardY + (CardH - BarPadB - BarH) * 0.5f;
        SKColor rankCol  = rank == 1 ? Gold : rank == 2 ? Silver : rank == 3 ? Bronze : new SKColor(0xC4, 0xB8, 0xBA, 0xFF);
        DrawShadow(canvas, rank.ToString(), rankRightX, topMidY, FtRank, true, align: Align.Right);
        Draw(canvas, rank.ToString(), rankRightX, topMidY, FtRank, true, rankCol, Align.Right);

        // ── 6. Job badge in rounded box ───────────────────────────────────────
        float badgeStartX = rankRightX + 6f;
        if (opts.ShowJobIcon)
        {
            float badgeX = badgeStartX;
            float badgeY = cardY + (CardH - BarPadB - BarH - BadgeH) * 0.5f + 1f;
            badgeY = Math.Max(cardY + 3f, badgeY);
            var badgeRect = SKRect.Create(badgeX, badgeY, BadgeW, BadgeH);

            // Drop shadow
            var shadowRect = SKRect.Create(badgeX + 2f, badgeY + 2.5f, BadgeW, BadgeH);
            using (var shadowPaint = new SKPaint {
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
                Color       = new SKColor(0, 0, 0, 0x88),
                ImageFilter = SKImageFilter.CreateBlur(2.5f, 2.5f),
            })
                canvas.DrawRoundRect(shadowRect, BadgeR, BadgeR, shadowPaint);

            // Rounded box background (role color)
            SKColor roleColor = GetRoleColor(c.ClassJobId);
            _p.Color = new SKColor(roleColor.Red, roleColor.Green, roleColor.Blue, 0x70);
            canvas.DrawRoundRect(badgeRect, BadgeR, BadgeR, _p);

            // Icon or text
            var icon = GetJobIcon(c.ClassJobId);
            if (icon != null)
            {
                _p.Color = c.ClassJobId == 0
                    ? new SKColor(0xFF, 0xCC, 0x55, 0xFF)  // gold tint for generic enemy
                    : SKColors.White;
                float iconShift = (c.ClassJobId == 0 || c.ClassJobId == 41) ? 0f : 2f;
                float iconScale = c.ClassJobId == 0 ? 1.18f : 1f;
                float iconW     = BadgeW * iconScale;
                float iconH     = BadgeH * iconScale;
                var iconRect = SKRect.Create(
                    badgeX  + (BadgeW - iconW) * 0.5f,
                    badgeY  + (BadgeH - iconH) * 0.5f + iconShift,
                    iconW, iconH);
                canvas.DrawImage(icon, iconRect, _p);
            }
            else
            {
                string abbr = Jobs.TryGetValue(c.ClassJobId, out var ji) ? ji.Abbr : "???";
                Draw(canvas, abbr, badgeX + BadgeW * 0.5f, badgeY + BadgeH * 0.5f, 10f, true, SKColors.White, Align.Center);
            }

            badgeStartX = badgeX + BadgeW + 6f;
        }

        // ── 7. Name just above the bar ────────────────────────────────────────
        string displayName = opts.ShowFullName ? c.Name : ToInitials(c.Name);
        if (opts.ShowPlayerServer && !string.IsNullOrEmpty(c.World))
            displayName += "@" + c.World;

        float rightReserve = RightPad + ValW + (opts.ShowPercentage ? PctW + 4f : 0f);
        float nameMaxW = cardX + cardW - rightReserve - badgeStartX - 4f;
        float nameY    = topMidY; // centered vertically in space between card top and bar

        DrawShadow(canvas, displayName, badgeStartX, nameY, FtName, rank == 1 || isLocal, nameMaxW);
        Draw(canvas, displayName, badgeStartX, nameY, FtName,
             rank == 1 || isLocal, isLocal ? LocalAccent : TextPrim, Align.Left, nameMaxW);

        // ── 8. Value (right side, vertically centered in top area) ────────────
        float valRightX  = cardX + cardW - RightPad - (opts.ShowPercentage ? PctW + 4f : 0f);
        Draw(canvas, FormatVal((long)val, metric), valRightX, topMidY, FtValue, true,
             isLocal ? LocalAccent : TextPrim, Align.Right);

        if (opts.ShowPercentage && pct > 0.001)
            Draw(canvas, $"{pct * 100.0:F0}%", cardX + cardW - RightPad, topMidY, FtSub, false, TextMuted, Align.Right);

        // ── 9. Card border (subtle accent rim) ────────────────────────────────
        _p.Style       = SKPaintStyle.Stroke;
        _p.StrokeWidth = 1f;
        _p.Color       = (isLocal ? LocalAccent : accent).WithAlpha(0x35);
        canvas.DrawRoundRect(cardRect, CardR, CardR, _p);
        _p.Style = SKPaintStyle.Fill;

        canvas.RestoreToCount(rowSave);
    }

    // ── Minimal row (single-line condensed) ──────────────────────────────────
    private void DrawRowMinimal(SKCanvas canvas, CombatantData c, int rank, int rowIdx, int w,
        float y, float rowH, double val, double pct, MeterType metric,
        DisplayOptions opts, SKColor barColor)
    {
        const float IconSz  = 18f;  // small job icon size
        const float RankSzM = 11f;  // rank font size
        const float NameSzM = 11f;  // name font size
        const float ValSzM  = 11f;  // value font size
        const float PadL    =  6f;  // left padding
        const float PadR    =  6f;  // right padding

        float midY = y + rowH * 0.5f;

        // Row background — alternating
        _p.Color = rowIdx % 2 == 0
            ? new SKColor(0x1C, 0x16, 0x18, 0xFF)
            : new SKColor(0x18, 0x12, 0x14, 0xFF);
        canvas.DrawRect(SKRect.Create(0, y, w, rowH), _p);

        // Bar as background fill (proportional width, behind text)
        if (pct > 0.001)
        {
            float barFillW = w * (float)pct;
            _p.Color = barColor.WithAlpha(0x55);
            canvas.DrawRect(SKRect.Create(0, y, barFillW, rowH), _p);
        }

        // Bottom separator
        _p.Color = new SKColor(0x28, 0x20, 0x22, 0xFF);
        canvas.DrawRect(SKRect.Create(0, y + rowH - 1f, w, 1f), _p);

        // Rank number
        SKColor rankCol = rank == 1 ? Gold : rank == 2 ? Silver : rank == 3 ? Bronze : TextMuted;
        float curX = PadL;
        using (var rankFont = Font(RankSzM, true))
        {
            float rankW = rankFont.MeasureText(rank.ToString());
            Draw(canvas, rank.ToString(), curX, midY, RankSzM, true, rankCol, Align.Left);
            curX += rankW + 4f;
        }

        // Job icon (small, no badge)
        if (opts.ShowJobIcon)
        {
            var icon = GetJobIcon(c.ClassJobId);
            float iconY = y + (rowH - IconSz) * 0.5f;
            if (icon != null)
            {
                _p.Color = c.ClassJobId == 0 ? new SKColor(0xFF, 0xCC, 0x55, 0xFF) : SKColors.White;
                canvas.DrawImage(icon, SKRect.Create(curX, iconY, IconSz, IconSz), _p);
            }
            else
            {
                string abbr = Jobs.TryGetValue(c.ClassJobId, out var ji) ? ji.Abbr : "?";
                Draw(canvas, abbr, curX + IconSz * 0.5f, midY, 9f, true, TextMuted, Align.Center);
            }
            curX += IconSz + 4f;
        }

        // Right side: value + pct (measure first so name can truncate)
        float rightX = w - PadR;
        if (opts.ShowPercentage && pct > 0.001)
        {
            string pctStr = $"{pct * 100.0:F0}%";
            using var pctFont = Font(ValSzM, false);
            float pctW2 = pctFont.MeasureText(pctStr);
            Draw(canvas, pctStr, rightX, midY, ValSzM, false, TextMuted, Align.Right);
            rightX -= pctW2 + 6f;
        }
        string valStr = FormatVal((long)val, metric);
        using (var valFont = Font(ValSzM, true))
        {
            float valW2 = valFont.MeasureText(valStr);
            Draw(canvas, valStr, rightX, midY, ValSzM, true, TextPrim, Align.Right);
            rightX -= valW2 + 8f;
        }

        // Name (fills remaining space)
        string name = opts.ShowFullName ? c.Name : ToInitials(c.Name);
        if (opts.ShowPlayerServer && !string.IsNullOrEmpty(c.World))
            name += "@" + c.World;
        float nameMaxW = rightX - curX - 2f;
        Draw(canvas, name, curX, midY, NameSzM, rank == 1, TextPrim, Align.Left, nameMaxW);
    }

    // ── Bar Breathing Pulse Glow (Panache DrawPulseGlow technique) ───────────
    private void DrawBarPulseGlow(SKCanvas canvas, SKRect barRect, float r, SKColor barColor, float time)
    {
        // pulse: 0→1 smooth sine — speed halved (π × 0.75), intensity ~0.08
        float pulse = (MathF.Sin(time * MathF.PI * 0.75f) + 1f) * 0.5f;

        // Glow color: lerp from lighter barColor (50% toward white) → near-white
        byte gc_r = (byte)(barColor.Red   + (255 - barColor.Red)   * (0.5f + pulse * 0.5f));
        byte gc_g = (byte)(barColor.Green + (255 - barColor.Green) * (0.5f + pulse * 0.5f));
        byte gc_b = (byte)(barColor.Blue  + (255 - barColor.Blue)  * (0.5f + pulse * 0.5f));

        float blur  = 2f + pulse * 3f;       // 2–5px (tight, subtle)
        byte  alpha = (byte)(12f + pulse * 28f); // 12–40 (very low intensity)

        using var filter = SKImageFilter.CreateBlur(blur, blur);
        using var paint  = new SKPaint
        {
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f + pulse * 1.0f,  // 1–2px stroke
            Color       = new SKColor(gc_r, gc_g, gc_b, alpha),
            ImageFilter = filter,
            BlendMode   = SKBlendMode.Screen,
        };
        canvas.DrawRoundRect(barRect, r, r, paint);
    }

    // ── Volumetric Glow / Bloom (same technique as PanacheUI's DrawBloom) ─────
    private void DrawBloom(SKCanvas canvas, SKRect rect, float r, SKColor glowColor, float intensity)
    {
        for (int pass = 1; pass <= 3; pass++)
        {
            float blurR = pass * 5f;
            using var bloomPaint = new SKPaint
            {
                Color       = glowColor.WithAlpha((byte)(intensity * 60f / pass)),
                ImageFilter = SKImageFilter.CreateBlur(blurR, blurR),
                BlendMode   = SKBlendMode.Screen,
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
            };
            canvas.DrawRoundRect(rect, r, r, bloomPaint);
        }
    }

    // ── Easing ────────────────────────────────────────────────────────────────
    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);

    // ── Name helpers ──────────────────────────────────────────────────────────
    private static string ToInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name;
        return string.Join(".", parts.Select(p => p.Length > 0 ? p[0].ToString().ToUpperInvariant() : "")) + ".";
    }

    // ── Job icon loading ──────────────────────────────────────────────────────
    private SKImage? GetJobIcon(byte jobId)
    {
        if (_jobIconCache.TryGetValue(jobId, out var cached)) return cached;

        // jobId == 0 → use generic enemy icon (92154) as the "no job" fallback
        uint iconId = jobId == 0 ? 92154u : 62000u + jobId;
        SKImage? result = null;
        try
        {
            string folder = $"{iconId / 1000 * 1000:D6}";
            string path   = $"ui/icon/{folder}/{iconId:D6}_hr1.tex";
            var tex       = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(path);
            if (tex == null)
            {
                path = $"ui/icon/{folder}/{iconId:D6}.tex";
                tex  = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(path);
            }
            if (tex != null)
            {
                // Filter converts any source format (BC1/BC3/BC7/etc.) to B8G8R8A8
                var buf    = tex.TextureBuffer.Filter(0, 0, Lumina.Data.Files.TexFile.TextureFormat.B8G8R8A8);
                int bw     = buf.Width;
                int bh     = buf.Height;
                var raw    = buf.RawData;
                int needed = bw * bh * 4;
                if (raw.Length >= needed)
                {
                    var rgba = new byte[needed];
                    Array.Copy(raw, rgba, needed);
                    for (int i = 0; i < needed; i += 4)
                        (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
                    var info   = new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                    try
                    {
                        var tmp = new SKBitmap();
                        tmp.InstallPixels(info, handle.AddrOfPinnedObject(), bw * 4);
                        using var owned = tmp.Copy();
                        result = SKImage.FromBitmap(owned);
                    }
                    finally { handle.Free(); }
                }
            }
        }
        catch { }

        _jobIconCache[jobId] = result;
        return result;
    }

    // ── Text drawing ──────────────────────────────────────────────────────────
    private enum Align { Left, Center, Right }

    private void DrawShadow(SKCanvas canvas, string text, float x, float cy, float sz, bool bold,
                             float maxW = 0f, Align align = Align.Left)
    {
        using var font = Font(sz, bold);
        font.GetFontMetrics(out var m);
        float baselineY = cy - (m.Ascent + m.Descent) * 0.5f;

        if (maxW > 0f)
        {
            float tw = font.MeasureText(text);
            if (tw > maxW)
            {
                float ew     = font.MeasureText("...");
                float budget = maxW - ew;
                if (budget <= 0f) text = "...";
                else
                {
                    int lo = 0, hi = text.Length;
                    while (lo < hi) { int mid = (lo + hi + 1) / 2; if (font.MeasureText(text[..mid]) <= budget) lo = mid; else hi = mid - 1; }
                    text = text[..lo] + "...";
                }
            }
        }

        SKTextAlign skAlign = align switch {
            Align.Center => SKTextAlign.Center,
            Align.Right  => SKTextAlign.Right,
            _            => SKTextAlign.Left,
        };

        using var shadowPaint = new SKPaint {
            IsAntialias = true,
            Style       = SKPaintStyle.Fill,
            Color       = new SKColor(0, 0, 0, 0xA0),
            ImageFilter = SKImageFilter.CreateBlur(1.5f, 1.5f),
        };
        canvas.DrawText(text, x + 1f, baselineY + 1.5f, skAlign, font, shadowPaint);
    }

    private void Draw(SKCanvas canvas, string text, float x, float cy, float sz, bool bold, SKColor col,
                      Align align = Align.Left, float maxW = 0f)
    {
        using var font = Font(sz, bold);
        font.GetFontMetrics(out var m);
        float baselineY = cy - (m.Ascent + m.Descent) * 0.5f;

        if (maxW > 0f)
        {
            float tw = font.MeasureText(text);
            if (tw > maxW)
            {
                float ew     = font.MeasureText("...");
                float budget = maxW - ew;
                if (budget <= 0f) { text = "..."; }
                else
                {
                    int lo = 0, hi = text.Length;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        if (font.MeasureText(text[..mid]) <= budget) lo = mid; else hi = mid - 1;
                    }
                    text = text[..lo] + "...";
                }
            }
        }

        _p.Shader = null;
        _p.Style  = SKPaintStyle.Fill;
        _p.Color  = col;

        SKTextAlign skAlign = align switch
        {
            Align.Center => SKTextAlign.Center,
            Align.Right  => SKTextAlign.Right,
            _            => SKTextAlign.Left,
        };

        canvas.DrawText(text, x, baselineY, skAlign, font, _p);
    }

    private static SKFont Font(float sz, bool bold) =>
        bold
            ? new SKFont(SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), sz)
            : new SKFont(SKTypeface.Default, sz);

    // ── Color helpers ─────────────────────────────────────────────────────────
    private static SKColor GetRoleColor(byte jobId)
        => Jobs.TryGetValue(jobId, out var info) ? info.Role : UnknownCol;

    private static SKColor Darken(SKColor c, float f) =>
        new((byte)(c.Red * f), (byte)(c.Green * f), (byte)(c.Blue * f), c.Alpha);

    private static SKColor AbgrToSkColor(uint abgr) =>
        new((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF), (byte)((abgr >> 24) & 0xFF));

    private static SKColor EnsureBright(SKColor c, byte minMax = 150)
    {
        byte max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
        if (max >= minMax) return c;
        if (max == 0) return new SKColor(minMax, minMax, minMax, c.Alpha);
        float scale = (float)minMax / max;
        return new SKColor(
            (byte)Math.Min(255, c.Red   * scale),
            (byte)Math.Min(255, c.Green * scale),
            (byte)Math.Min(255, c.Blue  * scale),
            c.Alpha);
    }

    private static string FormatVal(long v, MeterType m) =>
        m is MeterType.DPS or MeterType.HPS
            ? $"{MainWindow.FormatNumber(v)}/s"
            : MainWindow.FormatNumber(v);

    // ── Group accent helper ───────────────────────────────────────────────────
    public static SKColor GroupAccent(CombatantType t) => t switch
    {
        CombatantType.PartyMember    => AccentParty,
        CombatantType.FriendlyPlayer => AccentFriendly,
        CombatantType.Enemy          => AccentEnemy,
        _                            => AccentOther,
    };

    // ── Job map ───────────────────────────────────────────────────────────────
    private static readonly Dictionary<byte, (string Abbr, SKColor Role)> Jobs = new()
    {
        [1]  = ("GLA", TankCol),   [2]  = ("PGL", MeleeCol),  [3]  = ("MRD", TankCol),
        [4]  = ("LNC", MeleeCol),  [5]  = ("ARC", RangedCol), [6]  = ("CNJ", HealerCol),
        [7]  = ("THM", CasterCol), [19] = ("PLD", TankCol),   [20] = ("MNK", MeleeCol),
        [21] = ("WAR", TankCol),   [22] = ("DRG", MeleeCol),  [23] = ("BRD", RangedCol),
        [24] = ("WHM", HealerCol), [25] = ("BLM", CasterCol), [26] = ("ACN", CasterCol),
        [27] = ("SMN", CasterCol), [28] = ("SCH", HealerCol), [29] = ("ROG", MeleeCol),
        [30] = ("NIN", MeleeCol),  [31] = ("MCH", RangedCol), [32] = ("DRK", TankCol),
        [33] = ("AST", HealerCol), [34] = ("SAM", MeleeCol),  [35] = ("RDM", CasterCol),
        [36] = ("BLU", CasterCol), [37] = ("GNB", TankCol),   [38] = ("DNC", RangedCol),
        [39] = ("RPR", MeleeCol),  [40] = ("SGE", HealerCol), [41] = ("VPR", MeleeCol),
        [42] = ("PCT", CasterCol),
    };

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        foreach (var img in _jobIconCache.Values) img?.Dispose();
        _jobIconCache.Clear();
        _surface?.Dispose();
        _tex.Dispose();
        _p.Dispose();
    }
}
