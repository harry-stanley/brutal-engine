using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The texture-location and buffer-texture copy rules (docs/design/graphics.md section 6).</summary>
public sealed partial class CopyValidatorTests
{
    private static readonly TextureHandle _textureA = new(new Handle(1, 1));

    // 64x32 Rgba8 with 4 layers and the full chain of 7 levels: 64x32, 32x16, 16x8, 8x4, 4x2, 2x1, 1x1.
    private static readonly TextureDescription _texture = new()
    {
        Format = TextureFormat.Rgba8Unorm,
        Width = 64,
        Height = 32,
        ArrayLayers = 4,
        MipLevels = 7,
        Usage = TextureUsage.CopySource | TextureUsage.CopyDestination,
        DebugName = DebugName,
    };

    private static readonly TextureDescription _depth = _texture with { Format = TextureFormat.Depth32Float };

    private static readonly TextureDescription _multisampled = _texture with { ArrayLayers = 1, MipLevels = 1, SampleCount = 4 };

    // 4 rows of 16 Rgba8 texels (64 bytes each) at a 256-byte pitch: the buffer needs 3 * 256 + 64 = 832 bytes.
    private static readonly TextureBufferCopy _textureBufferCopy = new()
    {
        Texture = new TextureLocation(_textureA),
        Region = new TextureRegion(0, 0, 16, 4),
        // Its own handle: static initializers in another partial file may not have run yet.
        Buffer = new BufferHandle(new Handle(1, 1)),
        BytesPerRow = 256,
    };

    private static readonly string[] _textureRules =
    [
        "Location.AspectAboveLast", "Location.AspectNegative", "Location.MipNegative", "Location.MipAtCount", "Location.MipMaxValue",
        "Location.LayerNegative", "Location.LayerAtCount", "Location.LayerMaxValue", "Location.DepthAspectOnColor",
        "Location.ColorAspectOnDepth32", "Location.ColorAspectOnDepth16",
        "Region.XNegative", "Region.XAtExtent", "Region.XMaxValue", "Region.WidthZero", "Region.WidthNegative", "Region.WidthPastEnd",
        "Region.WidthMaxValue", "Region.YNegative", "Region.YAtExtent", "Region.YMaxValue", "Region.HeightZero", "Region.HeightNegative",
        "Region.HeightPastEnd", "Region.HeightMaxValue", "Region.LastMipIsOneByOne", "Region.MipHalvesWidth", "Region.MipHalvesHeight",
        "ToBuffer.TextureUsage", "ToBuffer.BufferUsage", "ToBuffer.Multisampled", "ToTexture.BufferUsage", "ToTexture.TextureUsage",
        "ToTexture.Multisampled", "ToTexture.DepthAspect", "ToTexture.Location",
    ];

    [Theory]
    [InlineData(TextureFormat.Depth16Unorm)]
    [InlineData(TextureFormat.Depth32Float)]
    public void DepthFromBuffer_Throws_DepthToBuffer_Passes(TextureFormat format)
    {
        TextureDescription depth = _texture with { Format = format };
        TextureBufferCopy copy = _textureBufferCopy with { Texture = _textureBufferCopy.Texture with { Aspect = TextureAspect.Depth } };

        CopyValidator.ValidateCopyToBuffer(copy, depth, _buffer);
        AssertRejected(typeof(ArgumentException), "copy", "TextureBufferCopy.Texture.Aspect", () => CopyValidator.ValidateCopyToTexture(copy, _buffer, depth));
    }

    private static (Action Pass, Action Fail, Type Exception, string Param, string Field) TextureRow(string rule)
    {
        Type outOfRange = typeof(ArgumentOutOfRangeException);
        Type invalid = typeof(ArgumentException);
        TextureBufferCopy t = _textureBufferCopy;
        TextureLocation at = t.Texture;
        TextureRegion r = t.Region;
        BufferDescription noCopySource = _buffer with { Usage = BufferUsage.Vertex | BufferUsage.CopyDestination };
        BufferDescription noCopyDestination = _buffer with { Usage = BufferUsage.Vertex | BufferUsage.CopySource };
        TextureDescription noTextureSource = _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopyDestination };
        TextureDescription noTextureDestination = _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopySource };
        TextureDescription single = _multisampled with { SampleCount = 1 };
        TextureBufferCopy firstTexel = t with { Region = new TextureRegion(0, 0, 1, 1) };
        return rule switch
        {
            "Location.AspectAboveLast" => (ToBuffer(t), ToBuffer(t with { Texture = at with { Aspect = (TextureAspect)2 } }), outOfRange, "copy", "TextureBufferCopy.Texture.Aspect"),
            "Location.AspectNegative" => (ToBuffer(t), ToBuffer(t with { Texture = at with { Aspect = (TextureAspect)(-1) } }), outOfRange, "copy", "TextureBufferCopy.Texture.Aspect"),
            "Location.MipNegative" => (ToBuffer(t), ToBuffer(t with { Texture = at with { MipLevel = -1 } }), outOfRange, "copy", "TextureBufferCopy.Texture.MipLevel"),
            "Location.MipAtCount" => (ToBuffer(AtMip(6, 1, 1)), ToBuffer(AtMip(7, 1, 1)), outOfRange, "copy", "TextureBufferCopy.Texture.MipLevel"),
            "Location.MipMaxValue" => (ToBuffer(AtMip(6, 1, 1)), ToBuffer(AtMip(int.MaxValue, 1, 1)), outOfRange, "copy", "TextureBufferCopy.Texture.MipLevel"),
            "Location.LayerNegative" => (ToBuffer(t), ToBuffer(t with { Texture = at with { ArrayLayer = -1 } }), outOfRange, "copy", "TextureBufferCopy.Texture.ArrayLayer"),
            "Location.LayerAtCount" => (
                ToBuffer(t with { Texture = at with { ArrayLayer = 3 } }),
                ToBuffer(t with { Texture = at with { ArrayLayer = 4 } }),
                outOfRange, "copy", "TextureBufferCopy.Texture.ArrayLayer"),
            "Location.LayerMaxValue" => (ToBuffer(t), ToBuffer(t with { Texture = at with { ArrayLayer = int.MaxValue } }), outOfRange, "copy", "TextureBufferCopy.Texture.ArrayLayer"),
            "Location.DepthAspectOnColor" => (ToBuffer(t), ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }), invalid, "copy", "TextureBufferCopy.Texture.Aspect"),
            "Location.ColorAspectOnDepth32" => (
                ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _depth),
                ToBuffer(t, _depth),
                invalid, "copy", "TextureBufferCopy.Texture.Aspect"),
            "Location.ColorAspectOnDepth16" => (
                ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _texture with { Format = TextureFormat.Depth16Unorm }),
                ToBuffer(t, _texture with { Format = TextureFormat.Depth16Unorm }),
                invalid, "copy", "TextureBufferCopy.Texture.Aspect"),

            "Region.XNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { X = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.X"),
            "Region.XAtExtent" => (ToBuffer(t with { Region = r with { X = 63, Width = 1 } }), ToBuffer(t with { Region = r with { X = 64, Width = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.X"),
            "Region.XMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { X = int.MaxValue, Width = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.X"),
            "Region.WidthZero" => (ToBuffer(t with { Region = r with { Width = 1 } }), ToBuffer(t with { Region = r with { Width = 0 } }), outOfRange, "copy", "TextureBufferCopy.Region.Width"),
            "Region.WidthNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { Width = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Width"),
            "Region.WidthPastEnd" => (ToBuffer(t with { Region = r with { X = 48 } }), ToBuffer(t with { Region = r with { X = 49 } }), outOfRange, "copy", "TextureBufferCopy.Region.Width"),
            "Region.WidthMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { X = 1, Width = int.MaxValue } }), outOfRange, "copy", "TextureBufferCopy.Region.Width"),
            "Region.YNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { Y = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Y"),
            "Region.YAtExtent" => (ToBuffer(t with { Region = r with { Y = 31, Height = 1 } }), ToBuffer(t with { Region = r with { Y = 32, Height = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Y"),
            "Region.YMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { Y = int.MaxValue, Height = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Y"),
            "Region.HeightZero" => (ToBuffer(t with { Region = r with { Height = 1 } }), ToBuffer(t with { Region = r with { Height = 0 } }), outOfRange, "copy", "TextureBufferCopy.Region.Height"),
            "Region.HeightNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { Height = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Height"),
            "Region.HeightPastEnd" => (ToBuffer(t with { Region = r with { Y = 28 } }), ToBuffer(t with { Region = r with { Y = 29 } }), outOfRange, "copy", "TextureBufferCopy.Region.Height"),
            "Region.HeightMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { Y = 1, Height = int.MaxValue } }), outOfRange, "copy", "TextureBufferCopy.Region.Height"),
            // Mip 6 is 1x1: max(1, 32 >> 6) is 1 high, where the bare shift gives 0. Mip 5 is 2x1, mip 3 is 8x4.
            "Region.LastMipIsOneByOne" => (ToBuffer(AtMip(6, 1, 1)), ToBuffer(AtMip(6, 2, 1)), outOfRange, "copy", "TextureBufferCopy.Region.Width"),
            "Region.MipHalvesWidth" => (ToBuffer(AtMip(5, 2, 1)), ToBuffer(AtMip(5, 3, 1)), outOfRange, "copy", "TextureBufferCopy.Region.Width"),
            "Region.MipHalvesHeight" => (ToBuffer(AtMip(3, 1, 4)), ToBuffer(AtMip(3, 1, 5)), outOfRange, "copy", "TextureBufferCopy.Region.Height"),

            "ToBuffer.TextureUsage" => (ToBuffer(t), ToBuffer(t, noTextureSource), invalid, "copy", "TextureBufferCopy.Texture"),
            "ToBuffer.BufferUsage" => (ToBuffer(t), ToBuffer(t, _texture, noCopyDestination), invalid, "copy", "TextureBufferCopy.Buffer"),
            "ToBuffer.Multisampled" => (ToBuffer(firstTexel, single), ToBuffer(firstTexel, _multisampled), invalid, "copy", "TextureBufferCopy.Texture"),
            "ToTexture.BufferUsage" => (ToTexture(t), ToTexture(t, noCopySource, _texture), invalid, "copy", "TextureBufferCopy.Buffer"),
            "ToTexture.TextureUsage" => (ToTexture(t), ToTexture(t, _buffer, noTextureDestination), invalid, "copy", "TextureBufferCopy.Texture"),
            "ToTexture.Multisampled" => (ToTexture(firstTexel, _buffer, single), ToTexture(firstTexel, _buffer, _multisampled), invalid, "copy", "TextureBufferCopy.Texture"),
            "ToTexture.DepthAspect" => (
                ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _depth),
                ToTexture(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _buffer, _depth),
                invalid, "copy", "TextureBufferCopy.Texture.Aspect"),
            // The location rules hold in this direction too.
            "ToTexture.Location" => (ToTexture(AtMip(6, 1, 1)), ToTexture(AtMip(6, 1, 1) with { Region = new TextureRegion(0, 1, 1, 1) }), outOfRange, "copy", "TextureBufferCopy.Region.Y"),

            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }

    [Fact]
    public void TextureMessages_ReportTheComputedBound()
    {
        ArgumentOutOfRangeException width = Assert.Throws<ArgumentOutOfRangeException>(
            () => CopyValidator.ValidateCopyToBuffer(AtMip(1, 33, 1), _texture, _buffer));

        Assert.Contains("width after X (32), but is 33", width.Message, StringComparison.Ordinal);
    }

    private static TextureBufferCopy AtMip(int mip, int width, int height) => _textureBufferCopy with
    {
        Texture = new TextureLocation(_textureA, MipLevel: mip),
        Region = new TextureRegion(0, 0, width, height),
    };

    private static Action ToBuffer(TextureBufferCopy copy) => ToBuffer(copy, _texture, _buffer);

    private static Action ToBuffer(TextureBufferCopy copy, TextureDescription texture) => ToBuffer(copy, texture, _buffer);

    private static Action ToBuffer(TextureBufferCopy copy, TextureDescription texture, BufferDescription buffer) =>
        () => CopyValidator.ValidateCopyToBuffer(copy, texture, buffer);

    private static Action ToTexture(TextureBufferCopy copy) => ToTexture(copy, _buffer, _texture);

    private static Action ToTexture(TextureBufferCopy copy, BufferDescription buffer, TextureDescription texture) =>
        () => CopyValidator.ValidateCopyToTexture(copy, buffer, texture);
}
