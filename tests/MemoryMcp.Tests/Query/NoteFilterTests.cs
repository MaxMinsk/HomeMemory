using MemoryMcp.Core.Query;
using Xunit;

namespace MemoryMcp.Tests.Query;

public class NoteFilterTests
{
    [Fact]
    public void Compiles_payload_equality_with_parameter()
    {
        var compiled = NoteFilter.Compile("payload.sprint == 'S1'");

        Assert.Contains("json_extract(n.payload_json, '$.sprint')", compiled.Sql);
        Assert.Equal("S1", Assert.Single(compiled.Parameters).Value);
    }

    [Fact]
    public void Compiles_envelope_field()
    {
        var compiled = NoteFilter.Compile("status == 'ready'");

        Assert.Contains("n.status =", compiled.Sql);
        Assert.Equal("ready", Assert.Single(compiled.Parameters).Value);
    }

    [Fact]
    public void Compiles_and_or_and_parentheses()
    {
        var compiled = NoteFilter.Compile("status == 'ready' OR (status == 'next' AND payload.sprint == 'S1')");

        Assert.Equal(3, compiled.Parameters.Count);
        Assert.Contains(" OR ", compiled.Sql);
        Assert.Contains(" AND ", compiled.Sql);
    }

    [Fact]
    public void Compiles_in_list()
    {
        var compiled = NoteFilter.Compile("status in ('ready', 'next', 'blocked')");

        Assert.Equal(3, compiled.Parameters.Count);
        Assert.Contains("n.status IN (", compiled.Sql);
    }

    [Fact]
    public void Compiles_is_null_and_is_not_null()
    {
        var nul = NoteFilter.Compile("payload.sprint is null");
        Assert.Contains("json_extract(n.payload_json, '$.sprint') IS NULL", nul.Sql);
        Assert.Empty(nul.Parameters);

        var notNul = NoteFilter.Compile("payload.sprint is not null");
        Assert.Contains("IS NOT NULL", notNul.Sql);
    }

    [Fact]
    public void Values_are_parameterized_not_inlined()
    {
        var compiled = NoteFilter.Compile("payload.note == \"x'; DROP TABLE notes;--\"");

        Assert.DoesNotContain("DROP", compiled.Sql); // the dangerous value never reaches the SQL text
        Assert.Equal("x'; DROP TABLE notes;--", Assert.Single(compiled.Parameters).Value);
    }

    [Theory]
    [InlineData("bogusfield == 'x'")]          // unknown envelope field
    [InlineData("payload.1bad == 'x'")]        // invalid payload key (starts with digit)
    [InlineData("status ==")]                  // missing value
    [InlineData("status 'ready'")]             // missing operator
    [InlineData("status == 'ready' AND")]      // dangling AND
    [InlineData("domain; drop")]               // unexpected character
    public void Invalid_filter_throws(string expression)
    {
        Assert.Throws<FilterException>(() => NoteFilter.Compile(expression));
    }
}
