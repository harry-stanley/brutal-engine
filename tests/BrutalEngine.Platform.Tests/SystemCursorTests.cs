using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class SystemCursorTests
{
    [Fact]
    public void Values_AreContiguousFromDefault()
    {
        Assert.Equal(Enumerable.Range(0, 12), Enum.GetValues<SystemCursor>().Select(cursor => (int)cursor));
        Assert.Equal(
            [
                "Default", "Text", "Wait", "Crosshair", "Progress", "ResizeNwse", "ResizeNesw", "ResizeEw", "ResizeNs",
                "Move", "NotAllowed", "Pointer",
            ],
            Enum.GetNames<SystemCursor>());
        Assert.Equal(SystemCursor.Default, default);
    }
}
