using System.Diagnostics;
using System.IO;

namespace WallBili.Core;

/// <summary>
/// Minimal append-only performance log for the perf branch. Synchronous one-line appends under a
/// lock: a line costs microseconds, and guaranteed persistence matters more than non-blocking
/// semantics on this diagnostic branch. Writes before <see cref="Start"/> (unit tests) are dropped.
/// Line format: &lt;RFC3339 timestamp&gt; &lt;ms since start&gt; [category] k=v k=v
/// </summary>
public static class PerfLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static long _started;

    /// <summary>Starts the log file. Call once from application startup; later calls are no-ops.</summary>
    public static void Start(string dataDirectory)
    {
        lock (Gate)
        {
            if (_writer is not null) return;
            try
            {
                Directory.CreateDirectory(dataDirectory);
                _started = Environment.TickCount64;
                _writer = new StreamWriter(Path.Combine(dataDirectory, "perf.log"), append: true) { AutoFlush = true };
                _writer.WriteLine(Line("perf-init", $"pid={Environment.ProcessId} dataDirectory={dataDirectory}"));
            }
            catch (Exception ex)
            {
                _writer = null;
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "wallbili-perf-error.log"),
                        $"{DateTimeOffset.Now:O} Start failed: {ex}" + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    /// <summary>Appends one line. Never throws; drops the line when not started.</summary>
    public static void Write(string category, string fields = "")
    {
        lock (Gate)
        {
            if (_writer is null) return;
            try { _writer.WriteLine(Line(category, fields)); }
            catch { /* the perf log must never take the app down */ }
        }
    }

    /// <summary>Returns a closure that logs elapsed_ms when invoked. Logs a start line immediately,
    /// so a step that never completes still shows where execution stopped.</summary>
    public static Action Step(string name)
    {
        Write(name + ".start");
        var stamp = Stopwatch.GetTimestamp();
        return () => Write(name, FormattableString.Invariant(
            $"elapsed_ms={(Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency:F1}"));
    }

    /// <summary>Flushes and closes the log file. Call once on shutdown.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_writer is null) return;
            try { _writer.Dispose(); } catch { }
            _writer = null;
        }
    }

    private static string Line(string category, string fields)
    {
        var relative = Environment.TickCount64 - Volatile.Read(ref _started);
        return $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {relative,7} [{category}] {fields}".TrimEnd();
    }
}
