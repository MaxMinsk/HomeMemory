using MemoryMcp.Core;
using Xunit;

namespace MemoryMcp.Tests;

public class ApplicationInfoTests
{
    [Fact]
    public void Name_is_not_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ApplicationInfo.Name));
    }
}
