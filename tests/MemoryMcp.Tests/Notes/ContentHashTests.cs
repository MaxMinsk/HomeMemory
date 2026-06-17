using MemoryMcp.Core.Notes;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class ContentHashTests
{
    [Fact]
    public void Identical_content_hashes_equal_and_is_64_hex_chars()
    {
        var a = ContentHash.Compute("backlog_item", "T", "body", """{"key":"MEMP-1","status":"ready"}""", """["x","y"]""");
        var b = ContentHash.Compute("backlog_item", "T", "body", """{"key":"MEMP-1","status":"ready"}""", """["x","y"]""");

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.True(a.All(Uri.IsHexDigit));
    }

    [Fact]
    public void Payload_key_order_and_whitespace_do_not_change_the_hash()
    {
        var a = ContentHash.Compute("t", "T", null, """{"a":1,"b":2}""", null);
        var b = ContentHash.Compute("t", "T", null, "{ \"b\": 2,\n  \"a\": 1 }", null);

        Assert.Equal(a, b); // canonicalized: object keys sorted, insignificant whitespace dropped
    }

    [Fact]
    public void Different_content_changes_the_hash()
    {
        var baseline = ContentHash.Compute("t", "T", "body", null, null);

        Assert.NotEqual(baseline, ContentHash.Compute("t", "T", "body!", null, null)); // body
        Assert.NotEqual(baseline, ContentHash.Compute("t", "T2", "body", null, null)); // title
        Assert.NotEqual(baseline, ContentHash.Compute("u", "T", "body", null, null));  // type
    }
}
