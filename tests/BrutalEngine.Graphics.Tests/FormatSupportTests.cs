using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The per-format support flags (docs/design/graphics.md section 2).</summary>
public sealed class FormatSupportTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.True(typeof(FormatSupport).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(FormatSupport)));
        Assert.Equal(
            [
                ("None", 0), ("Sampled", 1), ("Filterable", 2), ("ColorTarget", 4), ("Blendable", 8), ("DepthStencilTarget", 16),
                ("Storage", 32), ("CopySource", 64), ("CopyDestination", 128), ("Multisample", 256),
            ],
            Enum.GetValues<FormatSupport>().Select(v => (v.ToString(), (int)v)));
    }
}
