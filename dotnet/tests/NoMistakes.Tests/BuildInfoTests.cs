using NoMistakes.Core;
using Xunit;

namespace NoMistakes.Tests;

public sealed class BuildInfoTests
{
    [Fact]
    public void FormatMatchesGoBuildInfoShape()
    {
        var formatted = BuildInfo.Format(new BuildInfoOptions("v1.2.3", "abc1234", "2026-07-08T00:00:00Z"));

        Assert.Equal("v1.2.3 (abc1234) 2026-07-08T00:00:00Z", formatted);
    }

    [Fact]
    public void BlankVersionFallsBackToDev()
    {
        var formatted = BuildInfo.Format(new BuildInfoOptions("", "abc1234", "2026-07-08T00:00:00Z"));

        Assert.Equal("dev (abc1234) 2026-07-08T00:00:00Z", formatted);
    }
}
