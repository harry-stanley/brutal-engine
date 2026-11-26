using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The texture-write and texture-to-texture copy rules (docs/design/graphics.md section 6).</summary>
public sealed partial class CopyValidatorTests
{
    // Its own handles: static initializers in another partial file may not have run yet.
    private static readonly TextureHandle _writeTexture = new(new Handle(1, 1));
    private static readonly TextureHandle _otherTexture = new(new Handle(2, 1));

    // 4 rows of 16 Rgba8 texels: 64 bytes each, so tight rows need 3 * 64 + 64 = 256 bytes.
    private static readonly TextureWrite _write = new() { Target = new TextureLocation(_writeTexture), Region = new TextureRegion(0, 0, 16, 4) };

    private const string DestinationName = "dst";

    // The copies' destination: a different size, layer count and mip count from the 64x32, 4-layer, 7-level source,
    // and its own debug name, so a destination rule checked against the source, or a message naming the wrong side,
    // fails. Its mips are 128x16, 64x8, 32x4, 16x2 and 8x1.
    private static readonly TextureDescription _destination = new()
    {
        Format = TextureFormat.Rgba8Unorm,
        Width = 128,
        Height = 16,
        ArrayLayers = 2,
        MipLevels = 5,
        Usage = TextureUsage.CopySource | TextureUsage.CopyDestination,
        DebugName = DestinationName,
    };

    // A 16x16 region copied to the right end of the destination's 128x16 top level: both destination edges at the bound.
    private static readonly TextureCopy _textureCopy = new()
    {
        Source = new TextureLocation(_writeTexture),
        SourceRegion = new TextureRegion(0, 0, 16, 16),
        Destination = new TextureLocation(_otherTexture),
        DestinationX = 112,
        DestinationY = 0,
    };

    private static readonly string[] _writeRules =
    [
        "Write.Usage", "Write.Depth16Format", "Write.Depth32Format", "Write.Multisampled", "Write.DepthAspectOnColor",
        "Write.AspectAboveLast", "Write.AspectNegative", "Write.MipNegative", "Write.MipAtCount", "Write.LayerNegative", "Write.LayerAtCount",
        "Write.RegionXNegative", "Write.RegionWidthZero", "Write.RegionWidthPastEnd", "Write.RegionHeightPastEnd", "Write.RegionLastMip",
        "Write.BytesPerRowNegative", "Write.BytesPerRowMinValue", "Write.BytesPerRowBelowRow", "Write.BytesPerRowMaxValue",
        "Write.TightRows", "Write.DataLongerAllowed", "Write.PitchedRows", "Write.DataEmpty", "Write.DataNegative",
        "Copy.SourceUsage", "Copy.DestinationUsage", "Copy.SameFormat", "Copy.SourceMultisampled", "Copy.DestinationMultisampled",
        "Copy.SourceLocation", "Copy.DestinationLocation", "Copy.DestinationMip", "Copy.SourceAspect", "Copy.DestinationAspectAboveLast",
        "Copy.DestinationAspectNegative", "Copy.SourceRegion", "Copy.SourceRegionAtItsOwnMip", "Copy.DestinationXNegative", "Copy.DestinationXAtExtent",
        "Copy.DestinationXPastEnd", "Copy.DestinationXMaxValue", "Copy.DestinationYNegative", "Copy.DestinationYPastEnd",
        "Copy.DestinationYMaxValue", "Copy.DestinationInsideItsMip", "Copy.DestinationLastMip", "Copy.DepthToDepth", "Copy.SameSubresource",
    ];

    [Theory]
    [InlineData(0, 0, 16, 0, 0, 0, true, false)]    // adjacent on the right
    [InlineData(16, 0, 0, 0, 0, 0, true, false)]    // adjacent on the left
    [InlineData(0, 0, 0, 16, 0, 0, true, false)]    // adjacent below
    [InlineData(0, 16, 0, 0, 0, 0, true, false)]    // adjacent above
    [InlineData(0, 0, 15, 0, 0, 0, true, true)]     // one column of overlap on the right
    [InlineData(16, 0, 1, 0, 0, 0, true, true)]     // one column of overlap on the left
    [InlineData(0, 0, 0, 15, 0, 0, true, true)]     // one row of overlap below
    [InlineData(0, 16, 0, 1, 0, 0, true, true)]     // one row of overlap above
    [InlineData(0, 0, 15, 15, 0, 0, true, true)]    // one texel of overlap, at the corner
    [InlineData(0, 0, 16, 15, 0, 0, true, false)]   // diagonal but apart on x
    [InlineData(0, 0, 15, 16, 0, 0, true, false)]   // diagonal but apart on y
    [InlineData(0, 0, 0, 0, 0, 0, true, true)]      // the region onto itself
    [InlineData(0, 0, 0, 0, 0, 1, true, false)]     // another layer
    [InlineData(0, 0, 0, 0, 1, 0, true, false)]     // another mip level (32x16 holds the 16x16 region)
    [InlineData(0, 0, 0, 0, 0, 0, false, false)]    // another texture
    public void TextureCopy_SameSubresourceOverlap_Throws(int sourceX, int sourceY, int destinationX, int destinationY, int mip, int layer, bool sameTexture, bool overlaps)
    {
        TextureCopy copy = _textureCopy with
        {
            SourceRegion = _textureCopy.SourceRegion with { X = sourceX, Y = sourceY },
            Destination = new TextureLocation(sameTexture ? _writeTexture : _otherTexture, mip, layer),
            DestinationX = destinationX,
            DestinationY = destinationY,
        };

        if (overlaps)
        {
            AssertRejected(typeof(ArgumentException), "copy", "TextureCopy.Destination", () => CopyValidator.Validate(copy, _texture, _texture), TextureName);
        }
        else
        {
            CopyValidator.Validate(copy, _texture, _texture);
        }
    }

    [Fact]
    public void WriteAndCopyMessages_ReportTheComputedBound()
    {
        ArgumentOutOfRangeException tight = Assert.Throws<ArgumentOutOfRangeException>(() => CopyValidator.ValidateWriteTexture(_write, _texture, 255));
        ArgumentOutOfRangeException pitch = Assert.Throws<ArgumentOutOfRangeException>(
            () => CopyValidator.ValidateWriteTexture(_write with { BytesPerRow = 63 }, _texture, 256));
        ArgumentOutOfRangeException extent = Assert.Throws<ArgumentOutOfRangeException>(
            () => CopyValidator.Validate(_textureCopy with { DestinationX = 113 }, _texture, _destination));

        Assert.Contains("texel size (256), but is 255", tight.Message, StringComparison.Ordinal);
        Assert.Contains("texel size (64), but is 63", pitch.Message, StringComparison.Ordinal);
        Assert.Contains("width after DestinationX (15), but is 16", extent.Message, StringComparison.Ordinal);
        Assert.Contains("debug name 'dst'", extent.Message, StringComparison.Ordinal);
    }

    private static (Action Pass, Action Fail, Type Exception, string Param, string Field, string Name) WriteRow(string rule)
    {
        Type outOfRange = typeof(ArgumentOutOfRangeException);
        Type invalid = typeof(ArgumentException);
        TextureWrite w = _write;
        TextureLocation at = w.Target;
        TextureRegion r = w.Region;
        TextureCopy c = _textureCopy;
        TextureDescription depth16 = _texture with { Format = TextureFormat.Depth16Unorm };
        TextureDescription single = _multisampled with { SampleCount = 1 };
        TextureWrite depthWrite = w with { Target = at with { Aspect = TextureAspect.Depth } };
        TextureLocation lastMip = new(_writeTexture, MipLevel: 6);
        TextureCopy oneTexel = c with { SourceRegion = new TextureRegion(0, 0, 1, 1), DestinationX = 0, DestinationY = 0 };
        TextureDescription singleDestination = _destination with { ArrayLayers = 1, MipLevels = 1 };
        TextureDescription depthDestination = _destination with { Format = TextureFormat.Depth32Float };
        TextureCopy depthCopy = c with { Source = c.Source with { Aspect = TextureAspect.Depth }, Destination = c.Destination with { Aspect = TextureAspect.Depth } };
        return rule switch
        {
            "Write.Usage" => (WriteTexture(w, 256), WriteTexture(w, 256, _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopySource }), invalid, "write", "TextureWrite.Target", TextureName),
            "Write.Depth16Format" => (WriteTexture(w, 256), WriteTexture(depthWrite, 256, depth16), invalid, "write", "TextureWrite.Target", TextureName),
            "Write.Depth32Format" => (WriteTexture(w, 256), WriteTexture(depthWrite, 256, _depth), invalid, "write", "TextureWrite.Target", TextureName),
            "Write.Multisampled" => (WriteTexture(w, 256, single), WriteTexture(w, 256, _multisampled), invalid, "write", "TextureWrite.Target", TextureName),
            "Write.DepthAspectOnColor" => (WriteTexture(w, 256), WriteTexture(depthWrite, 256), invalid, "write", "TextureWrite.Target.Aspect", TextureName),
            "Write.AspectAboveLast" => (WriteTexture(w, 256), WriteTexture(w with { Target = at with { Aspect = (TextureAspect)2 } }, 256), outOfRange, "write", "TextureWrite.Target.Aspect", TextureName),
            "Write.AspectNegative" => (WriteTexture(w, 256), WriteTexture(w with { Target = at with { Aspect = (TextureAspect)(-1) } }, 256), outOfRange, "write", "TextureWrite.Target.Aspect", TextureName),
            "Write.MipNegative" => (WriteTexture(w, 256), WriteTexture(w with { Target = at with { MipLevel = -1 } }, 256), outOfRange, "write", "TextureWrite.Target.MipLevel", TextureName),
            "Write.MipAtCount" => (
                WriteTexture(w with { Target = lastMip, Region = new TextureRegion(0, 0, 1, 1) }, 4),
                WriteTexture(w with { Target = at with { MipLevel = 7 }, Region = new TextureRegion(0, 0, 1, 1) }, 4),
                outOfRange, "write", "TextureWrite.Target.MipLevel", TextureName),
            "Write.LayerNegative" => (WriteTexture(w, 256), WriteTexture(w with { Target = at with { ArrayLayer = -1 } }, 256), outOfRange, "write", "TextureWrite.Target.ArrayLayer", TextureName),
            "Write.LayerAtCount" => (
                WriteTexture(w with { Target = at with { ArrayLayer = 3 } }, 256),
                WriteTexture(w with { Target = at with { ArrayLayer = 4 } }, 256),
                outOfRange, "write", "TextureWrite.Target.ArrayLayer", TextureName),
            "Write.RegionXNegative" => (WriteTexture(w, 256), WriteTexture(w with { Region = r with { X = -1 } }, 256), outOfRange, "write", "TextureWrite.Region.X", TextureName),
            "Write.RegionWidthZero" => (WriteTexture(w with { Region = r with { Width = 1 } }, 256), WriteTexture(w with { Region = r with { Width = 0 } }, 256), outOfRange, "write", "TextureWrite.Region.Width", TextureName),
            "Write.RegionWidthPastEnd" => (WriteTexture(w with { Region = r with { X = 48 } }, 256), WriteTexture(w with { Region = r with { X = 49 } }, 256), outOfRange, "write", "TextureWrite.Region.Width", TextureName),
            "Write.RegionHeightPastEnd" => (WriteTexture(w with { Region = r with { Y = 28 } }, 256), WriteTexture(w with { Region = r with { Y = 29 } }, 256), outOfRange, "write", "TextureWrite.Region.Height", TextureName),
            // Mip 6 is 1x1: max(1, 32 >> 6) is 1 high.
            "Write.RegionLastMip" => (
                WriteTexture(w with { Target = lastMip, Region = new TextureRegion(0, 0, 1, 1) }, 4),
                WriteTexture(w with { Target = lastMip, Region = new TextureRegion(0, 0, 1, 2) }, 8),
                outOfRange, "write", "TextureWrite.Region.Height", TextureName),
            "Write.BytesPerRowNegative" => (WriteTexture(w, 256), WriteTexture(w with { BytesPerRow = -1 }, 256), outOfRange, "write", "TextureWrite.BytesPerRow", TextureName),
            "Write.BytesPerRowMinValue" => (WriteTexture(w, 256), WriteTexture(w with { BytesPerRow = int.MinValue }, 256), outOfRange, "write", "TextureWrite.BytesPerRow", TextureName),
            "Write.BytesPerRowBelowRow" => (WriteTexture(w with { BytesPerRow = 64 }, 256), WriteTexture(w with { BytesPerRow = 63 }, 256), outOfRange, "write", "TextureWrite.BytesPerRow", TextureName),
            // Any pitch at or above the row size is accepted, so int.MaxValue is too; two such rows need more than
            // int.MaxValue bytes, which no span holds, computed on long without wrapping.
            "Write.BytesPerRowMaxValue" => (
                WriteTexture(w with { Region = r with { Height = 1 }, BytesPerRow = int.MaxValue }, 64),
                WriteTexture(w with { Region = r with { Height = 2 }, BytesPerRow = int.MaxValue }, int.MaxValue),
                outOfRange, "data", "TextureWrite.data.Length", TextureName),
            // BytesPerRow 0 means tight rows: 4 rows of 64 bytes.
            "Write.TightRows" => (WriteTexture(w, 256), WriteTexture(w, 255), outOfRange, "data", "TextureWrite.data.Length", TextureName),
            "Write.DataLongerAllowed" => (WriteTexture(w, 257), WriteTexture(w with { BytesPerRow = 64 }, 255), outOfRange, "data", "TextureWrite.data.Length", TextureName),
            // Any row pitch, not only multiples of 256: 3 * 100 + 64, as the last row needs only its texels.
            "Write.PitchedRows" => (WriteTexture(w with { BytesPerRow = 100 }, 364), WriteTexture(w with { BytesPerRow = 100 }, 363), outOfRange, "data", "TextureWrite.data.Length", TextureName),
            "Write.DataEmpty" => (WriteTexture(w with { Region = new TextureRegion(0, 0, 1, 1) }, 4), WriteTexture(w, 0), outOfRange, "data", "TextureWrite.data.Length", TextureName),
            "Write.DataNegative" => (WriteTexture(w, int.MaxValue), WriteTexture(w, int.MinValue), outOfRange, "data", "TextureWrite.data.Length", TextureName),

            "Copy.SourceUsage" => (
                CopyTexture(c),
                CopyTexture(c, _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopyDestination }, _destination),
                invalid, "copy", "TextureCopy.Source", TextureName),
            "Copy.DestinationUsage" => (
                CopyTexture(c),
                CopyTexture(c, _texture, _destination with { Usage = TextureUsage.Sampled | TextureUsage.CopySource }),
                invalid, "copy", "TextureCopy.Destination", DestinationName),
            // Bgra8 has Rgba8's texel size, so only the format rule tells them apart.
            "Copy.SameFormat" => (CopyTexture(c), CopyTexture(c, _texture, _destination with { Format = TextureFormat.Bgra8Unorm }), invalid, "copy", "TextureCopy.Destination", DestinationName),
            "Copy.SourceMultisampled" => (CopyTexture(c, single, singleDestination), CopyTexture(c, _multisampled, singleDestination), invalid, "copy", "TextureCopy.Source", TextureName),
            "Copy.DestinationMultisampled" => (
                CopyTexture(c, single, singleDestination),
                CopyTexture(c, single, singleDestination with { SampleCount = 2 }),
                invalid, "copy", "TextureCopy.Destination", DestinationName),
            "Copy.SourceLocation" => (
                CopyTexture(c with { Source = c.Source with { ArrayLayer = 3 } }),
                CopyTexture(c with { Source = c.Source with { ArrayLayer = 4 } }),
                outOfRange, "copy", "TextureCopy.Source.ArrayLayer", TextureName),
            // The destination has 2 layers and 5 levels, the source 4 and 7: layer 2 and level 5 exist in the source only.
            "Copy.DestinationLocation" => (
                CopyTexture(c with { Destination = c.Destination with { ArrayLayer = 1 } }),
                CopyTexture(c with { Destination = c.Destination with { ArrayLayer = 2 } }),
                outOfRange, "copy", "TextureCopy.Destination.ArrayLayer", DestinationName),
            "Copy.DestinationMip" => (
                CopyTexture(oneTexel with { Destination = c.Destination with { MipLevel = 4 } }),
                CopyTexture(oneTexel with { Destination = c.Destination with { MipLevel = 5 } }),
                outOfRange, "copy", "TextureCopy.Destination.MipLevel", DestinationName),
            "Copy.SourceAspect" => (CopyTexture(c), CopyTexture(c with { Source = c.Source with { Aspect = TextureAspect.Depth } }), invalid, "copy", "TextureCopy.Source.Aspect", TextureName),
            "Copy.DestinationAspectAboveLast" => (
                CopyTexture(c),
                CopyTexture(c with { Destination = c.Destination with { Aspect = (TextureAspect)2 } }),
                outOfRange, "copy", "TextureCopy.Destination.Aspect", DestinationName),
            "Copy.DestinationAspectNegative" => (
                CopyTexture(c),
                CopyTexture(c with { Destination = c.Destination with { Aspect = (TextureAspect)(-1) } }),
                outOfRange, "copy", "TextureCopy.Destination.Aspect", DestinationName),
            "Copy.SourceRegion" => (
                CopyTexture(c with { SourceRegion = c.SourceRegion with { X = 48 } }),
                CopyTexture(c with { SourceRegion = c.SourceRegion with { X = 49 } }),
                outOfRange, "copy", "TextureCopy.SourceRegion.Width", TextureName),
            // A 16x8 region at (40, 20) of the source's 64x32 level 0, copied to the destination's level 1 (64x8). At the
            // source's level 1 (32x16) it wouldn't fit, so the region must be checked at the source's own level.
            "Copy.SourceRegionAtItsOwnMip" => (
                CopyTexture(c with { SourceRegion = new TextureRegion(40, 20, 16, 8), Destination = c.Destination with { MipLevel = 1 }, DestinationX = 0 }),
                CopyTexture(c with { SourceRegion = new TextureRegion(49, 20, 16, 8), Destination = c.Destination with { MipLevel = 1 }, DestinationX = 0 }),
                outOfRange, "copy", "TextureCopy.SourceRegion.Width", TextureName),
            "Copy.DestinationXNegative" => (CopyTexture(c with { DestinationX = 0 }), CopyTexture(c with { DestinationX = -1 }), outOfRange, "copy", "TextureCopy.DestinationX", DestinationName),
            // The destination is 128 wide and the source 64: X 127 fits only the destination.
            "Copy.DestinationXAtExtent" => (CopyTexture(oneTexel with { DestinationX = 127 }), CopyTexture(oneTexel with { DestinationX = 128 }), outOfRange, "copy", "TextureCopy.DestinationX", DestinationName),
            "Copy.DestinationXPastEnd" => (CopyTexture(c), CopyTexture(c with { DestinationX = 113 }), outOfRange, "copy", "TextureCopy.SourceRegion.Width", DestinationName),
            "Copy.DestinationXMaxValue" => (CopyTexture(c), CopyTexture(c with { DestinationX = int.MaxValue }), outOfRange, "copy", "TextureCopy.DestinationX", DestinationName),
            "Copy.DestinationYNegative" => (CopyTexture(c with { DestinationY = 0 }), CopyTexture(c with { DestinationY = -1 }), outOfRange, "copy", "TextureCopy.DestinationY", DestinationName),
            // The destination is 16 high and the source 32: the 16-high region fits at Y 0 only in the destination.
            "Copy.DestinationYPastEnd" => (CopyTexture(c), CopyTexture(c with { DestinationY = 1 }), outOfRange, "copy", "TextureCopy.SourceRegion.Height", DestinationName),
            "Copy.DestinationYMaxValue" => (CopyTexture(c), CopyTexture(c with { DestinationY = int.MaxValue }), outOfRange, "copy", "TextureCopy.DestinationY", DestinationName),
            // The destination's level 1 is 64x8 (the source's is 32x16): a 16x8 region fits at (48, 0), not at (49, 0).
            "Copy.DestinationInsideItsMip" => (
                CopyTexture(c with { SourceRegion = new TextureRegion(0, 0, 16, 8), Destination = c.Destination with { MipLevel = 1 }, DestinationX = 48 }),
                CopyTexture(c with { SourceRegion = new TextureRegion(0, 0, 16, 8), Destination = c.Destination with { MipLevel = 1 }, DestinationX = 49 }),
                outOfRange, "copy", "TextureCopy.SourceRegion.Width", DestinationName),
            // The destination's last level is 8x1: max(1, 16 >> 4) is 1 high, so a texel fits at (7, 0) and not at (0, 1).
            "Copy.DestinationLastMip" => (
                CopyTexture(oneTexel with { Destination = c.Destination with { MipLevel = 4 }, DestinationX = 7 }),
                CopyTexture(oneTexel with { Destination = c.Destination with { MipLevel = 4 }, DestinationY = 1 }),
                outOfRange, "copy", "TextureCopy.DestinationY", DestinationName),
            "Copy.DepthToDepth" => (
                CopyTexture(depthCopy, _depth, depthDestination),
                CopyTexture(depthCopy with { Source = c.Source }, _depth, depthDestination),
                invalid, "copy", "TextureCopy.Source.Aspect", TextureName),
            // Within one texture, so one description serves both sides.
            "Copy.SameSubresource" => (
                CopyTexture(c with { Destination = c.Source, DestinationX = 16, DestinationY = 0 }, _texture, _texture),
                CopyTexture(c with { Destination = c.Source, DestinationX = 15, DestinationY = 0 }, _texture, _texture),
                invalid, "copy", "TextureCopy.Destination", TextureName),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }

    private static Action WriteTexture(TextureWrite write, int dataLength) => WriteTexture(write, dataLength, _texture);

    private static Action WriteTexture(TextureWrite write, int dataLength, TextureDescription target) =>
        () => CopyValidator.ValidateWriteTexture(write, target, dataLength);

    private static Action CopyTexture(TextureCopy copy) => CopyTexture(copy, _texture, _destination);

    private static Action CopyTexture(TextureCopy copy, TextureDescription source, TextureDescription destination) =>
        () => CopyValidator.Validate(copy, source, destination);
}
