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

    // A 16x16 region copied to the bottom-right corner of the other texture's 64x32 top level: both edges at the bound.
    private static readonly TextureCopy _textureCopy = new()
    {
        Source = new TextureLocation(_writeTexture),
        SourceRegion = new TextureRegion(0, 0, 16, 16),
        Destination = new TextureLocation(_otherTexture),
        DestinationX = 48,
        DestinationY = 16,
    };

    private static readonly string[] _writeRules =
    [
        "Write.Usage", "Write.Depth16Format", "Write.Depth32Format", "Write.Multisampled", "Write.DepthAspectOnColor",
        "Write.AspectAboveLast", "Write.AspectNegative", "Write.MipNegative", "Write.MipAtCount", "Write.LayerNegative", "Write.LayerAtCount",
        "Write.RegionXNegative", "Write.RegionWidthZero", "Write.RegionWidthPastEnd", "Write.RegionHeightPastEnd", "Write.RegionLastMip",
        "Write.BytesPerRowNegative", "Write.BytesPerRowMinValue", "Write.BytesPerRowBelowRow", "Write.BytesPerRowMaxValue",
        "Write.TightRows", "Write.DataLongerAllowed", "Write.PitchedRows", "Write.DataEmpty", "Write.DataNegative",
        "Copy.SourceUsage", "Copy.DestinationUsage", "Copy.SameFormat", "Copy.SourceMultisampled", "Copy.DestinationMultisampled",
        "Copy.SourceLocation", "Copy.DestinationLocation", "Copy.SourceAspect", "Copy.DestinationAspectAboveLast",
        "Copy.DestinationAspectNegative", "Copy.SourceRegion", "Copy.DestinationXNegative", "Copy.DestinationXAtExtent",
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
        TextureCopy oneTexel = c with
        {
            Source = lastMip,
            SourceRegion = new TextureRegion(0, 0, 1, 1),
            Destination = lastMip with { Texture = _otherTexture },
            DestinationX = 0,
            DestinationY = 0,
        };
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

            "Copy.SourceUsage" => (CopyTexture(c), CopyTexture(c, _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopyDestination }, _texture), invalid, "copy", "TextureCopy.Source", TextureName),
            "Copy.DestinationUsage" => (CopyTexture(c), CopyTexture(c, _texture, _texture with { Usage = TextureUsage.Sampled | TextureUsage.CopySource }), invalid, "copy", "TextureCopy.Destination", TextureName),
            // Bgra8 has Rgba8's texel size, so only the format rule tells them apart.
            "Copy.SameFormat" => (CopyTexture(c), CopyTexture(c, _texture, _texture with { Format = TextureFormat.Bgra8Unorm }), invalid, "copy", "TextureCopy.Destination", TextureName),
            "Copy.SourceMultisampled" => (CopyTexture(c, single, single), CopyTexture(c, _multisampled, single), invalid, "copy", "TextureCopy.Source", TextureName),
            "Copy.DestinationMultisampled" => (CopyTexture(c, single, single), CopyTexture(c, single, _multisampled), invalid, "copy", "TextureCopy.Destination", TextureName),
            "Copy.SourceLocation" => (
                CopyTexture(c with { Source = c.Source with { ArrayLayer = 3 } }),
                CopyTexture(c with { Source = c.Source with { ArrayLayer = 4 } }),
                outOfRange, "copy", "TextureCopy.Source.ArrayLayer", TextureName),
            "Copy.DestinationLocation" => (CopyTexture(oneTexel), CopyTexture(oneTexel with { Destination = oneTexel.Destination with { MipLevel = 7 } }), outOfRange, "copy", "TextureCopy.Destination.MipLevel", TextureName),
            "Copy.SourceAspect" => (CopyTexture(c), CopyTexture(c with { Source = c.Source with { Aspect = TextureAspect.Depth } }), invalid, "copy", "TextureCopy.Source.Aspect", TextureName),
            "Copy.DestinationAspectAboveLast" => (
                CopyTexture(c),
                CopyTexture(c with { Destination = c.Destination with { Aspect = (TextureAspect)2 } }),
                outOfRange, "copy", "TextureCopy.Destination.Aspect", TextureName),
            "Copy.DestinationAspectNegative" => (
                CopyTexture(c),
                CopyTexture(c with { Destination = c.Destination with { Aspect = (TextureAspect)(-1) } }),
                outOfRange, "copy", "TextureCopy.Destination.Aspect", TextureName),
            "Copy.SourceRegion" => (
                CopyTexture(c with { SourceRegion = c.SourceRegion with { X = 48 } }),
                CopyTexture(c with { SourceRegion = c.SourceRegion with { X = 49 } }),
                outOfRange, "copy", "TextureCopy.SourceRegion.Width", TextureName),
            "Copy.DestinationXNegative" => (CopyTexture(c with { DestinationX = 0 }), CopyTexture(c with { DestinationX = -1 }), outOfRange, "copy", "TextureCopy.DestinationX", TextureName),
            "Copy.DestinationXAtExtent" => (CopyTexture(oneTexel with { Destination = c.Destination, DestinationX = 63 }), CopyTexture(oneTexel with { Destination = c.Destination, DestinationX = 64 }), outOfRange, "copy", "TextureCopy.DestinationX", TextureName),
            "Copy.DestinationXPastEnd" => (CopyTexture(c), CopyTexture(c with { DestinationX = 49 }), outOfRange, "copy", "TextureCopy.SourceRegion.Width", TextureName),
            "Copy.DestinationXMaxValue" => (CopyTexture(c), CopyTexture(c with { DestinationX = int.MaxValue }), outOfRange, "copy", "TextureCopy.DestinationX", TextureName),
            "Copy.DestinationYNegative" => (CopyTexture(c with { DestinationY = 0 }), CopyTexture(c with { DestinationY = -1 }), outOfRange, "copy", "TextureCopy.DestinationY", TextureName),
            "Copy.DestinationYPastEnd" => (CopyTexture(c), CopyTexture(c with { DestinationY = 17 }), outOfRange, "copy", "TextureCopy.SourceRegion.Height", TextureName),
            "Copy.DestinationYMaxValue" => (CopyTexture(c), CopyTexture(c with { DestinationY = int.MaxValue }), outOfRange, "copy", "TextureCopy.DestinationY", TextureName),
            // Mip 1 of the destination is 32x16, so the 16x16 region fits at (16, 0) and not at (17, 0).
            "Copy.DestinationInsideItsMip" => (
                CopyTexture(c with { Destination = c.Destination with { MipLevel = 1 }, DestinationX = 16, DestinationY = 0 }),
                CopyTexture(c with { Destination = c.Destination with { MipLevel = 1 }, DestinationX = 17, DestinationY = 0 }),
                outOfRange, "copy", "TextureCopy.SourceRegion.Width", TextureName),
            // Mip 6 is 1x1: max(1, 32 >> 6) is 1 high, so (0, 0) is the only place a texel fits.
            "Copy.DestinationLastMip" => (CopyTexture(oneTexel), CopyTexture(oneTexel with { DestinationY = 1 }), outOfRange, "copy", "TextureCopy.DestinationY", TextureName),
            "Copy.DepthToDepth" => (
                CopyTexture(depthCopy, _depth, _depth),
                CopyTexture(depthCopy with { Source = c.Source }, _depth, _depth),
                invalid, "copy", "TextureCopy.Source.Aspect", TextureName),
            "Copy.SameSubresource" => (
                CopyTexture(c with { Destination = c.Source, DestinationX = 16, DestinationY = 0 }),
                CopyTexture(c with { Destination = c.Source, DestinationX = 15, DestinationY = 0 }),
                invalid, "copy", "TextureCopy.Destination", TextureName),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }

    private static Action WriteTexture(TextureWrite write, int dataLength) => WriteTexture(write, dataLength, _texture);

    private static Action WriteTexture(TextureWrite write, int dataLength, TextureDescription target) =>
        () => CopyValidator.ValidateWriteTexture(write, target, dataLength);

    private static Action CopyTexture(TextureCopy copy) => CopyTexture(copy, _texture, _texture);

    private static Action CopyTexture(TextureCopy copy, TextureDescription source, TextureDescription destination) =>
        () => CopyValidator.Validate(copy, source, destination);
}
