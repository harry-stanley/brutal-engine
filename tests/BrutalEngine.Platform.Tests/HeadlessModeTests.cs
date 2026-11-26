using System;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class HeadlessModeTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.Equal(0, (int)HeadlessMode.None);
        Assert.Equal(1, (int)HeadlessMode.Offscreen);
        Assert.Equal(2, (int)HeadlessMode.NoSurface);
        Assert.Equal(["None", "Offscreen", "NoSurface"], Enum.GetNames<HeadlessMode>());
    }
}
