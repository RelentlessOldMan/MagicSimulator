using MagicSim.Cli;

namespace MagicSim.Tests;

public class CliOptionsTests
{
    [Fact]
    public void LowIsASwitchWithoutAValue()
    {
        var o = Options.Parse(new[] { "deck.txt", "--low", "--games", "5" });
        Assert.True(o.Has("--low"));
        Assert.Equal(new[] { "deck.txt" }, o.Positional);
        Assert.Equal("5", o.Get("--games"));
        Assert.False(Options.Parse(new[] { "deck.txt" }).Has("--low"));
    }

    [Fact]
    public void LowUsesTheLastCore()
    {
        Assert.Equal((nint)1 << (Environment.ProcessorCount - 1), LowPriority.CoreMask);
    }
}
