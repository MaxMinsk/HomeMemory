using MemoryMcp.Core.Query;
using Xunit;

namespace MemoryMcp.Tests.Query;

public class RecencyDecayTests
{
    [Fact]
    public void Score_halves_each_half_life_and_clamps_at_the_edges()
    {
        Assert.Equal(1.0, RecencyDecay.Score(0, 30), 3);
        Assert.Equal(0.5, RecencyDecay.Score(30, 30), 3);
        Assert.Equal(0.25, RecencyDecay.Score(60, 30), 3);
        Assert.Equal(1.0, RecencyDecay.Score(-5, 30), 3); // negative age clamps to "brand new"
        Assert.Equal(0.0, RecencyDecay.Score(10, 0), 3);  // non-positive half-life => fully decayed
    }

    [Fact]
    public void HalfLifeDays_uses_per_type_values_with_a_default()
    {
        Assert.Equal(7.0, RecencyDecay.HalfLifeDays("episode"));
        Assert.True(RecencyDecay.HalfLifeDays("recipe") > RecencyDecay.HalfLifeDays("backlog_item")); // durable > ephemeral
        Assert.Equal(RecencyDecay.DefaultHalfLifeDays, RecencyDecay.HalfLifeDays("unknown_type"));
        Assert.Equal(RecencyDecay.DefaultHalfLifeDays, RecencyDecay.HalfLifeDays(null));
    }
}
