using System.Collections.Concurrent;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// In-process record of when each identified agent (keyed by <c>sourceAgent</c>) last recalled/searched, so a
/// write can nudge an agent that writes without recalling first (MEMP-204). Best-effort and volatile: it lives
/// only for the server process — stateless HTTP has no session — so after a restart the first write from each
/// agent may be nudged. Bounded to cap memory; thread-safe.
/// </summary>
public sealed class ReadActivityTracker
{
    /// <summary>How recent a recall must be to suppress the write nudge.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private const int MaxAgents = 4096;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRead = new(StringComparer.Ordinal);

    /// <summary>Creates the tracker over a clock (defaults to the system clock).</summary>
    /// <param name="clock">Time source for read timestamps and the recency window.</param>
    public ReadActivityTracker(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Records that <paramref name="agent"/> just performed a recall/search (no-op for a null/blank agent).</summary>
    /// <param name="agent">The reading agent's <c>sourceAgent</c> identity.</param>
    public void RecordRead(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return;
        }

        if (_lastRead.Count >= MaxAgents && !_lastRead.ContainsKey(agent))
        {
            Evict();
        }

        _lastRead[agent] = _clock.GetUtcNow();
    }

    /// <summary>True when <paramref name="agent"/> recorded a recall/search within the recent <see cref="Window"/>.</summary>
    /// <param name="agent">The writing agent's <c>sourceAgent</c> identity.</param>
    public bool HasRecentRead(string? agent) =>
        !string.IsNullOrWhiteSpace(agent)
        && _lastRead.TryGetValue(agent!, out var when)
        && _clock.GetUtcNow() - when <= Window;

    // Drop the oldest ~half when the map is full, so a long-running server can't grow unbounded.
    private void Evict()
    {
        foreach (var stale in _lastRead.OrderBy(pair => pair.Value).Take(_lastRead.Count / 2).Select(pair => pair.Key).ToList())
        {
            _lastRead.TryRemove(stale, out _);
        }
    }
}
