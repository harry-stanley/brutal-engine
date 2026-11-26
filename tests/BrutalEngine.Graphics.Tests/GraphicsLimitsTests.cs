using System.Globalization;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The adapter limits and the capabilities record that carries them (docs/design/graphics.md section 2).</summary>
public sealed class GraphicsLimitsTests
{
    private static readonly GraphicsLimits _limits = new()
    {
        MaxTextureDimension2D = 32768,
        MaxTextureArrayLayers = 2048,
        MaxBindGroups = 32,
        MaxPushConstantBytes = 256,
        MaxColorTargets = 8,
        MinUniformBufferOffsetAlignment = 64,
        MinStorageBufferOffsetAlignment = 16,
        MaxSamplerAnisotropy = 16.5f,
        ColorSampleCounts = SampleCounts.X1 | SampleCounts.X2 | SampleCounts.X4 | SampleCounts.X8,
        TimestampPeriodNanoseconds = 0.25,
    };

    private const string LimitsText =
        "GraphicsLimits { MaxTextureDimension2D = 32768, MaxTextureArrayLayers = 2048, MaxBindGroups = 32, "
        + "MaxPushConstantBytes = 256, MaxColorTargets = 8, MinUniformBufferOffsetAlignment = 64, "
        + "MinStorageBufferOffsetAlignment = 16, MaxSamplerAnisotropy = 16.5, ColorSampleCounts = X1, X2, X4, X8, "
        + "TimestampPeriodNanoseconds = 0.25 }";

    [Fact]
    public void ToString_UnderAnotherCulture_ListsEveryFieldInvariantly()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        // German writes 16.5 as "16,5" and 0.25 as "0,25".
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(LimitsText, _limits.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CapabilitiesToString_UnderAnotherCulture_FormatsTheLimitsInvariantly()
    {
        var capabilities = new GraphicsCapabilities
        {
            Features = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TimestampQueries,
            Limits = _limits,
        };

        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(
                "GraphicsCapabilities { Features = SamplerAnisotropy, TimestampQueries, Limits = " + LimitsText + " }",
                capabilities.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Equals_ComparesEveryField()
    {
        Assert.Equal(_limits, _limits with { });
        Assert.NotEqual(_limits, _limits with { MaxSamplerAnisotropy = 16f });
        Assert.NotEqual(_limits, _limits with { TimestampPeriodNanoseconds = 1 });
        Assert.NotEqual(_limits, _limits with { ColorSampleCounts = SampleCounts.X1 });
        Assert.Equal(0, default(GraphicsLimits).MaxTextureDimension2D);
    }
}
