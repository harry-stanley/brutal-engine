using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The copy and upload enums' names, values and flags, and the row pitch (docs/design/graphics.md section 6).</summary>
public sealed class CopyEnumValuesTests
{
    [Fact]
    public void TextureAspect_Values_MatchDesign()
    {
        Assert.False(typeof(TextureAspect).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(TextureAspect)));
        Assert.Equal([("Color", 0), ("Depth", 1)], Enum.GetValues<TextureAspect>().Select(v => (v.ToString(), (int)v)));
    }

    [Fact]
    public void TransientUsage_Values_MatchDesign()
    {
        Assert.True(typeof(TransientUsage).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(TransientUsage)));
        Assert.Equal(
            [("Vertex", 1), ("Index", 2), ("Uniform", 4), ("Storage", 8), ("Indirect", 16)],
            Enum.GetValues<TransientUsage>().Select(v => (v.ToString(), (int)v)));
    }

    [Fact]
    public void TextureBufferCopy_RowPitchAlignment_Is256()
    {
        Assert.Equal(256, TextureBufferCopy.RowPitchAlignment);
    }
}
