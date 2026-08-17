using System.Diagnostics;

namespace MemoryMcp.Core.Diagnostics;

/// <summary>
/// How long one kind of operation has been taking lately (MEMP-247).
/// </summary>
/// <param name="Operation">The operation name, e.g. <c>notes_search</c>.</param>
/// <param name="Count">Calls recorded since start (not since the sample window — the window only bounds timings).</param>
/// <param name="P50Ms">Median duration over the retained samples.</param>
/// <param name="P95Ms">95th percentile over the retained samples — where the pain shows up first.</param>
/// <param name="MaxMs">Slowest retained sample.</param>
public sealed record OperationTiming(string Operation, long Count, double P50Ms, double P95Ms, double MaxMs);

/// <summary>
/// What the server costs to run right now (MEMP-247): per-operation timings plus process figures.
/// <para>Deliberately NOT persisted and reset by a restart. This is a health signal, not an audit trail —
/// <c>note_events</c> already covers the audit, and a metric that has to survive a restart is a metric that
/// needs storage, migrations and pruning for no gain.</para>
/// </summary>
/// <param name="Operations">Per-operation timings, slowest p95 first.</param>
/// <param name="WorkingSetBytes">Resident memory of the server process.</param>
/// <param name="ManagedHeapBytes">Managed heap in use (the part this server's own code controls).</param>
/// <param name="CpuSeconds">Total processor time consumed since start, across all cores.</param>
/// <param name="UptimeSeconds">How long the process has been running — the denominator for CPU seconds.</param>
public sealed record LoadReport(
    IReadOnlyList<OperationTiming> Operations, long WorkingSetBytes, long ManagedHeapBytes,
    double CpuSeconds, double UptimeSeconds);

/// <summary>
/// Records how long operations take, in memory, so "is this too heavy for the box?" is a reading rather than
/// an argument (MEMP-247). Bounded by construction: each operation keeps only its most recent
/// <see cref="Window"/> samples in a ring buffer, so memory is O(operations x window) no matter how long the
/// server runs or how hard it is hit.
/// </summary>
public sealed class OperationMetrics
{
    /// <summary>Samples retained per operation. Enough for a stable p95, small enough to stay quiet in memory.</summary>
    public const int Window = 512;

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly long _startedTicks;

    /// <summary>Creates the recorder.</summary>
    /// <param name="timeProvider">Clock used for uptime; defaults to the system clock.</param>
    public OperationMetrics(TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _startedTicks = _clock.GetTimestamp();
    }

    /// <summary>Records one completed operation.</summary>
    /// <param name="operation">Operation name (a tool name — a bounded, non-user-controlled set).</param>
    /// <param name="elapsedMs">How long it took, in milliseconds.</param>
    public void Record(string operation, double elapsedMs)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(operation, out var bucket))
            {
                bucket = new Bucket();
                _buckets[operation] = bucket;
            }

            bucket.Add(elapsedMs);
        }
    }

    /// <summary>
    /// Times <paramref name="action"/> and records it under <paramref name="operation"/>. A failed call is
    /// still recorded: an operation that is slow because it throws is exactly what this is meant to reveal.
    /// </summary>
    /// <typeparam name="T">The action's result type.</typeparam>
    /// <param name="operation">Operation name.</param>
    /// <param name="action">The work to time.</param>
    public T Measure<T>(string operation, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var from = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            Record(operation, Stopwatch.GetElapsedTime(from).TotalMilliseconds);
        }
    }

    /// <summary>Reads the current load: per-operation timings (worst p95 first) plus process figures.</summary>
    public LoadReport Snapshot()
    {
        List<OperationTiming> timings;
        lock (_gate)
        {
            timings = _buckets.Select(pair => pair.Value.ToTiming(pair.Key)).ToList();
        }

        timings.Sort((a, b) => b.P95Ms.CompareTo(a.P95Ms));

        using var process = Process.GetCurrentProcess();
        var uptime = Stopwatch.GetElapsedTime(_startedTicks, _clock.GetTimestamp()).TotalSeconds;
        return new LoadReport(
            timings, process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false),
            Math.Round(process.TotalProcessorTime.TotalSeconds, 2), Math.Round(uptime, 1));
    }

    // A ring of recent durations plus an all-time count. The ring is what keeps this bounded; the count is
    // kept separately so "how often" survives being pushed out of the timing window.
    private sealed class Bucket
    {
        private readonly double[] _samples = new double[Window];
        private int _next;
        private int _filled;
        private long _count;

        public void Add(double elapsedMs)
        {
            _samples[_next] = elapsedMs;
            _next = (_next + 1) % Window;
            _filled = Math.Min(_filled + 1, Window);
            _count++;
        }

        public OperationTiming ToTiming(string operation)
        {
            if (_filled == 0)
            {
                return new OperationTiming(operation, _count, 0, 0, 0);
            }

            var sorted = new double[_filled];
            Array.Copy(_samples, sorted, _filled);
            Array.Sort(sorted);
            return new OperationTiming(
                operation, _count,
                Math.Round(Percentile(sorted, 0.50), 2),
                Math.Round(Percentile(sorted, 0.95), 2),
                Math.Round(sorted[^1], 2));
        }

        // Nearest-rank percentile: with few samples an interpolated one invents precision that isn't there.
        private static double Percentile(double[] sorted, double fraction)
        {
            var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }
    }
}
