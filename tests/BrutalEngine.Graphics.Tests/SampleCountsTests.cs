using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The multisample count flags (docs/design/graphics.md section 2).</summary>
public sealed class SampleCountsTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.True(typeof(SampleCounts).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(SampleCounts)));
        Assert.Equal(
            [("None", 0), ("X1", 1), ("X2", 2), ("X4", 4), ("X8", 8), ("X16", 16)],
            Enum.GetValues<SampleCounts>().Select(v => (v.ToString(), (int)v)));
    }
}
