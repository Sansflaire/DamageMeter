using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DamageMeter.Windows;

/// <summary>
/// Main meter window.
/// Combatants are separated into Party / Friendly / Enemy groups.
/// Right-click any row for a full ability-by-ability breakdown popup.
/// </summary>
public sealed class MainWindow : IDisposable
{
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly Plugin _plugin;
    private Configuration Config  => _plugin.Config;
    private CombatTracker Tracker => _plugin.Tracker;

    private readonly Dictionary<byte, ISharedImmediateTexture?> _iconCache      = new();
    private readonly Dictionary<string, bool>                   _groupCollapsed = new();

    // Right-click detail state
    private uint           _detailEntityId;
    private CombatSession? _detailSession;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow(Plugin plugin) => _plugin = plugin;

    // ── Draw ──────────────────────────────────────────────────────────────────
    public void Draw()
    {
        if (!IsVisible) return;

        ApplyWindowStyle();

        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (Config.LockWindow)
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        ImGui.SetNextWindowSizeConstraints(new Vector2(280, 120), new Vector2(900, 2000));
        ImGui.SetNextWindowSize(new Vector2(400, 300), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Config.Opacity);

        if (!ImGui.Begin("DAMAGE METER###DamageMeterMain", ref _isVisible, flags))
        {
            ImGui.End();
            return;
        }

        DrawToolbar();
        DrawEncounterTimer();
        ImGui.Separator();
        DrawMeterBars();
        DrawDetailPopup();

        ImGui.End();
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────
    private void DrawToolbar()
    {
        var label = Config.CurrentMeter.DisplayName();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 112);
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
        if (ImGui.SmallButton("History"))
            _plugin._historyWindow.IsVisible = !_plugin._historyWindow.IsVisible;

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            _plugin._settingsWindow.IsVisible = !_plugin._settingsWindow.IsVisible;
    }

    // ── Encounter timer ───────────────────────────────────────────────────────
    private void DrawEncounterTimer()
    {
        var session = GetDisplaySession();

        if (session == null)
        {
            ImGui.TextDisabled("No encounter recorded yet.");
            return;
        }

        var dur = session.FormattedDuration;

        if (session.IsActive)
        {
            // Live — green pulse indicator
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), $"● LIVE  {dur}");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                $"■ {session.ZoneName}   {dur}   "
                + $"{session.StartTime.ToLocalTime():HH:mm:ss}");
        }

        // Pinned session label
        if (_plugin._historyWindow.PinnedSession != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "[pinned]");
        }
    }

    // ── Meter bars ────────────────────────────────────────────────────────────
    private void DrawMeterBars()
    {
        var session = GetDisplaySession();
        if (session == null) return;

        var meterType = Config.CurrentMeter;
        var total     = session.GetTotal(meterType);
        var dur       = session.DurationSeconds;

        // Sort all combatants — then split into groups
        var party    = session.GetSortedByType(meterType, CombatantType.PartyMember);
        var friendly = session.GetSortedByType(meterType, CombatantType.FriendlyPlayer);
        var enemies  = session.GetSortedByType(meterType, CombatantType.Enemy);

        var anyData  = party.Count + friendly.Count + enemies.Count > 0;
        if (!anyData)
        {
            ImGui.TextDisabled("No combatants recorded.");
            return;
        }

        if (party.Count > 0)
            DrawGroup("Party", party, meterType, total, dur, session);

        if (friendly.Count > 0)
            DrawGroup("Friendly", friendly, meterType, total, dur, session);

        if (enemies.Count > 0)
            DrawGroup("Enemies", enemies, meterType, total, dur, session);
    }

    // ── Group section ─────────────────────────────────────────────────────────
    private static readonly Vector4 ColParty    = new(0.3f,  0.8f,  1f,    1f);
    private static readonly Vector4 ColFriendly = new(0.3f,  1f,    0.4f,  1f);
    private static readonly Vector4 ColEnemy    = new(1f,    0.4f,  0.4f,  1f);

    private void DrawGroup(
        string label, List<CombatantData> combatants,
        MeterType meterType, long total, double dur,
        CombatSession session)
    {
        _groupCollapsed.TryGetValue(label, out var collapsed);
        var color  = label switch { "Party" => ColParty, "Friendly" => ColFriendly, _ => ColEnemy };
        var arrow  = collapsed ? "▶" : "▼";

        if (ImGui.SmallButton($"{arrow}##grp_{label}"))
            _groupCollapsed[label] = !collapsed;

        ImGui.SameLine();
        ImGui.TextColored(color, $"{label} ({combatants.Count})");

        if (!collapsed)
            DrawRows(combatants, meterType, total, dur, session);

        ImGui.Spacing();
    }

    // ── Rows ──────────────────────────────────────────────────────────────────
    private void DrawRows(
        List<CombatantData> combatants,
        MeterType meterType, long total, double dur,
        CombatSession session)
    {
        var rowH     = Config.RowHeight;
        var avail    = ImGui.GetContentRegionAvail().X;
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

            var rowMin = ImGui.GetCursorScreenPos();
            var rowMax = new Vector2(rowMin.X + avail, rowMin.Y + rowH);

            ImGui.Dummy(new Vector2(avail, rowH));

            // Hover: brief tooltip
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text($"{c.Name}{(string.IsNullOrEmpty(c.World) ? "" : $"@{c.World}")}");
                ImGui.TextDisabled("Right-click for full breakdown");
                ImGui.EndTooltip();
            }

            // Right-click: open detail popup
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _detailEntityId = c.EntityId;
                _detailSession  = session;
                ImGui.OpenPopup("##CombatantDetail");
            }

            // Row background (alternating subtle shade)
            uint rowBg = (i % 2 == 0) ? 0x18FFFFFFu : 0x08FFFFFFu;
            dl.AddRectFilled(rowMin, rowMax, rowBg);

            // Colored bar fill
            if (pct > 0)
                dl.AddRectFilled(rowMin, new Vector2(rowMin.X + avail * pct, rowMax.Y - 1), barColor);

            // Job icon
            var iconSz = rowH - 4f;
            if (Config.ShowJobIcon)
            {
                var jobWrap = GetJobIcon(c.ClassJobId)?.GetWrapOrDefault();
                if (jobWrap != null)
                    dl.AddImage(jobWrap.Handle,
                        new Vector2(rowMin.X + 2f, rowMin.Y + 2f),
                        new Vector2(rowMin.X + 2f + iconSz, rowMin.Y + 2f + iconSz));
            }

            var textX = rowMin.X + (Config.ShowJobIcon ? iconSz + 6f : 4f);
            var textY = rowMin.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;

            // Name
            dl.AddText(new Vector2(textX, textY), 0xFFFFFFFF, c.DisplayName(showSvr, initials));

            // Value + %
            var sb = new System.Text.StringBuilder();
            if (showVal) sb.Append(FormatValue(val, meterType));
            if (showPct) sb.Append($" ({pct * 100:F0}%)");
            var right = sb.ToString().Trim();
            if (right.Length > 0)
            {
                var rightSz = ImGui.CalcTextSize(right);
                dl.AddText(new Vector2(rowMax.X - rightSz.X - 4f, textY), 0xFFFFFFFF, right);
            }
        }
    }

    // ── Detail popup (right-click) ────────────────────────────────────────────
    private void DrawDetailPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 560), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##CombatantDetail",
            ImGuiWindowFlags.NoResize))
            return;

        if (_detailSession == null
            || !_detailSession.Combatants.TryGetValue(_detailEntityId, out var c))
        {
            ImGui.Text("No data available.");
            ImGui.EndPopup();
            return;
        }

        var dur = _detailSession.DurationSeconds;

        // ── Header ────────────────────────────────────────────────────────────
        var icon = GetJobIcon(c.ClassJobId)?.GetWrapOrDefault();
        if (icon != null && Config.ShowJobIcon)
        {
            ImGui.Image(icon.Handle, new Vector2(36, 36));
            ImGui.SameLine();
        }

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
        ImGui.TextDisabled($"{typeLabel}   ●   Encounter: {_detailSession.FormattedDuration}");
        ImGui.EndGroup();

        ImGui.Separator();

        // ── Stat summary row ─────────────────────────────────────────────────
        ImGui.TextColored(new Vector4(1f,   0.4f, 0.4f, 1f), $"DMG  {FormatNumber(c.TotalDamageDealt)}");
        ImGui.SameLine(0, 20);
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1f), $"HEAL {FormatNumber(c.TotalHealingDone)}");
        ImGui.SameLine(0, 20);
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1f), $"OHEAL {FormatNumber(c.TotalOverhealingDone)}");
        ImGui.SameLine(0, 20);
        ImGui.TextColored(new Vector4(0.5f, 0.7f, 1f,   1f), $"TAKEN {FormatNumber(c.TotalDamageTaken)}");

        var dps = c.GetDps(dur);
        var hps = c.GetHps(dur);
        ImGui.TextDisabled($"DPS {FormatNumber((long)dps)}/s   HPS {FormatNumber((long)hps)}/s");

        ImGui.Separator();

        // ── Tabs ─────────────────────────────────────────────────────────────
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
        if (abilities.Count == 0)
        {
            ImGui.TextDisabled("No data recorded.");
            return;
        }

        var sorted = abilities.Values
            .OrderByDescending(a => a.TotalAmount)
            .ToList();

        var tableFlags = ImGuiTableFlags.Borders
                       | ImGuiTableFlags.RowBg
                       | ImGuiTableFlags.ScrollY
                       | ImGuiTableFlags.SizingFixedFit
                       | ImGuiTableFlags.Sortable;

        var colCount = showOverheal ? 7 : 6;
        if (!ImGui.BeginTable("##AbilityTable", colCount, tableFlags,
            new Vector2(0, ImGui.GetContentRegionAvail().Y - 4)))
            return;

        // Headers
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Ability",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Hits",     ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Total",    ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Avg",      ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Min",      ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Max",      ImGuiTableColumnFlags.WidthFixed, 70);
        if (showOverheal)
            ImGui.TableSetupColumn("Overheal", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        foreach (var a in sorted)
        {
            var pct = grandTotal > 0 ? (float)a.TotalAmount / grandTotal * 100f : 0f;

            ImGui.TableNextRow();

            // Ability name + % bar drawn behind it
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

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(a.Hits.ToString());

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(FormatNumber(a.TotalAmount));

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(FormatNumber((long)a.Average));

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(a.Hits > 0 ? FormatNumber(a.MinHit) : "-");

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(FormatNumber(a.MaxHit));

            if (showOverheal)
            {
                ImGui.TableSetColumnIndex(6);
                if (a.TotalOverheal > 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                        $"{FormatNumber(a.TotalOverheal)} ({a.OverhealPercent:F0}%)");
                }
                else
                {
                    ImGui.TextDisabled("-");
                }
            }
        }

        ImGui.EndTable();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private CombatSession? GetDisplaySession()
        => _plugin._historyWindow.PinnedSession
        ?? Tracker.ActiveSession
        ?? Tracker.Store.TempSessions.LastOrDefault();

    private ISharedImmediateTexture? GetJobIcon(byte classJobId)
    {
        if (_iconCache.TryGetValue(classJobId, out var cached)) return cached;
        try
        {
            var shared = Plugin.TextureProvider.GetFromGameIcon(
                new GameIconLookup((uint)(62100 + classJobId)));
            _iconCache[classJobId] = shared;
            return shared;
        }
        catch
        {
            _iconCache[classJobId] = null;
            return null;
        }
    }

    private static string FormatValue(long value, MeterType type) => type switch
    {
        MeterType.DPS or MeterType.HPS => $"{FormatNumber(value)}/s",
        _                              => FormatNumber(value)
    };

    internal static string FormatNumber(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F2}M",
        >= 1_000     => $"{n / 1_000.0:F1}K",
        _            => n.ToString()
    };

    private void ApplyWindowStyle()
    {
        if (Config.Style == WindowStyle.Minimal)
            ImGui.SetNextWindowBgAlpha(Config.Opacity * 0.6f);
    }

    public void Dispose()
    {
        _iconCache.Clear();
    }
}
