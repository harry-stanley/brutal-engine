using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The negotiated feature flags (docs/design/graphics.md section 2).</summary>
public sealed class GraphicsFeaturesTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.True(typeof(GraphicsFeatures).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(long), Enum.GetUnderlyingType(typeof(GraphicsFeatures)));
        Assert.Equal(
            [("None", 0L), ("SamplerAnisotropy", 1L), ("TimestampQueries", 2L), ("TextureCompressionBC", 4L), ("TextureCompressionAstc", 8L)],
            Enum.GetValues<GraphicsFeatures>().Select(v => (v.ToString(), (long)v)));
    }
}
