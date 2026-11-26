using System;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class WindowModeTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.Equal(0, (int)WindowMode.Windowed);
        Assert.Equal(1, (int)WindowMode.BorderlessFullscreen);
        Assert.Equal(2, (int)WindowMode.ExclusiveFullscreen);
        Assert.Equal(["Windowed", "BorderlessFullscreen", "ExclusiveFullscreen"], Enum.GetNames<WindowMode>());
    }
}
