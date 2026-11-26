using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The buffer and texture enums' names, values and flags (docs/design/graphics.md section 5).</summary>
public sealed class ResourceEnumValuesTests
{
    [Fact]
    public void BufferUsage_Values_MatchDesign()
    {
        Assert.True(typeof(BufferUsage).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(BufferUsage)));
        Assert.Equal(
            [
                ("None", 0), ("Vertex", 1), ("Index", 2), ("Uniform", 4), ("Storage", 8), ("Indirect", 16),
                ("CopySource", 32), ("CopyDestination", 64),
            ],
            Enum.GetValues<BufferUsage>().Select(v => (v.ToString(), (int)v)));
    }

    [Fact]
    public void MemoryIntent_Values_MatchDesign()
    {
        Assert.False(typeof(MemoryIntent).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(MemoryIntent)));
        Assert.Equal(
            [("GpuOnly", 0), ("Upload", 1), ("Readback", 2)],
            Enum.GetValues<MemoryIntent>().Select(v => (v.ToString(), (int)v)));
    }

    [Fact]
    public void TextureUsage_Values_MatchDesign()
    {
        Assert.True(typeof(TextureUsage).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(TextureUsage)));
        Assert.Equal(
            [
                ("None", 0), ("Sampled", 1), ("Storage", 2), ("ColorTarget", 4), ("DepthStencilTarget", 8),
                ("CopySource", 16), ("CopyDestination", 32),
            ],
            Enum.GetValues<TextureUsage>().Select(v => (v.ToString(), (int)v)));
    }

    [Fact]
    public void TextureFormat_Values_MatchDesign()
    {
        Assert.False(typeof(TextureFormat).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(TextureFormat)));
        Assert.Equal(
            [
                ("Undefined", 0), ("R8Unorm", 1), ("Rg8Unorm", 2), ("Rgba8Unorm", 3), ("Rgba8UnormSrgb", 4),
                ("Bgra8Unorm", 5), ("Bgra8UnormSrgb", 6), ("R16Float", 7), ("Rg16Float", 8), ("Rgba16Float", 9),
                ("R32Float", 10), ("Rg32Float", 11), ("Rgba32Float", 12), ("R32Uint", 13), ("Rgba8Uint", 14),
                ("Depth16Unorm", 15), ("Depth32Float", 16),
            ],
            Enum.GetValues<TextureFormat>().Select(v => (v.ToString(), (int)v)));
    }
}
