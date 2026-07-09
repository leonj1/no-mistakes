using NoMistakes.Scm.SafeUrl;
using Xunit;

namespace NoMistakes.Tests;

public class RedactorTests
{
    [Fact]
    public void RedactText_HidesUrlUserInfo()
    {
        var input = "failed: https://user:token@example.com/repo.git not found";
        var got = Redactor.RedactText(input);
        Assert.DoesNotContain("token", got);
        Assert.Contains("redacted@example.com", got);
    }

    [Fact]
    public void RedactText_LeavesCredentialFreeUrlUnchanged()
    {
        var input = "cloning https://example.com/repo.git";
        Assert.Equal(input, Redactor.RedactText(input));
    }

    [Fact]
    public void RedactText_LeavesNonUrlUnchanged()
    {
        Assert.Equal("no urls here", Redactor.RedactText("no urls here"));
    }
}
