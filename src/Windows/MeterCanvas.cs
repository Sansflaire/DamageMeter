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
    private const float EncounterH = 52f;  // encounter info — centered single line
    private const float HeaderH    = TitleBarH + EncounterH; // 80px total header
    private const float DividerH   =  1f;
    private const float GroupH     = 30f;  // per-group title row
    private const float RowH       = 66f;  // per-combatant row (includes card margin)
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
    private const float RankW      = 22f;
    private const float RightPad   = 10f;
    private const float PctW       = 36f;
    private const float ValW       = 72f;

    // ── Font sizes ────────────────────────────────────────────────────────────
    private const float FtHeader = 14f;
    private const float FtZone   = 11f;
    private const float FtTimer  = 22f;
    private const float FtMain   = 13f;
    private const float FtSub    = 11f;
    private const float FtRank   = 13f;
    private const float FtName   = 13f;
    private const float FtValue  = 16f;

    // ── Color palette ─────────────────────────────────────────────────────────
    private static readonly SKColor BgDeep    = new(0x0C, 0x0E, 0x1C, 0xFF);
    private static readonly SKColor BgEven    = new(0x12, 0x16, 0x28, 0xFF);
    private static readonly SKColor BgOdd     = new(0x18, 0x1C, 0x32, 0xFF);
    private static readonly SKColor BgGroup   = new(0x1A, 0x1E, 0x36, 0xFF);
    private static readonly SKColor BgHeader1 = new(0x10, 0x12, 0x28, 0xFF);
    private static readonly SKColor BgHeader2 = new(0x14, 0x18, 0x30, 0xFF);

    private static readonly SKColor TextPrim   = SKColors.White;
    private static readonly SKColor TextMuted  = new(0xA0, 0xA0, 0xC8, 0xFF);
    private static readonly SKColor TextDim    = new(0x70, 0x70, 0x90, 0xFF);
    private static readonly SKColor TextLive   = new(0x30, 0xFF, 0x70, 0xFF);
    private static readonly SKColor TextEnded  = new(0x90, 0x90, 0xB0, 0xFF);
    private static readonly SKColor TextTimer  = new(0xFF, 0xCC, 0x44, 0xFF);
    private static readonly SKColor TextZone   = new(0xCC, 0xCC, 0xFF, 0xFF);

    private static readonly SKColor Gold   = new(0xFF, 0xB8, 0x00, 0xFF);
    private static readonly SKColor Silver = new(0xC4, 0xC4, 0xD8, 0xFF);
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

        float totalH = ComputeHeight(groups, session);
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
                float rowsH    = group.Combatants.Count * RowH;
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

                    DrawRow(canvas, c, i + 1, i, group.Accent, w, rowY, val, pct, metric, localEntityId, opts, dt);
                    _hitRows.Add((rowY, RowH, c));
                    rowY += RowH;
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
    private float ComputeHeight(List<GroupData> groups, CombatSession? session)
    {
        float h = HeaderH + DividerH;
        bool first = true;
        foreach (var g in groups)
        {
            if (g.Combatants.Count == 0) continue;
            if (!first) h += GroupGap;
            first = false;
            float expandT = _groupExpandT.TryGetValue(g.Label, out float et) ? et : 1f;
            h += GroupH + g.Combatants.Count * RowH * expandT;
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

        if (isMinimal)
        {
            _p.Color = new SKColor(0x08, 0x08, 0x0E, 0xFF);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
        }
        else if (isModern)
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, TitleBarH),
                new[] { new SKColor(0x04, 0x14, 0x20, 0xFF), new SKColor(0x08, 0x18, 0x28, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
            _p.Shader = null;
        }
        else
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, TitleBarH),
                new[] { new SKColor(0x10, 0x08, 0x22, 0xFF), new SKColor(0x08, 0x0C, 0x28, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
            _p.Shader = null;
        }

        if (!isMinimal)
        {
            SKColor stripeA = isModern ? new SKColor(0x00, 0xCC, 0xCC, 0xFF) : new SKColor(0x88, 0x44, 0xDD, 0xFF);
            SKColor stripeB = isModern ? new SKColor(0x44, 0xFF, 0xEE, 0xFF) : new SKColor(0x44, 0x88, 0xFF, 0xFF);
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, 0),
                new[] { stripeA, stripeB }, SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, 2f), _p);
            _p.Shader = null;
        }

        float titleMidY = TitleBarH * 0.5f + 1f;
        if (!isMinimal)
        {
            SKColor labelCol = isModern ? new SKColor(0x44, 0xFF, 0xEE, 0xCC) : new SKColor(0xCC, 0xAA, 0xFF, 0xFF);
            Draw(canvas, "DAMAGE METER", w * 0.5f, titleMidY, 11f, true, labelCol, Align.Center);
        }
        if (isPinned)
            Draw(canvas, "PINNED", w - 6f, titleMidY, FtSub, true, new SKColor(0xFF, 0xCC, 0x44, 0xFF), Align.Right);

        _p.Color = isModern ? new SKColor(0x10, 0x30, 0x30, 0xFF) : new SKColor(0x30, 0x20, 0x50, 0xFF);
        canvas.DrawRect(SKRect.Create(0, TitleBarH - 1f, w, 1f), _p);

        float encY = TitleBarH;
        if (isMinimal)
        {
            _p.Color = new SKColor(0x06, 0x06, 0x0C, 0xFF);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
        }
        else if (isModern)
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, encY), new SKPoint(0, encY + EncounterH),
                new[] { new SKColor(0x06, 0x10, 0x18, 0xFF), new SKColor(0x0A, 0x16, 0x22, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
            _p.Shader = null;
        }
        else
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, encY), new SKPoint(0, encY + EncounterH),
                new[] { BgHeader1, BgHeader2 }, SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
            _p.Shader = null;
        }

        _p.Color = isModern ? new SKColor(0x10, 0x28, 0x28, 0xFF) : new SKColor(0x28, 0x28, 0x48, 0xFF);
        canvas.DrawRect(SKRect.Create(0, HeaderH, w, DividerH), _p);

        if (session == null)
        {
            Draw(canvas, "Waiting for combat…", w / 2f, encY + EncounterH * 0.5f, FtMain, false, TextMuted, Align.Center);
            return;
        }

        float cx   = w * 0.5f;
        float midY = encY + EncounterH * 0.5f;

        string statusDot = session.IsActive ? "● " : "■ ";
        SKColor dotCol   = session.IsActive ? TextLive : TextEnded;
        float subY       = encY + 14f;
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
        if (opts.Style == WindowStyle.Minimal)
        {
            _p.Color = new SKColor(0x08, 0x08, 0x10, 0xFF);
            canvas.DrawRect(SKRect.Create(0, y, w, GroupH), _p);
        }
        else
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, y), new SKPoint(w * 0.4f, y),
                new[] { Darken(group.Accent, 0.25f), BgGroup }, SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, y, w, GroupH), _p);
            _p.Shader = null;
        }

        // Left accent stripe
        _p.Color = group.Accent.WithAlpha(0xE0);
        canvas.DrawRect(SKRect.Create(0, y, StripeW, GroupH), _p);

        float midY = y + GroupH * 0.5f;

        // Accordion chevron (▶ collapsed / ▼ expanded) — animated opacity
        string chevron = collapsed ? "▶" : "▼";
        Draw(canvas, chevron, StripeW + 7f, midY, 10f, false,
             group.Accent.WithAlpha((byte)(collapsed ? 0xCC : 0xAA)), Align.Left);

        // Group label + count
        using var gFont  = Font(FtMain, true);
        float labelW2    = gFont.MeasureText(group.Label);
        Draw(canvas, group.Label,                     StripeW + 20f,             midY, FtMain, true,  group.Accent,  Align.Left);
        Draw(canvas, $"  ({group.Combatants.Count})", StripeW + 20f + labelW2,   midY, FtSub,  false, TextMuted, Align.Left);

        // Top value
        if (topVal > 0)
            Draw(canvas, FormatVal((long)topVal, metric), w - RightPad, midY, FtSub, false, TextMuted, Align.Right);
    }

    // ── Combatant row (card style) ────────────────────────────────────────────
    private void DrawRow(
        SKCanvas canvas, CombatantData c, int rank, int rowIdx,
        SKColor accent, int w, float y, double val, double pct, MeterType metric,
        uint localEntityId, DisplayOptions opts, float dt)
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
        canvas.ClipRect(SKRect.Create(0, y, w, RowH));

        bool hasTransform = shakeX != 0f || shakeY != 0f || slideX != 0f;
        if (hasTransform)
            canvas.Translate(shakeX + slideX, shakeY);

        // ── 1. Card base background ───────────────────────────────────────────
        SKColor bgBase = isLocal
            ? new SKColor(0x0A, 0x14, 0x24, 0xFF)
            : rowIdx % 2 == 0 ? BgEven : BgOdd;
        _p.Color  = bgBase;
        _p.Shader = null;
        canvas.DrawRoundRect(cardRect, CardR, CardR, _p);

        // ── 2. Accent gradient overlay ────────────────────────────────────────
        SKColor gradAccent = isLocal ? LocalAccent : accent;
        _p.Shader = SKShader.CreateLinearGradient(
            new SKPoint(cardX, 0), new SKPoint(cardX + cardW * 0.55f, 0),
            new SKColor[] { gradAccent.WithAlpha(isLocal ? (byte)0x40 : (byte)0x28),
                            new SKColor(0, 0, 0, 0) },
            SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(cardRect, CardR, CardR, _p);
        _p.Shader = null;

        // ── 3. Volumetric Glow (Bloom — Panache technique) ────────────────────
        DrawBloom(canvas, cardRect, CardR, barColor, 0.28f);

        // ── 4. Bar layout ─────────────────────────────────────────────────────
        float barX     = cardX + BarPadX;
        float barMaxW  = cardW - BarPadX * 2;
        float barY     = cardY + CardH - BarPadB - BarH;

        // Ghost track (full width)
        _p.Color = new SKColor(barColor.Red, barColor.Green, barColor.Blue, 0x40);
        canvas.DrawRoundRect(SKRect.Create(barX, barY, barMaxW, BarH), BarR, BarR, _p);

        // Fill bar (rounded, proportional)
        if (pct > 0.001)
        {
            float barFillW = barMaxW * (float)pct;
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(barX, barY), new SKPoint(barX + barFillW, barY),
                new[] { barColor.WithAlpha(0xBB), barColor.WithAlpha(0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(SKRect.Create(barX, barY, barFillW, BarH), BarR, BarR, _p);
            _p.Shader = null;
        }

        // ── 5. Rank number (top-left of card) ────────────────────────────────
        float rankRightX = cardX + LeftPad + RankW;
        float topMidY    = cardY + (CardH - BarPadB - BarH) * 0.5f;
        SKColor rankCol  = rank == 1 ? Gold : rank == 2 ? Silver : rank == 3 ? Bronze : TextDim;
        Draw(canvas, rank.ToString(), rankRightX, topMidY, FtRank, true, rankCol, Align.Right);

        // ── 6. Job badge in rounded box ───────────────────────────────────────
        float badgeStartX = rankRightX + 6f;
        if (opts.ShowJobIcon)
        {
            float badgeX = badgeStartX;
            float badgeY = cardY + (CardH - BarPadB - BarH - BadgeH) * 0.5f + 1f;
            badgeY = Math.Max(cardY + 3f, badgeY);
            var badgeRect = SKRect.Create(badgeX, badgeY, BadgeW, BadgeH);

            // Rounded box background (role color)
            SKColor roleColor = GetRoleColor(c.ClassJobId);
            _p.Color = new SKColor(roleColor.Red, roleColor.Green, roleColor.Blue, 0x70);
            canvas.DrawRoundRect(badgeRect, BadgeR, BadgeR, _p);

            // Icon or text
            var icon = GetJobIcon(c.ClassJobId);
            if (icon != null)
            {
                _p.Color = SKColors.White;
                canvas.DrawImage(icon, badgeRect, _p);
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
        float nameY    = barY - 5f; // just above bar — nameY is the "cy" (vertical center)

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
        if (jobId == 0) return null;
        if (_jobIconCache.TryGetValue(jobId, out var cached)) return cached;

        uint iconId = 62000u + jobId;
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
                var buf = tex.TextureBuffer;
                int bw  = buf.Width;
                int bh  = buf.Height;
                if (tex.Header.Format == Lumina.Data.Files.TexFile.TextureFormat.B8G8R8A8)
                {
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
        }
        catch { }

        _jobIconCache[jobId] = result;
        return result;
    }

    // ── Text drawing ──────────────────────────────────────────────────────────
    private enum Align { Left, Center, Right }

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
