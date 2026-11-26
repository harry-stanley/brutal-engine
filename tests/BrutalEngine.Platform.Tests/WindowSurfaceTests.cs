using System;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class WindowSurfaceTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.Equal(0, (int)WindowSurface.None);
        Assert.Equal(1, (int)WindowSurface.Vulkan);
        Assert.Equal(["None", "Vulkan"], Enum.GetNames<WindowSurface>());
    }
}
