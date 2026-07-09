using NoMistakes.Scm;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports Go's internal/scm prbody tests: the UTF-16 length measure, the
/// per-provider description caps, and the truncation clamp.
/// </summary>
public class PRBodyTests
{
    [Fact]
    public void MaxCharsCapsOnlyAzureDevOps()
    {
        Assert.Equal(4000, PRBody.MaxChars(Provider.AzureDevOps));
        foreach (var p in new[] { Provider.GitHub, Provider.GitLab, Provider.Bitbucket, Provider.Unknown })
        {
            Assert.Equal(0, PRBody.MaxChars(p));
        }
    }

    [Fact]
    public void LengthCountsUtf16Units()
    {
        Assert.Equal(3, PRBody.Length("abc"));
        // A non-BMP rune is two UTF-16 code units, matching Azure's .NET measure.
        Assert.Equal(2, PRBody.Length("😀"));
    }

    [Fact]
    public void ClampLeavesFittingBodiesAlone()
    {
        Assert.Equal("short", PRBody.Clamp("short", 0));
        Assert.Equal("short", PRBody.Clamp("short", 4000));
        Assert.Equal(4000, PRBody.Length(PRBody.Clamp(new string('a', 4000), 4000)));
    }

    [Fact]
    public void ClampTruncatesWithMarker()
    {
        var clamped = PRBody.Clamp(new string('x', 5000), 4000);

        Assert.True(PRBody.Length(clamped) <= 4000);
        Assert.EndsWith(PRBody.TruncationMarker, clamped);
    }

    [Fact]
    public void ClampRespectsUtf16BudgetForEmoji()
    {
        // 3000 emoji = 6000 UTF-16 units in; the clamp must respect the
        // UTF-16 budget, never overshoot by counting runes, and never split
        // a surrogate pair at the cut.
        var emoji = PRBody.Clamp(string.Concat(Enumerable.Repeat("😀", 3000)), 4000);

        Assert.True(PRBody.Length(emoji) <= 4000);
        Assert.False(char.IsHighSurrogate(emoji[^(PRBody.TruncationMarker.Length + 1)]));
    }
}
