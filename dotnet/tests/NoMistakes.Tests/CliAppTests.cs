using NoMistakes.Cli;
using NoMistakes.Core;
using Xunit;

namespace NoMistakes.Tests;

public sealed class CliAppTests
{
    [Fact]
    public void HelpPrintsRootDescriptionAndUsage()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApp(stdout, stderr);

        var code = app.Run(["--help"]);

        Assert.Equal(0, code);
        Assert.Contains("Local Git proxy that validates code before pushing", stdout.ToString());
        Assert.Contains("Usage:", stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void VersionPrintsBuildInfo()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApp(stdout, stderr, new BuildInfoOptions("v1.2.3", "abc1234", "2026-07-08T00:00:00Z"));

        var code = app.Run(["--version"]);

        Assert.Equal(0, code);
        Assert.Equal("no-mistakes version v1.2.3 (abc1234) 2026-07-08T00:00:00Z" + Environment.NewLine, stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void UnknownCommandReturnsUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApp(stdout, stderr);

        var code = app.Run(["not-a-command"]);

        Assert.Equal(2, code);
        Assert.Empty(stdout.ToString());
        Assert.Contains("unknown command: not-a-command", stderr.ToString());
    }
}
