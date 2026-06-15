using MemoryMcp.Core.Notes;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class RelationNameTests
{
    [Theory]
    [InlineData("uses")]
    [InlineData("supersedes")]
    [InlineData("depends_on")]
    [InlineData("derived_from")]
    [InlineData("in_sprint")]
    [InlineData("rendered_from")]
    public void Accepts_active_voice_snake_case(string rel) => Assert.True(RelationName.IsValid(rel));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DependsOn")]     // PascalCase
    [InlineData("depends-on")]    // kebab
    [InlineData("depends on")]    // space
    [InlineData("_leading")]      // must start with a letter
    [InlineData("1bad")]          // must start with a letter
    public void Rejects_non_conforming(string rel) => Assert.False(RelationName.IsValid(rel));
}
