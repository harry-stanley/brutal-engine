using System;
using System.Globalization;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The resource validator's buffer, texture, view and sampler rules (docs/design/graphics.md section 5).</summary>
public sealed class ResourceValidatorTests
{
    private const string DebugName = "probe";

    private static readonly BufferDescription _buffer = new() { Size = 256, Usage = BufferUsage.Vertex, DebugName = DebugName };

    // 256x128 has a full mip chain of floor(log2(256)) + 1 = 9 levels.
    private static readonly TextureDescription _texture = new()
    {
        Format = TextureFormat.Rgba8Unorm,
        Width = 256,
        Height = 128,
        Usage = TextureUsage.Sampled,
        DebugName = DebugName,
    };

    private static readonly GraphicsLimits _limits = new() { MaxTextureDimension2D = 4096, MaxTextureArrayLayers = 256 };

    // Limits that never bind, for the mip-chain property over every side length.
    private static readonly GraphicsLimits _unlimited = new() { MaxTextureDimension2D = int.MaxValue, MaxTextureArrayLayers = int.MaxValue };

    public static TheoryData<string> InvalidBuffers() =>
    [
        "SizeZero", "SizeNegative", "SizeMinValue", "UsageNone", "UsageUndefinedFlag", "UsageSignBit", "MemoryAboveLast",
        "MemoryNegative", "ReadbackWithVertex", "ReadbackWithCopySource", "ReadbackWithCopyDestinationAndVertex",
    ];

    [Theory]
    [MemberData(nameof(InvalidBuffers))]
    public void Buffer_Invalid_Throws(string rule)
    {
        (BufferDescription description, Type exception, string field) = rule switch
        {
            "SizeZero" => (_buffer with { Size = 0 }, typeof(ArgumentOutOfRangeException), "Size"),
            "SizeNegative" => (_buffer with { Size = -1 }, typeof(ArgumentOutOfRangeException), "Size"),
            "SizeMinValue" => (_buffer with { Size = long.MinValue }, typeof(ArgumentOutOfRangeException), "Size"),
            "UsageNone" => (_buffer with { Usage = BufferUsage.None }, typeof(ArgumentException), "Usage"),
            "UsageUndefinedFlag" => (_buffer with { Usage = BufferUsage.Vertex | (BufferUsage)128 }, typeof(ArgumentOutOfRangeException), "Usage"),
            "UsageSignBit" => (_buffer with { Usage = (BufferUsage)int.MinValue }, typeof(ArgumentOutOfRangeException), "Usage"),
            "MemoryAboveLast" => (_buffer with { Memory = (MemoryIntent)3 }, typeof(ArgumentOutOfRangeException), "Memory"),
            "MemoryNegative" => (_buffer with { Memory = (MemoryIntent)(-1) }, typeof(ArgumentOutOfRangeException), "Memory"),
            "ReadbackWithVertex" => (_buffer with { Memory = MemoryIntent.Readback }, typeof(ArgumentException), "Usage"),
            "ReadbackWithCopySource" => (_buffer with { Memory = MemoryIntent.Readback, Usage = BufferUsage.CopySource }, typeof(ArgumentException), "Usage"),
            "ReadbackWithCopyDestinationAndVertex" => (
                _buffer with { Memory = MemoryIntent.Readback, Usage = BufferUsage.CopyDestination | BufferUsage.Vertex },
                typeof(ArgumentException),
                "Usage"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };

        AssertRejected(exception, "BufferDescription." + field, () => ResourceValidator.Validate(description));
    }

    public static TheoryData<string> ValidBuffers() =>
        ["SizeOne", "SizeMaxValue", "EveryUsage", "ReadbackCopyDestination", "UploadUniform", "NoDebugName"];

    [Theory]
    [MemberData(nameof(ValidBuffers))]
    public void Buffer_Valid_DoesNotThrow(string row)
    {
        BufferDescription description = row switch
        {
            "SizeOne" => _buffer with { Size = 1 },
            "SizeMaxValue" => _buffer with { Size = long.MaxValue },
            "EveryUsage" => _buffer with { Usage = (BufferUsage)127 },
            "ReadbackCopyDestination" => _buffer with { Memory = MemoryIntent.Readback, Usage = BufferUsage.CopyDestination },
            "UploadUniform" => _buffer with { Memory = MemoryIntent.Upload, Usage = BufferUsage.Uniform },
            "NoDebugName" => _buffer with { DebugName = null },
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        ResourceValidator.Validate(description);
    }

    public static TheoryData<string> InvalidTextures() =>
    [
        "FormatUndefined", "FormatAboveLast", "FormatNegative", "WidthZero", "WidthNegative", "HeightZero", "HeightMinValue",
        "ArrayLayersZero", "ArrayLayersNegative", "MipLevelsZero", "MipLevelsNegative", "MipLevelsAboveChainWide", "MipLevelsAboveChainTall",
        "SampleCountZero", "SampleCountThree", "SampleCountSix", "SampleCountThirtyTwo", "SampleCountMinValue",
        "SampleCountWithMips", "SampleCountWithLayers", "UsageNone", "UsageUndefinedFlag", "UsageSignBit", "WidthAboveLimit",
        "HeightAboveLimit", "ArrayLayersAboveLimit",
    ];

    [Theory]
    [MemberData(nameof(InvalidTextures))]
    public void Texture_Invalid_Throws(string rule)
    {
        Type outOfRange = typeof(ArgumentOutOfRangeException);
        Type invalid = typeof(ArgumentException);
        (TextureDescription description, Type exception, string field) = rule switch
        {
            "FormatUndefined" => (_texture with { Format = TextureFormat.Undefined }, invalid, "Format"),
            "FormatAboveLast" => (_texture with { Format = (TextureFormat)17 }, outOfRange, "Format"),
            "FormatNegative" => (_texture with { Format = (TextureFormat)(-1) }, outOfRange, "Format"),
            "WidthZero" => (_texture with { Width = 0 }, outOfRange, "Width"),
            "WidthNegative" => (_texture with { Width = -1 }, outOfRange, "Width"),
            "HeightZero" => (_texture with { Height = 0 }, outOfRange, "Height"),
            "HeightMinValue" => (_texture with { Height = int.MinValue }, outOfRange, "Height"),
            "ArrayLayersZero" => (_texture with { ArrayLayers = 0 }, outOfRange, "ArrayLayers"),
            "ArrayLayersNegative" => (_texture with { ArrayLayers = -1 }, outOfRange, "ArrayLayers"),
            "MipLevelsZero" => (_texture with { MipLevels = 0 }, outOfRange, "MipLevels"),
            "MipLevelsNegative" => (_texture with { MipLevels = -1 }, outOfRange, "MipLevels"),
            "MipLevelsAboveChainWide" => (_texture with { MipLevels = 10 }, outOfRange, "MipLevels"),
            "MipLevelsAboveChainTall" => (_texture with { Width = 1, Height = 256, MipLevels = 10 }, outOfRange, "MipLevels"),
            "SampleCountZero" => (_texture with { SampleCount = 0 }, outOfRange, "SampleCount"),
            "SampleCountThree" => (_texture with { SampleCount = 3 }, outOfRange, "SampleCount"),
            "SampleCountSix" => (_texture with { SampleCount = 6 }, outOfRange, "SampleCount"),
            "SampleCountThirtyTwo" => (_texture with { SampleCount = 32 }, outOfRange, "SampleCount"),
            "SampleCountMinValue" => (_texture with { SampleCount = int.MinValue }, outOfRange, "SampleCount"),
            "SampleCountWithMips" => (_texture with { SampleCount = 4, MipLevels = 2 }, invalid, "SampleCount"),
            "SampleCountWithLayers" => (_texture with { SampleCount = 4, ArrayLayers = 2 }, invalid, "SampleCount"),
            "UsageNone" => (_texture with { Usage = TextureUsage.None }, invalid, "Usage"),
            "UsageUndefinedFlag" => (_texture with { Usage = TextureUsage.Sampled | (TextureUsage)64 }, outOfRange, "Usage"),
            "UsageSignBit" => (_texture with { Usage = (TextureUsage)int.MinValue }, outOfRange, "Usage"),
            "WidthAboveLimit" => (_texture with { Width = 4097 }, outOfRange, "Width"),
            "HeightAboveLimit" => (_texture with { Height = 4097 }, outOfRange, "Height"),
            "ArrayLayersAboveLimit" => (_texture with { ArrayLayers = 257 }, outOfRange, "ArrayLayers"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };

        AssertRejected(exception, "TextureDescription." + field, () => ResourceValidator.Validate(description, _limits, GraphicsFeatures.None));
    }

    public static TheoryData<string> ValidTextures() =>
    [
        "OneByOne", "AtLimits", "FullMipChain", "TwoSamples", "FourSamples", "EightSamples", "SixteenSamples", "Depth",
        "EveryUsage", "NoDebugName",
    ];

    [Theory]
    [MemberData(nameof(ValidTextures))]
    public void Texture_Valid_DoesNotThrow(string row)
    {
        TextureDescription description = row switch
        {
            "OneByOne" => _texture with { Width = 1, Height = 1 },
            // 4096 = 2^12, so 13 levels is the full chain.
            "AtLimits" => _texture with { Width = 4096, Height = 4096, ArrayLayers = 256, MipLevels = 13 },
            "FullMipChain" => _texture with { MipLevels = 9 },
            "TwoSamples" => _texture with { SampleCount = 2, Usage = TextureUsage.ColorTarget },
            "FourSamples" => _texture with { SampleCount = 4, Usage = TextureUsage.ColorTarget },
            "EightSamples" => _texture with { SampleCount = 8, Usage = TextureUsage.ColorTarget },
            "SixteenSamples" => _texture with { SampleCount = 16, Usage = TextureUsage.ColorTarget },
            "Depth" => _texture with { Format = TextureFormat.Depth32Float, Usage = TextureUsage.DepthStencilTarget },
            "EveryUsage" => _texture with { Usage = (TextureUsage)63 },
            "NoDebugName" => _texture with { DebugName = null },
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        ResourceValidator.Validate(description, _limits, GraphicsFeatures.None);
    }

    [Fact]
    public void Texture_InvalidWithoutDebugName_SaysSo()
    {
        TextureDescription description = _texture with { Width = 0, DebugName = null };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ResourceValidator.Validate(description, _limits, GraphicsFeatures.None));

        Assert.Contains("TextureDescription.Width", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no debug name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MipCount_MatchesReference()
    {
        // Half the sides sit on a power-of-two boundary (2^k - 1, 2^k, 2^k + 1, clamped to 1..int.MaxValue).
        var boundary = Gen.Select(Gen.Int[0, 31], Gen.Int[-1, 1], (k, d) => (int)Math.Clamp((1L << k) + d, 1, int.MaxValue));
        var side = Gen.OneOf(boundary, Gen.Int[1, int.MaxValue]);

        Gen.Select(side, side).Sample((width, height) => AssertMipChainBoundary(width, height));
    }

    [Fact]
    public void MipCount_AtEveryPowerOfTwoBoundary_MatchesReference()
    {
        for (int k = 0; k <= 31; k++)
        {
            for (long d = -1; d <= 1; d++)
            {
                int side = (int)Math.Clamp((1L << k) + d, 1, int.MaxValue);
                AssertMipChainBoundary(side, 1);
                AssertMipChainBoundary(1, side);
                AssertMipChainBoundary(side, side);
            }
        }
    }

    [Fact]
    public void ValidDescription_DoesNotAllocate()
    {
        BufferDescription buffer = _buffer with { Memory = MemoryIntent.Readback, Usage = BufferUsage.CopyDestination };
        TextureDescription texture = _texture with { ArrayLayers = 4, MipLevels = 9 };
        GraphicsLimits limits = _limits;
        ValidateBoth(buffer, texture, limits);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            ValidateBoth(buffer, texture, limits);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    // A 4-layer texture with a full 9-level chain, so view ranges can be off by one at both ends.
    private static readonly TextureDescription _viewTarget = _texture with { ArrayLayers = 4, MipLevels = 9 };

    private static readonly TextureViewDescription _view = new()
    {
        Texture = new TextureHandle(new Handle(1, 1)),
        Dimension = TextureViewDimension.Texture2DArray,
        DebugName = DebugName,
    };

    private static readonly SamplerDescription _sampler = new() { DebugName = DebugName };

    private static readonly GraphicsLimits _anisotropyLimits = new() { MaxSamplerAnisotropy = 16 };

    public static TheoryData<string> ViewsOutsideTexture() =>
    [
        "BaseMipNegative", "BaseMipAtCount", "MipCountZero", "MipCountNegative", "MipCountPastEnd", "MipCountPastEndFromBase",
        "MipCountMaxValue", "BaseLayerNegative", "BaseLayerAtCount", "LayerCountZero", "LayerCountPastEnd",
        "LayerCountPastEndFromBase", "LayerCountMaxValue",
    ];

    [Theory]
    [MemberData(nameof(ViewsOutsideTexture))]
    public void View_OutsideTexture_Throws(string rule)
    {
        (TextureViewDescription description, string field) = rule switch
        {
            "BaseMipNegative" => (_view with { BaseMipLevel = -1 }, "BaseMipLevel"),
            "BaseMipAtCount" => (_view with { BaseMipLevel = 9 }, "BaseMipLevel"),
            "MipCountZero" => (_view with { MipLevelCount = 0 }, "MipLevelCount"),
            "MipCountNegative" => (_view with { MipLevelCount = -1 }, "MipLevelCount"),
            "MipCountPastEnd" => (_view with { MipLevelCount = 10 }, "MipLevelCount"),
            "MipCountPastEndFromBase" => (_view with { BaseMipLevel = 8, MipLevelCount = 2 }, "MipLevelCount"),
            "MipCountMaxValue" => (_view with { BaseMipLevel = 1, MipLevelCount = int.MaxValue }, "MipLevelCount"),
            "BaseLayerNegative" => (_view with { BaseArrayLayer = -1 }, "BaseArrayLayer"),
            "BaseLayerAtCount" => (_view with { BaseArrayLayer = 4 }, "BaseArrayLayer"),
            "LayerCountZero" => (_view with { ArrayLayerCount = 0 }, "ArrayLayerCount"),
            "LayerCountPastEnd" => (_view with { ArrayLayerCount = 5 }, "ArrayLayerCount"),
            "LayerCountPastEndFromBase" => (_view with { BaseArrayLayer = 3, ArrayLayerCount = 2 }, "ArrayLayerCount"),
            "LayerCountMaxValue" => (_view with { BaseArrayLayer = 1, ArrayLayerCount = int.MaxValue }, "ArrayLayerCount"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };

        AssertRejected(typeof(ArgumentOutOfRangeException), "TextureViewDescription." + field, () => ResourceValidator.Validate(description, _viewTarget));
    }

    public static TheoryData<string> InvalidViews() => ["Texture2DWithTwoLayers", "DimensionAboveLast", "DimensionNegative"];

    [Theory]
    [MemberData(nameof(InvalidViews))]
    public void View_Invalid_Throws(string rule)
    {
        (TextureViewDescription description, Type exception, string field) = rule switch
        {
            "Texture2DWithTwoLayers" => (
                _view with { Dimension = TextureViewDimension.Texture2D, ArrayLayerCount = 2 },
                typeof(ArgumentException),
                "ArrayLayerCount"),
            "DimensionAboveLast" => (_view with { Dimension = (TextureViewDimension)2 }, typeof(ArgumentOutOfRangeException), "Dimension"),
            "DimensionNegative" => (_view with { Dimension = (TextureViewDimension)(-1) }, typeof(ArgumentOutOfRangeException), "Dimension"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };

        AssertRejected(exception, "TextureViewDescription." + field, () => ResourceValidator.Validate(description, _viewTarget));
    }

    public static TheoryData<string> ValidViews() =>
        ["Defaults", "Texture2DOfLastLayer", "EveryMipAndLayer", "LastMip", "ArrayOfOneLayer", "NoDebugName"];

    [Theory]
    [MemberData(nameof(ValidViews))]
    public void View_Valid_DoesNotThrow(string row)
    {
        TextureViewDescription description = row switch
        {
            "Defaults" => new TextureViewDescription { Texture = _view.Texture },
            "Texture2DOfLastLayer" => _view with { Dimension = TextureViewDimension.Texture2D, BaseArrayLayer = 3 },
            "EveryMipAndLayer" => _view with { MipLevelCount = 9, ArrayLayerCount = 4 },
            "LastMip" => _view with { BaseMipLevel = 8 },
            "ArrayOfOneLayer" => _view with { BaseArrayLayer = 2, ArrayLayerCount = 1 },
            "NoDebugName" => _view with { DebugName = null },
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        ResourceValidator.Validate(description, _viewTarget);
    }

    [Fact]
    public void Sampler_AnisotropyWithoutFeature_ThrowsInvalidOperation()
    {
        SamplerDescription description = _sampler with { MaxAnisotropy = 2 };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ResourceValidator.Validate(description, _anisotropyLimits, GraphicsFeatures.TimestampQueries));

        Assert.Contains("SamplerDescription.MaxAnisotropy", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SamplerAnisotropy", exception.Message, StringComparison.Ordinal);
        Assert.Contains("debug name 'probe'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sampler_AnisotropyAboveLimitWithoutFeature_ThrowsInvalidOperation()
    {
        // The feature is checked first: without it, the limit means nothing.
        SamplerDescription description = _sampler with { MaxAnisotropy = 17 };

        Assert.Throws<InvalidOperationException>(() => ResourceValidator.Validate(description, _anisotropyLimits, GraphicsFeatures.None));
    }

    public static TheoryData<string> InvalidSamplers() =>
    [
        "MinFilterAboveLast", "MagFilterAboveLast", "MipmapFilterNegative", "AddressUAboveLast", "AddressVNegative",
        "BorderColorAboveLast", "CompareAboveLast", "CompareNegative", "AnisotropyZero", "AnisotropyNegativeZero",
        "AnisotropyNegative", "AnisotropyNaN", "AnisotropyNegativeInfinity", "AnisotropyAboveLimit", "AnisotropyInfinity",
    ];

    [Theory]
    [MemberData(nameof(InvalidSamplers))]
    public void Sampler_Invalid_Throws(string rule)
    {
        (SamplerDescription description, string field) = rule switch
        {
            "MinFilterAboveLast" => (_sampler with { MinFilter = (Filter)2 }, "MinFilter"),
            "MagFilterAboveLast" => (_sampler with { MagFilter = (Filter)2 }, "MagFilter"),
            "MipmapFilterNegative" => (_sampler with { MipmapFilter = (Filter)(-1) }, "MipmapFilter"),
            "AddressUAboveLast" => (_sampler with { AddressU = (AddressMode)4 }, "AddressU"),
            "AddressVNegative" => (_sampler with { AddressV = (AddressMode)(-1) }, "AddressV"),
            "BorderColorAboveLast" => (_sampler with { BorderColor = (BorderColor)3 }, "BorderColor"),
            "CompareAboveLast" => (_sampler with { Compare = (CompareFunction)8 }, "Compare"),
            "CompareNegative" => (_sampler with { Compare = (CompareFunction)(-1) }, "Compare"),
            "AnisotropyZero" => (_sampler with { MaxAnisotropy = 0 }, "MaxAnisotropy"),
            "AnisotropyNegativeZero" => (_sampler with { MaxAnisotropy = -0f }, "MaxAnisotropy"),
            "AnisotropyNegative" => (_sampler with { MaxAnisotropy = -1 }, "MaxAnisotropy"),
            "AnisotropyNaN" => (_sampler with { MaxAnisotropy = float.NaN }, "MaxAnisotropy"),
            "AnisotropyNegativeInfinity" => (_sampler with { MaxAnisotropy = float.NegativeInfinity }, "MaxAnisotropy"),
            "AnisotropyAboveLimit" => (_sampler with { MaxAnisotropy = MathF.BitIncrement(16f) }, "MaxAnisotropy"),
            "AnisotropyInfinity" => (_sampler with { MaxAnisotropy = float.PositiveInfinity }, "MaxAnisotropy"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };

        AssertRejected(
            typeof(ArgumentOutOfRangeException),
            "SamplerDescription." + field,
            () => ResourceValidator.Validate(description, _anisotropyLimits, GraphicsFeatures.SamplerAnisotropy));
    }

    public static TheoryData<string> ValidSamplers() =>
    [
        "Defaults", "AnisotropyBelowOneWithoutFeature", "AnisotropyOneWithZeroLimit", "AnisotropyAtLimit",
        "AnisotropySmallestAboveOne", "Linear", "ClampToBorderOpaqueWhite", "CompareAlways", "CompareNever", "NoDebugName",
    ];

    [Theory]
    [MemberData(nameof(ValidSamplers))]
    public void Sampler_Valid_DoesNotThrow(string row)
    {
        GraphicsFeatures anisotropic = GraphicsFeatures.SamplerAnisotropy;
        (SamplerDescription description, GraphicsLimits limits, GraphicsFeatures features) = row switch
        {
            "Defaults" => (new SamplerDescription(), _anisotropyLimits, GraphicsFeatures.None),
            "AnisotropyBelowOneWithoutFeature" => (_sampler with { MaxAnisotropy = 0.5f }, _anisotropyLimits, GraphicsFeatures.None),
            // Without the feature the adapter may report a limit of 0; anisotropy 1 needs neither.
            "AnisotropyOneWithZeroLimit" => (_sampler, default(GraphicsLimits), GraphicsFeatures.None),
            "AnisotropyAtLimit" => (_sampler with { MaxAnisotropy = 16 }, _anisotropyLimits, anisotropic),
            "AnisotropySmallestAboveOne" => (_sampler with { MaxAnisotropy = MathF.BitIncrement(1f) }, _anisotropyLimits, anisotropic),
            "Linear" => (_sampler with { MinFilter = Filter.Linear, MagFilter = Filter.Linear, MipmapFilter = Filter.Linear }, _anisotropyLimits, GraphicsFeatures.None),
            "ClampToBorderOpaqueWhite" => (
                _sampler with { AddressU = AddressMode.ClampToBorder, AddressV = AddressMode.ClampToBorder, BorderColor = BorderColor.OpaqueWhite },
                _anisotropyLimits,
                GraphicsFeatures.None),
            "CompareAlways" => (_sampler with { Compare = CompareFunction.Always }, _anisotropyLimits, GraphicsFeatures.None),
            "CompareNever" => (_sampler with { Compare = CompareFunction.Never }, _anisotropyLimits, GraphicsFeatures.None),
            "NoDebugName" => (_sampler with { DebugName = null }, _anisotropyLimits, GraphicsFeatures.None),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        ResourceValidator.Validate(description, limits, features);
    }

    [Fact]
    public void ValidViewAndSampler_DoNotAllocate()
    {
        TextureViewDescription view = _view with { MipLevelCount = 9, ArrayLayerCount = 4 };
        TextureDescription target = _viewTarget;
        SamplerDescription sampler = _sampler with { MaxAnisotropy = 8, Compare = CompareFunction.Less };
        GraphicsLimits limits = _anisotropyLimits;
        ValidateViewAndSampler(view, target, sampler, limits);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            ValidateViewAndSampler(view, target, sampler, limits);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void ValidateViewAndSampler(in TextureViewDescription view, in TextureDescription target, in SamplerDescription sampler, in GraphicsLimits limits)
    {
        ResourceValidator.Validate(view, target);
        ResourceValidator.Validate(sampler, limits, GraphicsFeatures.SamplerAnisotropy);
    }

    private static void ValidateBoth(in BufferDescription buffer, in TextureDescription texture, in GraphicsLimits limits)
    {
        ResourceValidator.Validate(buffer);
        ResourceValidator.Validate(texture, limits, GraphicsFeatures.None);
    }

    private static void AssertMipChainBoundary(int width, int height)
    {
        int expected = ReferenceMipLevels(width, height);
        TextureDescription full = _texture with { Width = width, Height = height, MipLevels = expected };
        TextureDescription over = full with { MipLevels = expected + 1 };

        ResourceValidator.Validate(full, _unlimited, GraphicsFeatures.None);
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ResourceValidator.Validate(over, _unlimited, GraphicsFeatures.None));
        Assert.Contains("TextureDescription.MipLevels", exception.Message, StringComparison.Ordinal);
        // The message gives this texture's maximum, as the limit messages give their bound.
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"+ 1 ({expected}), but is {expected + 1} "),
            exception.Message,
            StringComparison.Ordinal);
    }

    // Halves the larger side down to 1, counting levels: independent of the validator's Log2.
    private static int ReferenceMipLevels(int width, int height)
    {
        int levels = 1;
        for (long side = Math.Max(width, height); side > 1; side /= 2)
        {
            levels++;
        }

        return levels;
    }

    private static void AssertRejected(Type exceptionType, string field, Action validate)
    {
        // Exact type: an ArgumentOutOfRangeException where an ArgumentException is documented fails, and vice versa.
        var exception = (ArgumentException)Assert.Throws(exceptionType, validate);

        Assert.Equal("description", exception.ParamName);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
        Assert.Contains("debug name 'probe'", exception.Message, StringComparison.Ordinal);
    }
}
