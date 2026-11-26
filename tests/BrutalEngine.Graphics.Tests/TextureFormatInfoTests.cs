using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The internal per-format table (docs/design/graphics.md sections 5 and 6).</summary>
public sealed class TextureFormatInfoTests
{
    // One row per concrete format: texel size in bytes, depth, sRGB. Table_CoversEveryFormat keeps it complete.
    private static readonly (TextureFormat Format, int TexelSize, bool IsDepth, bool IsSrgb)[] _rows =
    [
        (TextureFormat.R8Unorm, 1, false, false),
        (TextureFormat.Rg8Unorm, 2, false, false),
        (TextureFormat.Rgba8Unorm, 4, false, false),
        (TextureFormat.Rgba8UnormSrgb, 4, false, true),
        (TextureFormat.Bgra8Unorm, 4, false, false),
        (TextureFormat.Bgra8UnormSrgb, 4, false, true),
        (TextureFormat.R16Float, 2, false, false),
        (TextureFormat.Rg16Float, 4, false, false),
        (TextureFormat.Rgba16Float, 8, false, false),
        (TextureFormat.R32Float, 4, false, false),
        (TextureFormat.Rg32Float, 8, false, false),
        (TextureFormat.Rgba32Float, 16, false, false),
        (TextureFormat.R32Uint, 4, false, false),
        (TextureFormat.Rgba8Uint, 4, false, false),
        (TextureFormat.Depth16Unorm, 2, true, false),
        (TextureFormat.Depth32Float, 4, true, false),
    ];

    public static TheoryData<TextureFormat, int, bool, bool> Rows()
    {
        TheoryData<TextureFormat, int, bool, bool> data = [];
        foreach ((TextureFormat format, int texelSize, bool isDepth, bool isSrgb) in _rows)
        {
            data.Add(format, texelSize, isDepth, isSrgb);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Table(TextureFormat format, int texelSize, bool isDepth, bool isSrgb)
    {
        Assert.Equal(texelSize, TextureFormatInfo.TexelSize(format));
        Assert.Equal(isDepth, TextureFormatInfo.IsDepth(format));
        Assert.Equal(isSrgb, TextureFormatInfo.IsSrgb(format));
    }

    [Fact]
    public void Table_CoversEveryFormat()
    {
        Assert.Equal(16, _rows.Length);
        Assert.Equal(
            Enum.GetValues<TextureFormat>().Where(f => f != TextureFormat.Undefined),
            _rows.Select(r => r.Format));
    }

    [Theory]
    [InlineData(TextureFormat.Undefined)]
    [InlineData((TextureFormat)17)]
    [InlineData((TextureFormat)(-1))]
    public void NotConcreteFormat_Throws(TextureFormat format)
    {
        Assert.Equal("format", Assert.Throws<ArgumentOutOfRangeException>(() => TextureFormatInfo.TexelSize(format)).ParamName);
        Assert.Equal("format", Assert.Throws<ArgumentOutOfRangeException>(() => TextureFormatInfo.IsDepth(format)).ParamName);
        Assert.Equal("format", Assert.Throws<ArgumentOutOfRangeException>(() => TextureFormatInfo.IsSrgb(format)).ParamName);
    }
}
