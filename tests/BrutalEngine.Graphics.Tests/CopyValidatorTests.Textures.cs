using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The texture-location and buffer-texture copy rules (docs/design/graphics.md section 6).</summary>
public sealed partial class CopyValidatorTests
{
    private const string TextureName = "tex";
    private const string BufferName = "buf";

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
        DebugName = TextureName,
    };

    // The texture rows' buffer, named apart from the texture so each message must name the resource its field
    // describes: the texture for its location, region and usage, the buffer for its usage, BytesPerRow and BufferOffset.
    private static readonly BufferDescription _texBuffer = new()
    {
        Size = 1024,
        Usage = BufferUsage.CopySource | BufferUsage.CopyDestination,
        DebugName = BufferName,
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
        "TextureBuffer.BytesPerRowBelowRow", "TextureBuffer.BytesPerRowBelowTexelRow", "TextureBuffer.BytesPerRowOneUnder", "TextureBuffer.BytesPerRowNegative", "TextureBuffer.BytesPerRowMaxValue",
        "TextureBuffer.BytesPerRowAligned", "TextureBuffer.BufferOffsetNegative", "TextureBuffer.BufferOffsetMinValue",
        "TextureBuffer.BufferOffsetAlignedToFour", "TextureBuffer.BufferOffsetAlignedToTexel", "TextureBuffer.BufferHoldsRows",
        "TextureBuffer.BufferOffsetPastEnd", "TextureBuffer.BufferOffsetMaxValue",
    ];

    [Theory]
    [InlineData(TextureFormat.Depth16Unorm)]
    [InlineData(TextureFormat.Depth32Float)]
    public void DepthFromBuffer_Throws_DepthToBuffer_Passes(TextureFormat format)
    {
        TextureDescription depth = _texture with { Format = format };
        TextureBufferCopy copy = _textureBufferCopy with { Texture = _textureBufferCopy.Texture with { Aspect = TextureAspect.Depth } };

        CopyValidator.ValidateCopyToBuffer(copy, depth, _texBuffer);
        AssertRejected(typeof(ArgumentException), "copy", "TextureBufferCopy.Texture.Aspect", () => CopyValidator.ValidateCopyToTexture(copy, _texBuffer, depth), TextureName);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(384)]
    [InlineData(511)]
    public void RowPitch_Not256Multiple_Throws(int bytesPerRow)
    {
        // Each pitch holds the region's 64-byte rows, so only the multiple-of-256 rule rejects it, in both directions.
        TextureBufferCopy copy = _textureBufferCopy with { BytesPerRow = bytesPerRow };

        AssertRejected(typeof(ArgumentException), "copy", "TextureBufferCopy.BytesPerRow", () => CopyValidator.ValidateCopyToBuffer(copy, _texture, _texBuffer), BufferName);
        AssertRejected(typeof(ArgumentException), "copy", "TextureBufferCopy.BytesPerRow", () => CopyValidator.ValidateCopyToTexture(copy, _texBuffer, _texture), BufferName);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(768)]
    public void RowPitch_256Multiple_Passes(int bytesPerRow)
    {
        // A 4,096-byte buffer holds the 4 rows at each pitch: at 768, 3 * 768 + 64 = 2,368 bytes.
        BufferDescription buffer = _texBuffer with { Size = 4096 };
        TextureBufferCopy copy = _textureBufferCopy with { BytesPerRow = bytesPerRow };

        CopyValidator.ValidateCopyToBuffer(copy, _texture, buffer);
        CopyValidator.ValidateCopyToTexture(copy, buffer, _texture);
    }

    private static (Action Pass, Action Fail, Type Exception, string Param, string Field, string Name) TextureRow(string rule)
    {
        Type outOfRange = typeof(ArgumentOutOfRangeException);
        Type invalid = typeof(ArgumentException);
        TextureBufferCopy t = _textureBufferCopy;
        TextureLocation at = t.Texture;
        TextureRegion r = t.Region;
        BufferDescription noCopySource = _texBuffer with { Usage = BufferUsage.Vertex | BufferUsage.CopyDestination };
        BufferDescription noCopyDestination = _texBuffer with { Usage = BufferUsage.Vertex | BufferUsage.CopySource };
        TextureDescription noTextureSource = _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopyDestination };
        TextureDescription noTextureDestination = _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopySource };
        TextureDescription single = _multisampled with { SampleCount = 1 };
        TextureBufferCopy firstTexel = t with { Region = new TextureRegion(0, 0, 1, 1) };
        TextureDescription wide = _texture with { Width = 128 };
        BufferDescription big = _texBuffer with { Size = 4096 };
        return rule switch
        {
            "Location.AspectAboveLast" => (ToBuffer(t), ToBuffer(t with { Texture = at with { Aspect = (TextureAspect)2 } }), outOfRange, "copy", "TextureBufferCopy.Texture.Aspect", TextureName),
            "Location.AspectNegative" => (ToBuffer(t), ToBuffer(t with { Texture = at with { Aspect = (TextureAspect)(-1) } }), outOfRange, "copy", "TextureBufferCopy.Texture.Aspect", TextureName),
            "Location.MipNegative" => (ToBuffer(t), ToBuffer(t with { Texture = at with { MipLevel = -1 } }), outOfRange, "copy", "TextureBufferCopy.Texture.MipLevel", TextureName),
            "Location.MipAtCount" => (ToBuffer(AtMip(6, 1, 1)), ToBuffer(AtMip(7, 1, 1)), outOfRange, "copy", "TextureBufferCopy.Texture.MipLevel", TextureName),
            "Location.MipMaxValue" => (ToBuffer(AtMip(6, 1, 1)), ToBuffer(AtMip(int.MaxValue, 1, 1)), outOfRange, "copy", "TextureBufferCopy.Texture.MipLevel", TextureName),
            "Location.LayerNegative" => (ToBuffer(t), ToBuffer(t with { Texture = at with { ArrayLayer = -1 } }), outOfRange, "copy", "TextureBufferCopy.Texture.ArrayLayer", TextureName),
            "Location.LayerAtCount" => (
                ToBuffer(t with { Texture = at with { ArrayLayer = 3 } }),
                ToBuffer(t with { Texture = at with { ArrayLayer = 4 } }),
                outOfRange, "copy", "TextureBufferCopy.Texture.ArrayLayer", TextureName),
            "Location.LayerMaxValue" => (ToBuffer(t), ToBuffer(t with { Texture = at with { ArrayLayer = int.MaxValue } }), outOfRange, "copy", "TextureBufferCopy.Texture.ArrayLayer", TextureName),
            "Location.DepthAspectOnColor" => (ToBuffer(t), ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }), invalid, "copy", "TextureBufferCopy.Texture.Aspect", TextureName),
            "Location.ColorAspectOnDepth32" => (
                ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _depth),
                ToBuffer(t, _depth),
                invalid, "copy", "TextureBufferCopy.Texture.Aspect", TextureName),
            "Location.ColorAspectOnDepth16" => (
                ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _texture with { Format = TextureFormat.Depth16Unorm }),
                ToBuffer(t, _texture with { Format = TextureFormat.Depth16Unorm }),
                invalid, "copy", "TextureBufferCopy.Texture.Aspect", TextureName),

            "Region.XNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { X = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.X", TextureName),
            "Region.XAtExtent" => (ToBuffer(t with { Region = r with { X = 63, Width = 1 } }), ToBuffer(t with { Region = r with { X = 64, Width = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.X", TextureName),
            "Region.XMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { X = int.MaxValue, Width = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.X", TextureName),
            "Region.WidthZero" => (ToBuffer(t with { Region = r with { Width = 1 } }), ToBuffer(t with { Region = r with { Width = 0 } }), outOfRange, "copy", "TextureBufferCopy.Region.Width", TextureName),
            "Region.WidthNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { Width = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Width", TextureName),
            "Region.WidthPastEnd" => (ToBuffer(t with { Region = r with { X = 48 } }), ToBuffer(t with { Region = r with { X = 49 } }), outOfRange, "copy", "TextureBufferCopy.Region.Width", TextureName),
            "Region.WidthMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { X = 1, Width = int.MaxValue } }), outOfRange, "copy", "TextureBufferCopy.Region.Width", TextureName),
            "Region.YNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { Y = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Y", TextureName),
            "Region.YAtExtent" => (ToBuffer(t with { Region = r with { Y = 31, Height = 1 } }), ToBuffer(t with { Region = r with { Y = 32, Height = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Y", TextureName),
            "Region.YMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { Y = int.MaxValue, Height = 1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Y", TextureName),
            "Region.HeightZero" => (ToBuffer(t with { Region = r with { Height = 1 } }), ToBuffer(t with { Region = r with { Height = 0 } }), outOfRange, "copy", "TextureBufferCopy.Region.Height", TextureName),
            "Region.HeightNegative" => (ToBuffer(t), ToBuffer(t with { Region = r with { Height = -1 } }), outOfRange, "copy", "TextureBufferCopy.Region.Height", TextureName),
            "Region.HeightPastEnd" => (ToBuffer(t with { Region = r with { Y = 28 } }), ToBuffer(t with { Region = r with { Y = 29 } }), outOfRange, "copy", "TextureBufferCopy.Region.Height", TextureName),
            "Region.HeightMaxValue" => (ToBuffer(t), ToBuffer(t with { Region = r with { Y = 1, Height = int.MaxValue } }), outOfRange, "copy", "TextureBufferCopy.Region.Height", TextureName),
            // Mip 6 is 1x1: max(1, 32 >> 6) is 1 high, where the bare shift gives 0. Mip 5 is 2x1, mip 3 is 8x4.
            "Region.LastMipIsOneByOne" => (ToBuffer(AtMip(6, 1, 1)), ToBuffer(AtMip(6, 2, 1)), outOfRange, "copy", "TextureBufferCopy.Region.Width", TextureName),
            "Region.MipHalvesWidth" => (ToBuffer(AtMip(5, 2, 1)), ToBuffer(AtMip(5, 3, 1)), outOfRange, "copy", "TextureBufferCopy.Region.Width", TextureName),
            "Region.MipHalvesHeight" => (ToBuffer(AtMip(3, 1, 4)), ToBuffer(AtMip(3, 1, 5)), outOfRange, "copy", "TextureBufferCopy.Region.Height", TextureName),

            "ToBuffer.TextureUsage" => (ToBuffer(t), ToBuffer(t, noTextureSource), invalid, "copy", "TextureBufferCopy.Texture", TextureName),
            "ToBuffer.BufferUsage" => (ToBuffer(t), ToBuffer(t, _texture, noCopyDestination), invalid, "copy", "TextureBufferCopy.Buffer", BufferName),
            "ToBuffer.Multisampled" => (ToBuffer(firstTexel, single), ToBuffer(firstTexel, _multisampled), invalid, "copy", "TextureBufferCopy.Texture", TextureName),
            "ToTexture.BufferUsage" => (ToTexture(t), ToTexture(t, noCopySource, _texture), invalid, "copy", "TextureBufferCopy.Buffer", BufferName),
            "ToTexture.TextureUsage" => (ToTexture(t), ToTexture(t, _texBuffer, noTextureDestination), invalid, "copy", "TextureBufferCopy.Texture", TextureName),
            "ToTexture.Multisampled" => (ToTexture(firstTexel, _texBuffer, single), ToTexture(firstTexel, _texBuffer, _multisampled), invalid, "copy", "TextureBufferCopy.Texture", TextureName),
            "ToTexture.DepthAspect" => (
                ToBuffer(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _depth),
                ToTexture(t with { Texture = at with { Aspect = TextureAspect.Depth } }, _texBuffer, _depth),
                invalid, "copy", "TextureBufferCopy.Texture.Aspect", TextureName),
            // The location rules hold in this direction too.
            "ToTexture.Location" => (ToTexture(AtMip(6, 1, 1)), ToTexture(AtMip(6, 1, 1) with { Region = new TextureRegion(0, 1, 1, 1) }), outOfRange, "copy", "TextureBufferCopy.Region.Y", TextureName),

            // 64 Rgba8 texels make a 256-byte row: a pitch of exactly that passes, and 0 is below it.
            "TextureBuffer.BytesPerRowBelowRow" => (
                ToBuffer(t with { Region = r with { Width = 64 }, BytesPerRow = 256 }),
                ToBuffer(t with { Region = r with { Width = 64 }, BytesPerRow = 0 }),
                outOfRange, "copy", "TextureBufferCopy.BytesPerRow", BufferName),
            // 128 Rgba8 texels make a 512-byte row: a 256-byte pitch is a multiple of 256 and the 4,096-byte buffer holds
            // 3 * 256 + 512 bytes, so only the texel-size factor of the row-size rule rejects it.
            "TextureBuffer.BytesPerRowBelowTexelRow" => (
                ToBuffer(t with { Region = r with { Width = 128 }, BytesPerRow = 512 }, wide, big),
                ToBuffer(t with { Region = r with { Width = 128 }, BytesPerRow = 256 }, wide, big),
                outOfRange, "copy", "TextureBufferCopy.BytesPerRow", BufferName),
            // One byte under a 256-byte row is out of range, checked before the multiple-of-256 rule.
            "TextureBuffer.BytesPerRowOneUnder" => (
                ToBuffer(t with { Region = r with { Width = 64 }, BytesPerRow = 256 }),
                ToBuffer(t with { Region = r with { Width = 64 }, BytesPerRow = 255 }),
                outOfRange, "copy", "TextureBufferCopy.BytesPerRow", BufferName),
            "TextureBuffer.BytesPerRowNegative" => (ToBuffer(t), ToBuffer(t with { BytesPerRow = -256 }), outOfRange, "copy", "TextureBufferCopy.BytesPerRow", BufferName),
            "TextureBuffer.BytesPerRowMaxValue" => (ToBuffer(t), ToBuffer(t with { BytesPerRow = int.MaxValue }), invalid, "copy", "TextureBufferCopy.BytesPerRow", BufferName),
            "TextureBuffer.BytesPerRowAligned" => (ToBuffer(t with { BytesPerRow = 256 }), ToBuffer(t with { BytesPerRow = 128 }), invalid, "copy", "TextureBufferCopy.BytesPerRow", BufferName),
            "TextureBuffer.BufferOffsetNegative" => (ToBuffer(t), ToBuffer(t with { BufferOffset = -4 }), outOfRange, "copy", "TextureBufferCopy.BufferOffset", BufferName),
            "TextureBuffer.BufferOffsetMinValue" => (ToBuffer(t), ToBuffer(t with { BufferOffset = long.MinValue }), outOfRange, "copy", "TextureBufferCopy.BufferOffset", BufferName),
            // R8 texels are 1 byte, so 4 is the alignment; Rgba32Float texels are 16 bytes, so 16 is.
            "TextureBuffer.BufferOffsetAlignedToFour" => (
                ToBuffer(t with { BufferOffset = 4 }, _texture with { Format = TextureFormat.R8Unorm }),
                ToBuffer(t with { BufferOffset = 2 }, _texture with { Format = TextureFormat.R8Unorm }),
                invalid, "copy", "TextureBufferCopy.BufferOffset", BufferName),
            "TextureBuffer.BufferOffsetAlignedToTexel" => (
                ToBuffer(t with { Region = r with { Width = 4 }, BufferOffset = 16 }, _texture with { Format = TextureFormat.Rgba32Float }),
                ToBuffer(t with { Region = r with { Width = 4 }, BufferOffset = 8 }, _texture with { Format = TextureFormat.Rgba32Float }),
                invalid, "copy", "TextureBufferCopy.BufferOffset", BufferName),
            // 192 + 832 ends exactly at the buffer's end; 196 is one aligned step over.
            "TextureBuffer.BufferHoldsRows" => (ToBuffer(t with { BufferOffset = 192 }), ToBuffer(t with { BufferOffset = 196 }), outOfRange, "copy", "TextureBufferCopy.BufferOffset", BufferName),
            "TextureBuffer.BufferOffsetPastEnd" => (ToBuffer(t with { BufferOffset = 192 }), ToBuffer(t with { BufferOffset = 1028 }), outOfRange, "copy", "TextureBufferCopy.BufferOffset", BufferName),
            // long.MaxValue - 3 is the largest multiple of 4, so the buffer-size rule (not alignment) rejects it.
            "TextureBuffer.BufferOffsetMaxValue" => (ToBuffer(t), ToBuffer(t with { BufferOffset = long.MaxValue - 3 }), outOfRange, "copy", "TextureBufferCopy.BufferOffset", BufferName),
            _ => WriteRow(rule),
        };
    }

    // Rows whose offset or row pitch is near the type's maximum: a sum with the offset or an int product would wrap.
    private static (Action Validate, string Param, string Field) TextureHugeOffsetRow(string row)
    {
        // A texture tall enough that BytesPerRow * (Height - 1) passes int.MaxValue: in int it wraps, in long it doesn't.
        TextureDescription tall = _texture with { Width = 64, Height = 65536, ArrayLayers = 1, MipLevels = 1 };
        TextureBufferCopy tallCopy = _textureBufferCopy with { Region = new TextureRegion(0, 0, 64, 65536), BytesPerRow = int.MaxValue - 255 };
        return row switch
        {
            // 2^63 - 16 is a multiple of every texel size; the rows' 832 bytes don't fit in the 15 left.
            "TextureBufferOffsetNearMax" => (
                () => CopyValidator.ValidateCopyToBuffer(_textureBufferCopy with { BufferOffset = long.MaxValue - 15 }, _texture, _huge),
                "copy", "TextureBufferCopy.BufferOffset"),
            "TextureToTextureOffsetNearMax" => (
                () => CopyValidator.ValidateCopyToTexture(_textureBufferCopy with { BufferOffset = long.MaxValue - 15 }, _huge, _texture),
                "copy", "TextureBufferCopy.BufferOffset"),
            // (2^31 - 256) * 65535 + 256 bytes, about 2^47, against a 2^40-byte buffer.
            "TextureRowPitchTimesHeight" => (
                () => CopyValidator.ValidateCopyToBuffer(tallCopy, tall, _huge with { Size = 1L << 40 }),
                "copy", "TextureBufferCopy.BufferOffset"),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };
    }

    [Fact]
    public void TextureMessages_ReportTheComputedBound()
    {
        ArgumentOutOfRangeException width = Assert.Throws<ArgumentOutOfRangeException>(
            () => CopyValidator.ValidateCopyToBuffer(AtMip(1, 33, 1), _texture, _texBuffer));
        ArgumentOutOfRangeException rows = Assert.Throws<ArgumentOutOfRangeException>(
            () => CopyValidator.ValidateCopyToBuffer(_textureBufferCopy with { BufferOffset = 196 }, _texture, _texBuffer));

        Assert.Contains("width after X (32), but is 33", width.Message, StringComparison.Ordinal);
        Assert.Contains("the 832 bytes the rows need", rows.Message, StringComparison.Ordinal);
        Assert.Contains("buffer's end (1024), but is 196", rows.Message, StringComparison.Ordinal);
        Assert.Contains("debug name 'tex'", width.Message, StringComparison.Ordinal);
        Assert.Contains("debug name 'buf'", rows.Message, StringComparison.Ordinal);
    }

    private static TextureBufferCopy AtMip(int mip, int width, int height) => _textureBufferCopy with
    {
        Texture = new TextureLocation(_textureA, MipLevel: mip),
        Region = new TextureRegion(0, 0, width, height),
    };

    private static Action ToBuffer(TextureBufferCopy copy) => ToBuffer(copy, _texture, _texBuffer);

    private static Action ToBuffer(TextureBufferCopy copy, TextureDescription texture) => ToBuffer(copy, texture, _texBuffer);

    private static Action ToBuffer(TextureBufferCopy copy, TextureDescription texture, BufferDescription buffer) =>
        () => CopyValidator.ValidateCopyToBuffer(copy, texture, buffer);

    private static Action ToTexture(TextureBufferCopy copy) => ToTexture(copy, _texBuffer, _texture);

    private static Action ToTexture(TextureBufferCopy copy, BufferDescription buffer, TextureDescription texture) =>
        () => CopyValidator.ValidateCopyToTexture(copy, buffer, texture);
}
