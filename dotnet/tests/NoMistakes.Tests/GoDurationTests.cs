using NoMistakes.Config;
using Xunit;

namespace NoMistakes.Tests;

public sealed class GoDurationTests
{
    [Theory]
    [InlineData("168h", 168 * 3600)]
    [InlineData("2h30m", (2 * 3600) + (30 * 60))]
    [InlineData("90m", 90 * 60)]
    [InlineData("0s", 0)]
    [InlineData("0", 0)]
    [InlineData("1h", 3600)]
    [InlineData("500ms", 0)] // 0.5s truncates to 0 whole seconds
    public void ParsesGoDurationsIntoSeconds(string input, long wantSeconds)
    {
        var parsed = GoDuration.Parse(input);
        Assert.Equal(wantSeconds, (long)parsed.TotalSeconds);
    }

    [Fact]
    public void ParsesExactCompositeDuration()
    {
        Assert.Equal(TimeSpan.FromMinutes(150), GoDuration.Parse("2h30m"));
    }

    [Fact]
    public void ParsesNegativeDuration()
    {
        Assert.Equal(TimeSpan.FromMinutes(-5), GoDuration.Parse("-5m"));
    }

    [Fact]
    public void ParsesFractionalDuration()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), GoDuration.Parse("1.5s"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-duration")]
    [InlineData("10")] // missing unit
    [InlineData("5x")] // unknown unit
    public void ThrowsOnInvalidDuration(string input)
    {
        Assert.Throws<FormatException>(() => { GoDuration.Parse(input); });
    }
}
