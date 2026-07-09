using NoMistakes.Cli;
using Xunit;

namespace NoMistakes.Tests;

public class ToonTests
{
    [Fact]
    public void PrimitivesRenderAsKeyValueLines()
    {
        var doc = Toon.MarshalString(new ToonObject(
            new ToonField("name", "value"),
            new ToonField("count", 3),
            new ToonField("big", 1200L),
            new ToonField("ok", true),
            new ToonField("off", false),
            new ToonField("missing", null)));
        Assert.Equal("name: value\ncount: 3\nbig: 1200\nok: true\noff: false\nmissing: null", doc);
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData(" padded", "\" padded\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("false", "\"false\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("42", "\"42\"")]
    [InlineData("-1.5e3", "\"-1.5e3\"")]
    [InlineData("007", "\"007\"")]
    [InlineData("has, comma", "\"has, comma\"")]
    [InlineData("gate: open", "\"gate: open\"")]
    [InlineData("-leading-dash", "\"-leading-dash\"")]
    [InlineData("a\"quote", "\"a\\\"quote\"")]
    [InlineData("line\nbreak", "\"line\\nbreak\"")]
    [InlineData("plain text with `backticks` (and parens)", "plain text with `backticks` (and parens)")]
    [InlineData("ask-user", "ask-user")]
    [InlineData("feature/x", "feature/x")]
    public void StringQuotingFollowsToonRules(string input, string want)
    {
        var doc = Toon.MarshalString(new ToonObject(new ToonField("k", input)));
        Assert.Equal("k: " + want, doc);
    }

    [Fact]
    public void NestedObjectsIndentTwoSpacesPerLevel()
    {
        var doc = Toon.MarshalString(new ToonObject(
            new ToonField("outer", new ToonObject(
                new ToonField("a", "1x"),
                new ToonField("inner", new ToonObject(
                    new ToonField("b", "2x")))))));
        Assert.Equal("outer:\n  a: 1x\n  inner:\n    b: 2x", doc);
    }

    [Fact]
    public void StringArrayRendersInlineWithLengthHeader()
    {
        var doc = Toon.MarshalString(new ToonObject(
            new ToonField("help", new List<string> { "first step", "second, with comma" })));
        Assert.Equal("help[2]: first step,\"second, with comma\"", doc);
    }

    [Fact]
    public void EmptyArrayRendersBareHeader()
    {
        var doc = Toon.MarshalString(new ToonObject(
            new ToonField("steps", new List<string>())));
        Assert.Equal("steps[0]:", doc);
    }

    [Fact]
    public void UniformObjectRowsRenderAsTabularArray()
    {
        var rows = new List<ToonObject>
        {
            new(new ToonField("step", "review"), new ToonField("status", "completed"), new ToonField("n", 1)),
            new(new ToonField("step", "test"), new ToonField("status", "awaiting_approval"), new ToonField("n", 0)),
        };
        var doc = Toon.MarshalString(new ToonObject(new ToonField("steps", rows)));
        Assert.Equal(
            "steps[2]{step,status,n}:\n  review,completed,1\n  test,awaiting_approval,0",
            doc);
    }

    [Fact]
    public void ControlCharacterInStringThrows()
    {
        Assert.Throws<ToonEncodingException>(
            () => Toon.MarshalString(new ToonObject(new ToonField("k", "bad\u0001char"))));
    }

    [Fact]
    public void NonIdentifierKeyIsQuoted()
    {
        var doc = Toon.MarshalString(new ToonObject(new ToonField("weird key", "v")));
        Assert.Equal("\"weird key\": v", doc);
    }
}
