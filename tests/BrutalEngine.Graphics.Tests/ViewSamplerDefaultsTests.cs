using System;
using System.Linq;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The texture-view and sampler descriptions' defaults and their enums' values (docs/design/graphics.md sections 5 and 7).</summary>
public sealed class ViewSamplerDefaultsTests
{
    [Fact]
    public void Defaults_MatchDesign()
    {
        var texture = new TextureHandle(new Handle(3, 1));
        var view = new TextureViewDescription { Texture = texture };
        var sampler = new SamplerDescription();

        Assert.Equal(texture, view.Texture);
        Assert.Equal(TextureViewDimension.Texture2D, view.Dimension);
        Assert.Equal(0, view.BaseMipLevel);
        Assert.Equal(1, view.MipLevelCount);
        Assert.Equal(0, view.BaseArrayLayer);
        Assert.Equal(1, view.ArrayLayerCount);
        Assert.Null(view.DebugName);

        Assert.Equal(Filter.Nearest, sampler.MinFilter);
        Assert.Equal(Filter.Nearest, sampler.MagFilter);
        Assert.Equal(Filter.Nearest, sampler.MipmapFilter);
        Assert.Equal(AddressMode.ClampToEdge, sampler.AddressU);
        Assert.Equal(AddressMode.ClampToEdge, sampler.AddressV);
        Assert.Equal(BorderColor.TransparentBlack, sampler.BorderColor);
        Assert.Equal(1f, sampler.MaxAnisotropy);
        Assert.Null(sampler.Compare);
        Assert.Null(sampler.DebugName);
    }

    [Fact]
    public void DefaultValues_HaveZeroCountsAndAnisotropy()
    {
        // default(T) skips the constructor, so the initializers don't run: the documented, invalid zero values.
        TextureViewDescription view = default;
        SamplerDescription sampler = default;

        Assert.Equal(0, view.MipLevelCount);
        Assert.Equal(0, view.ArrayLayerCount);
        Assert.Equal(0f, sampler.MaxAnisotropy);
    }

    [Fact]
    public void TextureViewDimension_Values_MatchDesign() =>
        AssertValues<TextureViewDimension>(flags: false, [("Texture2D", 0), ("Texture2DArray", 1)]);

    [Fact]
    public void Filter_Values_MatchDesign() => AssertValues<Filter>(flags: false, [("Nearest", 0), ("Linear", 1)]);

    [Fact]
    public void AddressMode_Values_MatchDesign() =>
        AssertValues<AddressMode>(flags: false, [("ClampToEdge", 0), ("Repeat", 1), ("MirrorRepeat", 2), ("ClampToBorder", 3)]);

    [Fact]
    public void BorderColor_Values_MatchDesign() =>
        AssertValues<BorderColor>(flags: false, [("TransparentBlack", 0), ("OpaqueBlack", 1), ("OpaqueWhite", 2)]);

    [Fact]
    public void CompareFunction_Values_MatchDesign() =>
        AssertValues<CompareFunction>(
            flags: false,
            [
                ("Never", 0), ("Less", 1), ("Equal", 2), ("LessOrEqual", 3), ("Greater", 4), ("NotEqual", 5),
                ("GreaterOrEqual", 6), ("Always", 7),
            ]);

    [Fact]
    public void ShaderStages_Values_MatchDesign() =>
        AssertValues<ShaderStages>(flags: true, [("None", 0), ("Vertex", 1), ("Fragment", 2), ("Compute", 4)]);

    private static void AssertValues<T>(bool flags, (string Name, int Value)[] expected)
        where T : struct, Enum
    {
        Assert.Equal(flags, typeof(T).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(T)));
        Assert.Equal(expected, Enum.GetValues<T>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture))));
    }
}
