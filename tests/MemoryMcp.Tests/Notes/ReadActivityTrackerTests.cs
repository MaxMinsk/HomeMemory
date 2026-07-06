using MemoryMcp.Core.Notes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-204: the process-local recall tracker behind the recall-before-write nudge.
public class ReadActivityTrackerTests
{
    [Fact]
    public void An_agent_with_a_recorded_read_has_a_recent_read()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var tracker = new ReadActivityTracker(clock);

        tracker.RecordRead("kitchen-agent");

        Assert.True(tracker.HasRecentRead("kitchen-agent"));
        Assert.False(tracker.HasRecentRead("other-agent")); // a different agent has no recorded read
    }

    [Fact]
    public void A_read_older_than_the_window_no_longer_counts()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var tracker = new ReadActivityTracker(clock);
        tracker.RecordRead("agent");

        clock.Advance(ReadActivityTracker.Window + TimeSpan.FromMinutes(1));

        Assert.False(tracker.HasRecentRead("agent"));
    }

    [Fact]
    public void A_null_or_blank_agent_is_never_tracked()
    {
        var tracker = new ReadActivityTracker(new FakeTimeProvider());

        tracker.RecordRead(null);
        tracker.RecordRead("   ");

        Assert.False(tracker.HasRecentRead(null));
        Assert.False(tracker.HasRecentRead("   "));
    }
}
