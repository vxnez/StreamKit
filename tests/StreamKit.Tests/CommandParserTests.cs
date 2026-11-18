using StreamKit.Core.Commands;
using Xunit;

namespace StreamKit.Tests;

public class CommandParserTests
{
    [Fact]
    public void ParsesNameAndArguments()
    {
        var ok = CommandParser.TryParse("  !regalar @Ana 250  ", '!', 300, out var parsed);

        Assert.True(ok);
        Assert.Equal("regalar", parsed.Name);
        Assert.Equal(2, parsed.Arguments.Count);
        Assert.Equal("@Ana", parsed.Arg(0));
        Assert.Equal("250", parsed.Arg(1));
    }

    [Fact]
    public void LowercasesNameButKeepsArguments()
    {
        Assert.True(CommandParser.TryParse("!TOP 3", '!', 300, out var parsed));
        Assert.Equal("top", parsed.Name);
        Assert.Equal("3", parsed.Arg(0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("puntos")]
    [InlineData("!")]
    [InlineData("!   ")]
    public void RejectsInvalidMessages(string? message)
    {
        Assert.False(CommandParser.TryParse(message, '!', 300, out _));
    }

    [Fact]
    public void RejectsOverlongMessages()
    {
        var message = "!puntos " + new string('a', 400);
        Assert.False(CommandParser.TryParse(message, '!', 300, out _));
    }

    [Fact]
    public void RejectsOverlongNames()
    {
        var message = "!" + new string('a', 40);
        Assert.False(CommandParser.TryParse(message, '!', 300, out _));
    }

    [Fact]
    public void CapsArgumentCount()
    {
        var message = "!cmd " + string.Join(' ', Enumerable.Range(1, 20));
        Assert.True(CommandParser.TryParse(message, '!', 300, out var parsed));
        Assert.Equal(CommandParser.MaxArguments, parsed.Arguments.Count);
    }

    [Fact]
    public void HandlesMultipleSpacesBetweenArguments()
    {
        Assert.True(CommandParser.TryParse("!regalar    ana    10", '!', 300, out var parsed));
        Assert.Equal(new[] { "ana", "10" }, parsed.Arguments);
    }

    [Fact]
    public void ArgReturnsEmptyWhenOutOfRange()
    {
        Assert.True(CommandParser.TryParse("!puntos", '!', 300, out var parsed));
        Assert.Equal(string.Empty, parsed.Arg(0));
        Assert.Empty(parsed.Arguments);
    }
}
