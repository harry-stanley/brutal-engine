using System;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

public sealed class VideoDriverRulesTests
{
    [Theory]
    [InlineData(false, null, null, false)]
    [InlineData(false, "cocoa", "cocoa", false)]
    [InlineData(true, null, null, true)]
    [InlineData(true, null, "cocoa", true)]
    [InlineData(true, null, "dummy", false)]
    [InlineData(true, null, "offscreen", false)]
    [InlineData(true, "dummy", null, false)]
    [InlineData(true, "offscreen", null, false)]
    [InlineData(true, "DUMMY", null, false)]
    [InlineData(true, "dummy", "offscreen", false)]
    [InlineData(true, "cocoa", "dummy", true)]
    [InlineData(true, "x11", "offscreen", true)]
    [InlineData(true, "", "dummy", true)]
    [InlineData(true, "dummy,cocoa", null, true)]
    public void MustCheckMainThread_Table(bool isMacOS, string? environmentDriver, string? requestedDriver, bool expected)
    {
        Assert.Equal(expected, VideoDriverRules.MustCheckMainThread(isMacOS, environmentDriver, requestedDriver));
    }

    [Theory]
    [InlineData("offscreen", HeadlessMode.Offscreen)]
    [InlineData("OFFSCREEN", HeadlessMode.Offscreen)]
    [InlineData("dummy", HeadlessMode.NoSurface)]
    [InlineData("Dummy", HeadlessMode.NoSurface)]
    [InlineData("windows", HeadlessMode.None)]
    [InlineData("x11", HeadlessMode.None)]
    [InlineData("wayland", HeadlessMode.None)]
    [InlineData("cocoa", HeadlessMode.None)]
    [InlineData("", HeadlessMode.None)]
    [InlineData("dummy,offscreen", HeadlessMode.None)]
    public void HeadlessFrom_Table(string driver, HeadlessMode expected)
    {
        Assert.Equal(expected, VideoDriverRules.HeadlessFrom(driver));
    }

    [Fact]
    public void HeadlessFrom_Null_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(() => VideoDriverRules.HeadlessFrom(null!));
    }

    [Theory]
    [InlineData(HeadlessMode.None, null)]
    [InlineData(HeadlessMode.Offscreen, "offscreen")]
    [InlineData(HeadlessMode.NoSurface, "dummy")]
    public void HintFor_Table(HeadlessMode mode, string? expected)
    {
        Assert.Equal(expected, VideoDriverRules.HintFor(mode));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void HintFor_UndefinedMode_Throws(int mode)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => VideoDriverRules.HintFor((HeadlessMode)mode));
    }
}
