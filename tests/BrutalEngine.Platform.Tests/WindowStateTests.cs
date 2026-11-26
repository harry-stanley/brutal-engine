using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class WindowStateTests
{
    [Fact]
    public void FlagValues_MatchDesign()
    {
        Assert.Equal(0, (int)WindowState.None);
        Assert.Equal(1, (int)WindowState.Visible);
        Assert.Equal(2, (int)WindowState.Minimized);
        Assert.Equal(4, (int)WindowState.Maximized);
        Assert.Equal(8, (int)WindowState.InputFocus);
        Assert.Equal(16, (int)WindowState.MouseFocus);
        Assert.Equal(32, (int)WindowState.Occluded);
    }

    [Fact]
    public void Members_AreExactlyTheDesignedFlags()
    {
        Assert.Equal([0, 1, 2, 4, 8, 16, 32], Enum.GetValues<WindowState>().Select(state => (int)state));
        Assert.Equal(
            ["None", "Visible", "Minimized", "Maximized", "InputFocus", "MouseFocus", "Occluded"],
            Enum.GetNames<WindowState>());
    }

    [Fact]
    public void Type_IsFlagsEnum()
    {
        Assert.NotNull(typeof(WindowState).GetCustomAttribute<FlagsAttribute>());
    }
}
