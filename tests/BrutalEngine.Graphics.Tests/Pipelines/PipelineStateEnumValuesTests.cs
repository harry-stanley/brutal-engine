using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>
/// The pipeline state enums' names, values and flags, written from docs/design/graphics.md section 7 (the flag values of
/// <see cref="ColorWriteMask"/> are Vulkan's <c>VkColorComponentFlagBits</c>).
/// </summary>
public sealed class PipelineStateEnumValuesTests
{
    [Fact]
    public void BlendFactor_Values_MatchDesign() => AssertValues<BlendFactor>(
        flags: false,
        ("Zero", 0), ("One", 1), ("SourceColor", 2), ("OneMinusSourceColor", 3), ("SourceAlpha", 4), ("OneMinusSourceAlpha", 5),
        ("DestinationColor", 6), ("OneMinusDestinationColor", 7), ("DestinationAlpha", 8), ("OneMinusDestinationAlpha", 9));

    [Fact]
    public void BlendOperation_Values_MatchDesign() => AssertValues<BlendOperation>(
        flags: false, ("Add", 0), ("Subtract", 1), ("ReverseSubtract", 2), ("Min", 3), ("Max", 4));

    [Fact]
    public void ColorWriteMask_Values_MatchDesign() => AssertValues<ColorWriteMask>(
        flags: true, ("None", 0), ("Red", 1), ("Green", 2), ("Blue", 4), ("Alpha", 8), ("All", 15));

    [Fact]
    public void CullMode_Values_MatchDesign() => AssertValues<CullMode>(flags: false, ("None", 0), ("Front", 1), ("Back", 2));

    [Fact]
    public void FrontFace_Values_MatchDesign() => AssertValues<FrontFace>(flags: false, ("CounterClockwise", 0), ("Clockwise", 1));

    [Fact]
    public void PrimitiveTopology_Values_MatchDesign() => AssertValues<PrimitiveTopology>(
        flags: false, ("TriangleList", 0), ("TriangleStrip", 1), ("LineList", 2), ("LineStrip", 3));

    [Fact]
    public void VertexStepMode_Values_MatchDesign() => AssertValues<VertexStepMode>(flags: false, ("Vertex", 0), ("Instance", 1));

    [Fact]
    public void VertexFormat_Values_MatchDesign() => AssertValues<VertexFormat>(
        flags: false,
        ("Float32", 0), ("Float32x2", 1), ("Float32x3", 2), ("Float32x4", 3), ("Float16x2", 4), ("Float16x4", 5),
        ("Unorm8x4", 6), ("Uint8x4", 7), ("Uint32", 8), ("Uint32x2", 9), ("Sint32", 10));

    private static void AssertValues<T>(bool flags, params (string Name, int Value)[] expected)
        where T : struct, Enum
    {
        Assert.Equal(flags, typeof(T).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(T)));
        Assert.Equal(expected, Enum.GetValues<T>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture))));
    }
}
