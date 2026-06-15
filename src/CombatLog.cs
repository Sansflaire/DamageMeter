using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DamageMeter;

/// <summary>
/// Per-session JSONL combat log. One file per session, written to
/// {configDir}/combat_logs/{sessionId}.jsonl. Each line is a JSON event:
///   {"t":millis,"e":"use",...}     — player pressed a button (UseAction hook)
///   {"t":millis,"e":"effect",...}  — ActionEffect server result
///   {"t":millis,"e":"flytext",...} — FlyText subsystem event
///   {"t":millis,"e":"meta",...}    — session header at file start
/// All events share `t` = milliseconds since session start.
/// </summary>
public sealed class CombatLog : IDisposable
{
    private readonly object       _gate = new();
    private          StreamWriter? _writer;
    private          DateTime     _origin;
    private readonly string       _dir;

    public string? CurrentPath { get; private set; }

    public CombatLog(string configDir)
    {
        _dir = Path.Combine(configDir, "combat_logs");
        Directory.CreateDirectory(_dir);
    }

    public void StartSession(string sessionId, DateTime startUtc, string metaJson)
    {
        lock (_gate)
        {
            CloseLocked();
            _origin = startUtc;
            var safe = MakeSafeName(sessionId);
            CurrentPath = Path.Combine(_dir, safe + ".jsonl");
            _writer = new StreamWriter(new FileStream(
                CurrentPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            WriteLineLocked("{\"t\":0,\"e\":\"meta\"," + metaJson + "}");
        }
    }

    public void EndSession()
    {
        lock (_gate) CloseLocked();
    }

    public void Write(string eventJson)
    {
        lock (_gate)
        {
            if (_writer == null) return;
            var t = (long)(DateTime.UtcNow - _origin).TotalMilliseconds;
            WriteLineLocked("{\"t\":" + t + "," + eventJson + "}");
        }
    }

    private void WriteLineLocked(string line)
    {
        try { _writer!.WriteLine(line); _writer.Flush(); }
        catch { /* swallow — never crash the game on log write */ }
    }

    private void CloseLocked()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        CurrentPath = null;
    }

    private static string MakeSafeName(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }

    public void Dispose() => EndSession();

    // ── JSON helpers (no Newtonsoft dependency for hot path) ──────────────────
    public static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
