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
    public  const float TitleBarH  = 22f;  // exposed: "DAMAGE METER" title strip
    private const float EncounterH = 58f;  // encounter info (zone, timer, total value)
    private const float HeaderH    = TitleBarH + EncounterH; // 80px total header
    private const float DividerH   =  2f;  // separator line below header
    private const float GroupH     = 22f;  // per-group title row
    private const float RowH       = 34f;  // per-combatant row (more breathing room)
    private const float GroupGap   =  8f;  // vertical gap between groups

    // ── Row layout (left → right) ─────────────────────────────────────────────
    private const float StripeW  =  3f;  // left accent stripe
    private const float LeftPad  =  5f;
    private const float RankW    = 20f;  // rank number column
    private const float BadgeW   = 22f;  // job icon badge — square 1:1
    private const float BadgeH   = 22f;
    // Right columns (right edge of window)
    private const float RightPad =  6f;
    private const float PctW     = 36f;
    private const float ValW     = 60f;

    // X where name text starts
    private const float NameX    = StripeW + LeftPad + RankW + LeftPad + BadgeW + LeftPad;
    // How much right-side content reserves from the right edge
    private const float RightReserve = RightPad + PctW + 4f + ValW;

    // ── Font sizes ────────────────────────────────────────────────────────────
    private const float FtHeader = 14f;
    private const float FtZone   = 13f;
    private const float FtTimer  = 18f;
    private const float FtMain   = 13f;
    private const float FtSub    = 11f;
    private const float FtBadge  =  9f;
    private const float FtRank   = 10f;

    // ── Color palette ─────────────────────────────────────────────────────────
    // Backgrounds
    private static readonly SKColor BgDeep    = new(0x08, 0x08, 0x12, 0xFF);
    private static readonly SKColor BgEven    = new(0x0B, 0x0B, 0x18, 0xFF);
    private static readonly SKColor BgOdd     = new(0x0F, 0x0F, 0x1E, 0xFF);
    private static readonly SKColor BgGroup   = new(0x12, 0x12, 0x28, 0xFF);
    private static readonly SKColor BgHeader1 = new(0x0C, 0x0C, 0x22, 0xFF);
    private static readonly SKColor BgHeader2 = new(0x10, 0x10, 0x2A, 0xFF);

    // Text
    private static readonly SKColor TextPrim   = SKColors.White;
    private static readonly SKColor TextMuted  = new(0x80, 0x80, 0xAA, 0xFF);
    private static readonly SKColor TextDim    = new(0x50, 0x50, 0x70, 0xFF);
    private static readonly SKColor TextLive   = new(0x30, 0xFF, 0x70, 0xFF);
    private static readonly SKColor TextEnded  = new(0x70, 0x70, 0x90, 0xFF);
    private static readonly SKColor TextTimer  = new(0xFF, 0xCC, 0x44, 0xFF);
    private static readonly SKColor TextZone   = new(0xCC, 0xCC, 0xFF, 0xFF);

    // Rank colors
    private static readonly SKColor Gold   = new(0xFF, 0xB8, 0x00, 0xFF);
    private static readonly SKColor Silver = new(0xC4, 0xC4, 0xD8, 0xFF);
    private static readonly SKColor Bronze = new(0xC8, 0x78, 0x28, 0xFF);

    // Group accents
    private static readonly SKColor AccentParty    = new(0x44, 0x8C, 0xFF, 0xFF);
    private static readonly SKColor AccentFriendly = new(0x44, 0xCC, 0x88, 0xFF);
    private static readonly SKColor AccentEnemy    = new(0xFF, 0x44, 0x44, 0xFF);
    private static readonly SKColor AccentOther    = new(0x88, 0x88, 0xCC, 0xFF);

    // Job role colors
    private static readonly SKColor TankCol    = new(0x3B, 0x84, 0xFF, 0xFF);
    private static readonly SKColor HealerCol  = new(0x28, 0xCC, 0x58, 0xFF);
    private static readonly SKColor MeleeCol   = new(0xEE, 0x44, 0x44, 0xFF);
    private static readonly SKColor RangedCol  = new(0xEE, 0xA0, 0x20, 0xFF);
    private static readonly SKColor CasterCol  = new(0xCC, 0x44, 0xEE, 0xFF);
    private static readonly SKColor UnknownCol = new(0x44, 0x55, 0x66, 0xFF);

    // Local player accent
    private static readonly SKColor LocalAccent = new(0x44, 0xEE, 0xFF, 0xFF); // cyan

    // ── Display options (passed from MainWindow each frame) ───────────────────
    public struct DisplayOptions
    {
        public bool ShowFullName;    // false = initials only
        public bool ShowPlayerServer;
        public bool ShowJobIcon;
        public bool ShowPercentage;
        public uint BarColorAbgr;    // per-metric bar fill color (ABGR uint from config)
        public WindowStyle Style;
    }

    // ── Rendering infrastructure ──────────────────────────────────────────────
    private RenderSurface?  _surface;
    private readonly TextureManager _tex;
    private readonly SKPaint _p = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    // ── Job icon cache (FFXIV game icons loaded via Lumina) ───────────────────
    private readonly Dictionary<byte, SKImage?> _jobIconCache = new();

    // Hit-test table: (yStart, height, data)
    private readonly List<(float Y, float H, CombatantData? Data)> _hitRows = new();

    public float        TotalHeight { get; private set; }
    public ImTextureID? Handle      => _tex.Handle;

    // ── Group input ───────────────────────────────────────────────────────────
    public struct GroupData
    {
        public string              Label;
        public List<CombatantData> Combatants;
        public SKColor             Accent;
    }

    public MeterCanvas(ITextureProvider tp) => _tex = new TextureManager(tp);

    // ── Main render entry ─────────────────────────────────────────────────────
    public void Render(int width, CombatSession? session, List<GroupData> groups, MeterType metric, double dur, bool isPinned = false, uint localEntityId = 0, DisplayOptions opts = default)
    {
        _hitRows.Clear();

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

        // Compute total across all displayed combatants (party DPS / total damage / etc.)
        double groupTotal = 0;
        foreach (var g in groups)
            foreach (var c in g.Combatants)
                groupTotal += c.GetValue(metric, dur);

        // — Encounter header ——
        DrawEncounterHeader(canvas, session, w, metric, dur, isPinned, groupTotal, opts);
        float y = HeaderH + DividerH;

        // — Group sections ——
        bool firstGroup = true;
        foreach (var group in groups)
        {
            if (group.Combatants.Count == 0) continue;
            if (!firstGroup) y += GroupGap;
            firstGroup = false;

            // Group leader value for % bars
            double topVal = 0;
            foreach (var c in group.Combatants)
            {
                double v = c.GetValue(metric, dur);
                if (v > topVal) topVal = v;
            }

            DrawGroupHeader(canvas, group, w, y, topVal, metric, dur, opts);
            _hitRows.Add((y, GroupH, null));
            y += GroupH;

            for (int i = 0; i < group.Combatants.Count; i++)
            {
                var c   = group.Combatants[i];
                var val = (double)c.GetValue(metric, dur);
                var pct = topVal > 0 ? val / topVal : 0.0;

                DrawRow(canvas, c, i + 1, i, group.Accent, w, y, val, pct, metric, localEntityId, opts);
                _hitRows.Add((y, RowH, c));
                y += RowH;
            }
        }

        // — No data ——
        if (firstGroup) // no groups were drawn
        {
            float msgY = HeaderH + DividerH + 24f;
            Draw(canvas, "No encounter data yet.", w / 2f, msgY, FtSub, false, TextMuted, Align.Center);
        }

        _tex.Upload(_surface);
    }

    // ── PNG export for StatusApi ──────────────────────────────────────────────
    public byte[]? GetPngBytes() => _surface?.GetPngBytes();

    // ── Hit test ──────────────────────────────────────────────────────────────
    public CombatantData? HitTest(float imageY)
    {
        foreach (var (ry, rh, data) in _hitRows)
            if (imageY >= ry && imageY < ry + rh) return data;
        return null;
    }

    // ── Height ────────────────────────────────────────────────────────────────
    private static float ComputeHeight(List<GroupData> groups, CombatSession? session)
    {
        float h = HeaderH + DividerH;
        bool first = true;
        foreach (var g in groups)
        {
            if (g.Combatants.Count == 0) continue;
            if (!first) h += GroupGap;
            first = false;
            h += GroupH + g.Combatants.Count * RowH;
        }
        if (first && session != null) h += 30f; // height for "no data" message
        return h;
    }

    // ── Encounter header ──────────────────────────────────────────────────────
    private void DrawEncounterHeader(SKCanvas canvas, CombatSession? session, int w, MeterType metric, double dur, bool isPinned, double groupTotal, DisplayOptions opts)
    {
        bool isMinimal = opts.Style == WindowStyle.Minimal;
        bool isModern  = opts.Style == WindowStyle.Modern;

        // ── Title strip ───────────────────────────────────────────────────────
        if (isMinimal)
        {
            // Minimal: plain near-black, no gradient
            _p.Color = new SKColor(0x08, 0x08, 0x0E, 0xFF);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
        }
        else if (isModern)
        {
            // Modern: dark teal gradient
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, TitleBarH),
                new[] { new SKColor(0x04, 0x14, 0x20, 0xFF), new SKColor(0x08, 0x18, 0x28, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
            _p.Shader = null;
        }
        else
        {
            // Classic: purple-blue gradient
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, TitleBarH),
                new[] { new SKColor(0x10, 0x08, 0x22, 0xFF), new SKColor(0x08, 0x0C, 0x28, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, TitleBarH), _p);
            _p.Shader = null;
        }

        // Top accent stripe (2px) — hidden in Minimal
        if (!isMinimal)
        {
            SKColor stripeA = isModern ? new SKColor(0x00, 0xCC, 0xCC, 0xFF) : new SKColor(0x88, 0x44, 0xDD, 0xFF);
            SKColor stripeB = isModern ? new SKColor(0x44, 0xFF, 0xEE, 0xFF) : new SKColor(0x44, 0x88, 0xFF, 0xFF);
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, 0),
                new[] { stripeA, stripeB },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, 2f), _p);
            _p.Shader = null;
        }

        // "DAMAGE METER" label — hidden in Minimal
        float titleMidY = TitleBarH * 0.5f + 1f;
        if (!isMinimal)
        {
            SKColor labelCol = isModern ? new SKColor(0x44, 0xFF, 0xEE, 0xCC) : new SKColor(0xCC, 0xAA, 0xFF, 0xFF);
            Draw(canvas, "DAMAGE METER", w * 0.5f, titleMidY, 11f, true, labelCol, Align.Center);
        }

        if (isPinned)
            Draw(canvas, "PINNED", w - 6f, titleMidY, FtSub, true,
                 new SKColor(0xFF, 0xCC, 0x44, 0xFF), Align.Right);

        // Separator
        _p.Color = isModern ? new SKColor(0x10, 0x30, 0x30, 0xFF) : new SKColor(0x30, 0x20, 0x50, 0xFF);
        canvas.DrawRect(SKRect.Create(0, TitleBarH - 1f, w, 1f), _p);

        // ── Encounter strip ───────────────────────────────────────────────────
        float encY = TitleBarH;

        if (isMinimal)
        {
            // Minimal: very dark solid
            _p.Color = new SKColor(0x06, 0x06, 0x0C, 0xFF);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
        }
        else if (isModern)
        {
            // Modern: dark navy gradient
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, encY), new SKPoint(0, encY + EncounterH),
                new[] { new SKColor(0x06, 0x10, 0x18, 0xFF), new SKColor(0x0A, 0x16, 0x22, 0xFF) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
            _p.Shader = null;
        }
        else
        {
            // Classic
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, encY), new SKPoint(0, encY + EncounterH),
                new[] { BgHeader1, BgHeader2 },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, encY, w, EncounterH), _p);
            _p.Shader = null;
        }

        // Divider at bottom of header
        _p.Color = isModern ? new SKColor(0x10, 0x28, 0x28, 0xFF) : new SKColor(0x28, 0x28, 0x48, 0xFF);
        canvas.DrawRect(SKRect.Create(0, HeaderH, w, DividerH), _p);

        if (session == null)
        {
            Draw(canvas, "Waiting for combat…", w / 2f, encY + EncounterH * 0.5f,
                 FtMain, false, TextMuted, Align.Center);
            return;
        }

        float padX  = 10f;
        float line1 = encY + 16f;  // status / zone row
        float line2 = encY + 44f;  // metric label / big value row

        // Line 1 left: status indicator + zone name
        if (session.IsActive)
        {
            Draw(canvas, "●", padX, line1, 9f, false, TextLive, Align.Left);
            Draw(canvas, " LIVE", padX + 11f, line1, FtSub, true, TextLive, Align.Left);
            Draw(canvas, session.ZoneName, padX + 42f, line1, FtZone, false, TextZone, Align.Left);
        }
        else
        {
            Draw(canvas, "■", padX, line1, 8f, false, TextEnded, Align.Left);
            Draw(canvas, session.ZoneName, padX + 14f, line1, FtZone, false, TextZone, Align.Left);
        }

        // Line 1 right: timer (small, muted-gold)
        Draw(canvas, session.FormattedDuration, w - padX, line1, FtMain, false, TextTimer, Align.Right);

        // Line 2 left: metric label
        Draw(canvas, metric.DisplayName().ToUpperInvariant(), padX, line2, FtSub, false, TextMuted, Align.Left);

        // Line 2 right: BIG total value — focal point of the header
        string bigVal = groupTotal > 0 ? FormatVal((long)groupTotal, metric) : "—";
        Draw(canvas, bigVal, w - padX, line2, 21f, true, TextTimer, Align.Right);
    }

    // ── Group header row ──────────────────────────────────────────────────────
    private void DrawGroupHeader(SKCanvas canvas, GroupData group, int w, float y, double topVal, MeterType metric, double dur, DisplayOptions opts)
    {
        // Background — Minimal uses plain dark, others use gradient
        if (opts.Style == WindowStyle.Minimal)
        {
            _p.Color = new SKColor(0x08, 0x08, 0x10, 0xFF);
            canvas.DrawRect(SKRect.Create(0, y, w, GroupH), _p);
        }
        else
        {
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, y), new SKPoint(w * 0.4f, y),
                new[] { Darken(group.Accent, 0.25f), BgGroup },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, y, w, GroupH), _p);
            _p.Shader = null;
        }

        // Left accent stripe
        _p.Color = group.Accent.WithAlpha(0xCC);
        canvas.DrawRect(SKRect.Create(0, y, StripeW, GroupH), _p);

        float midY = y + GroupH * 0.5f;

        // Group label
        string label = $"{group.Label}  ({group.Combatants.Count})";
        Draw(canvas, label, StripeW + 8f, midY, FtSub, true, group.Accent, Align.Left);

        // Top value right-aligned
        if (topVal > 0)
        {
            string topStr = FormatVal((long)topVal, metric);
            Draw(canvas, topStr, w - RightPad, midY, FtSub, false, TextMuted, Align.Right);
        }
    }

    // ── Combatant row ─────────────────────────────────────────────────────────
    private void DrawRow(
        SKCanvas canvas, CombatantData c, int rank, int rowIdx,
        SKColor accent, int w, float y, double val, double pct, MeterType metric,
        uint localEntityId, DisplayOptions opts)
    {
        bool    isLocal  = localEntityId != 0 && c.EntityId == localEntityId;
        SKColor rankCol  = isLocal ? LocalAccent : RankColor(rank, accent);
        SKColor barColor = opts.BarColorAbgr != 0 ? AbgrToSkColor(opts.BarColorAbgr) : rankCol;
        float   midY     = y + RowH * 0.5f;

        // 1. Alternating background (slightly lighter for local player)
        SKColor rowBg;
        if (isLocal)
            rowBg = new SKColor(0x10, 0x18, 0x22, 0xFF);
        else if (opts.Style == WindowStyle.Minimal)
            rowBg = rowIdx % 2 == 0 ? new SKColor(0x08, 0x08, 0x0E, 0xCC) : new SKColor(0x05, 0x05, 0x0A, 0xCC);
        else if (opts.Style == WindowStyle.Modern)
            rowBg = rowIdx % 2 == 0 ? new SKColor(0x08, 0x0C, 0x10, 0xFF) : new SKColor(0x0C, 0x10, 0x16, 0xFF);
        else
            rowBg = rowIdx % 2 == 0 ? BgEven : BgOdd;
        _p.Color  = rowBg;
        _p.Shader = null;
        canvas.DrawRect(SKRect.Create(0, y, w, RowH), _p);

        // 2. Subtle full-width color track (shows bar scale)
        _p.Color = new SKColor(barColor.Red, barColor.Green, barColor.Blue, 0x18);
        canvas.DrawRect(SKRect.Create(StripeW, y, w - StripeW, RowH), _p);

        // 3. Gradient fill bar (proportional to %)
        if (pct > 0.001)
        {
            float barW = (w - StripeW) * (float)pct;
            var   dark = barColor.WithAlpha(0xB0);
            var   lite = barColor.WithAlpha(0xFF);
            _p.Shader = SKShader.CreateLinearGradient(
                new SKPoint(StripeW, y), new SKPoint(StripeW + barW, y),
                new[] { dark, lite },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(StripeW, y, barW, RowH), _p);
            _p.Shader = null;
        }

        // 4. Left stripe
        _p.Color = rankCol;
        canvas.DrawRect(SKRect.Create(0, y, StripeW, RowH), _p);

        // 5. Rank number
        float rankRightX = StripeW + LeftPad + RankW;
        Draw(canvas, rank.ToString(), rankRightX, midY, FtRank, true,
             rank <= 3 ? rankCol : TextDim, Align.Right);

        // 6. Job badge (skip column if ShowJobIcon = false)
        float nameStartX = NameX;
        if (opts.ShowJobIcon)
        {
            float badgeX = rankRightX + LeftPad;
            float badgeY = y + (RowH - BadgeH) * 0.5f;
            DrawJobBadge(canvas, c.ClassJobId, c.Name, badgeX, badgeY);
        }
        else
        {
            // Reclaim badge + padding space for name
            nameStartX = rankRightX + LeftPad;
        }

        // 7. Name — apply full/initials and @server settings
        string displayName = opts.ShowFullName ? c.Name : ToInitials(c.Name);
        if (opts.ShowPlayerServer && !string.IsNullOrEmpty(c.World))
            displayName += "@" + c.World;

        float nameMaxW = w - nameStartX - RightReserve - 4f;
        Draw(canvas, displayName, nameStartX, midY, 14f, isLocal,
             isLocal ? SKColors.White : TextPrim, Align.Left, nameMaxW);

        // 8. Value — dominant number (15px bold)
        float valRightX = w - RightPad - (opts.ShowPercentage ? PctW + 4f : 0f);
        Draw(canvas, FormatVal((long)val, metric), valRightX, midY, 15f, true,
             isLocal ? LocalAccent : TextPrim, Align.Right);

        // 9. Pct — muted, far right, small
        if (opts.ShowPercentage && pct > 0.001)
            Draw(canvas, $"{pct * 100.0:F0}%", w - RightPad, midY, FtSub, false, TextMuted, Align.Right);
    }

    // ── Name helpers ──────────────────────────────────────────────────────────
    private static string ToInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name;
        return string.Join(".", parts.Select(p => p.Length > 0 ? p[0].ToString().ToUpperInvariant() : "")) + ".";
    }

    // ── Job badge ─────────────────────────────────────────────────────────────
    private void DrawJobBadge(SKCanvas canvas, byte jobId, string entityName, float x, float y)
    {
        // Try game icon first (loaded from FFXIV data via Lumina)
        var icon = GetJobIcon(jobId);
        if (icon != null)
        {
            _p.Shader = null;
            _p.Color  = SKColors.White;
            canvas.DrawImage(icon, SKRect.Create(x, y, BadgeW, BadgeH), _p);
            return;
        }

        // Text fallback
        SKColor roleCol;
        string  abbr;
        if (Jobs.TryGetValue(jobId, out var info))
        {
            abbr    = info.Abbr;
            roleCol = info.Role;
        }
        else
        {
            abbr    = entityName.Length >= 3 ? entityName[..3].ToUpperInvariant() : entityName.ToUpperInvariant();
            roleCol = UnknownCol;
        }

        _p.Shader = null;
        _p.Color  = new SKColor(roleCol.Red, roleCol.Green, roleCol.Blue, 0xA0);
        canvas.DrawRoundRect(SKRect.Create(x, y, BadgeW, BadgeH), 3, 3, _p);
        Draw(canvas, abbr, x + BadgeW * 0.5f, y + BadgeH * 0.5f, FtBadge, true, SKColors.White, Align.Center);
    }

    // ── Job icon loading (FFXIV ClassJob icons: ID 62001–62042) ───────────────
    private SKImage? GetJobIcon(byte jobId)
    {
        if (_jobIconCache.TryGetValue(jobId, out var cached)) return cached;

        // Icon IDs: ui/icon/062000/0620XX_hr1.tex where XX = classJobId
        uint    iconId = 62000u + jobId;
        SKImage? result = null;
        try
        {
            string folder = $"{iconId / 1000 * 1000:D6}";
            string path   = $"ui/icon/{folder}/{iconId:D6}_hr1.tex";

            var tex = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(path);
            if (tex == null)
            {
                path = $"ui/icon/{folder}/{iconId:D6}.tex";
                tex  = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(path);
            }

            if (tex != null)
            {
                var buf = tex.TextureBuffer;
                int w   = buf.Width;
                int h   = buf.Height;
                var fmt = tex.Header.Format;

                // Handle uncompressed BGRA (B8G8R8A8) — most UI job icons
                if (fmt == Lumina.Data.Files.TexFile.TextureFormat.B8G8R8A8)
                {
                    var raw    = buf.RawData;
                    int needed = w * h * 4;
                    if (raw.Length >= needed)
                    {
                        // Copy raw BGRA bytes and swap B↔R → RGBA
                        var rgba = new byte[needed];
                        Array.Copy(raw, rgba, needed);
                        for (int i = 0; i < needed; i += 4)
                            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);

                        var info   = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                        try
                        {
                            var tmp = new SKBitmap();
                            tmp.InstallPixels(info, handle.AddrOfPinnedObject(), w * 4);
                            using var owned = tmp.Copy(); // independent copy
                            result = SKImage.FromBitmap(owned);
                        }
                        finally { handle.Free(); }
                    }
                }
            }
        }
        catch { /* best-effort — falls back to text badge */ }

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

        // Ellipsis clip
        if (maxW > 0f)
        {
            float tw = font.MeasureText(text);
            if (tw > maxW)
            {
                float ew = font.MeasureText("...");
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
    private static SKColor RankColor(int rank, SKColor accent) =>
        rank switch { 1 => Gold, 2 => Silver, 3 => Bronze, _ => accent };

    private static SKColor Darken(SKColor c, float f) =>
        new((byte)(c.Red * f), (byte)(c.Green * f), (byte)(c.Blue * f), c.Alpha);

    // ABGR uint (Dalamud/ImGui format) → SKColor (RGBA)
    private static SKColor AbgrToSkColor(uint abgr) =>
        new((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF), (byte)((abgr >> 24) & 0xFF));

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
