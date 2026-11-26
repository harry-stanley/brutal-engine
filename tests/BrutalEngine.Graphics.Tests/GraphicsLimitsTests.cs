using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

    // One row per GraphicsLimits property; Equals_FieldRows_CoverEveryProperty fails until a new limit adds its row.
    private static readonly string[] _fields =
    [
        nameof(GraphicsLimits.MaxTextureDimension2D),
        nameof(GraphicsLimits.MaxTextureArrayLayers),
        nameof(GraphicsLimits.MaxBindGroups),
        nameof(GraphicsLimits.MaxPushConstantBytes),
        nameof(GraphicsLimits.MaxColorTargets),
        nameof(GraphicsLimits.MinUniformBufferOffsetAlignment),
        nameof(GraphicsLimits.MinStorageBufferOffsetAlignment),
        nameof(GraphicsLimits.MaxSamplerAnisotropy),
        nameof(GraphicsLimits.ColorSampleCounts),
        nameof(GraphicsLimits.TimestampPeriodNanoseconds),
    ];

    public static TheoryData<string> Fields() => [.. _fields];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Equals_ComparesEveryField(string field)
    {
        GraphicsLimits changed = field switch
        {
            nameof(GraphicsLimits.MaxTextureDimension2D) => _limits with { MaxTextureDimension2D = 16384 },
            nameof(GraphicsLimits.MaxTextureArrayLayers) => _limits with { MaxTextureArrayLayers = 256 },
            nameof(GraphicsLimits.MaxBindGroups) => _limits with { MaxBindGroups = 4 },
            nameof(GraphicsLimits.MaxPushConstantBytes) => _limits with { MaxPushConstantBytes = 128 },
            nameof(GraphicsLimits.MaxColorTargets) => _limits with { MaxColorTargets = 4 },
            nameof(GraphicsLimits.MinUniformBufferOffsetAlignment) => _limits with { MinUniformBufferOffsetAlignment = 256 },
            nameof(GraphicsLimits.MinStorageBufferOffsetAlignment) => _limits with { MinStorageBufferOffsetAlignment = 32 },
            nameof(GraphicsLimits.MaxSamplerAnisotropy) => _limits with { MaxSamplerAnisotropy = 16f },
            nameof(GraphicsLimits.ColorSampleCounts) => _limits with { ColorSampleCounts = SampleCounts.X1 },
            nameof(GraphicsLimits.TimestampPeriodNanoseconds) => _limits with { TimestampPeriodNanoseconds = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        Assert.NotEqual(_limits, changed);
        Assert.True(_limits != changed);
    }

    [Fact]
    public void Equals_FieldRows_CoverEveryProperty()
    {
        Assert.Equal(
            typeof(GraphicsLimits).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal),
            _fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Equals_SameFields_AreEqual()
    {
        GraphicsLimits copy = _limits with { };

        Assert.Equal(_limits, copy);
        Assert.Equal(_limits.GetHashCode(), copy.GetHashCode());
        Assert.Equal(0, default(GraphicsLimits).MaxTextureDimension2D);
    }
}
