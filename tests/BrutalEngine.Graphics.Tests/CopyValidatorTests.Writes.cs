using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The texture-write rules (docs/design/graphics.md section 6).</summary>
public sealed partial class CopyValidatorTests
{
    // Its own handle: static initializers in another partial file may not have run yet.
    private static readonly TextureHandle _writeTexture = new(new Handle(1, 1));

    // 4 rows of 16 Rgba8 texels: 64 bytes each, so tight rows need 3 * 64 + 64 = 256 bytes.
    private static readonly TextureWrite _write = new() { Target = new TextureLocation(_writeTexture), Region = new TextureRegion(0, 0, 16, 4) };

    private static readonly string[] _writeRules =
    [
        "Write.Usage", "Write.Depth16Format", "Write.Depth32Format", "Write.Multisampled", "Write.DepthAspectOnColor",
        "Write.AspectAboveLast", "Write.AspectNegative", "Write.MipNegative", "Write.MipAtCount", "Write.LayerNegative", "Write.LayerAtCount",
        "Write.RegionXNegative", "Write.RegionWidthZero", "Write.RegionWidthPastEnd", "Write.RegionHeightPastEnd", "Write.RegionLastMip",
        "Write.BytesPerRowNegative", "Write.BytesPerRowMinValue", "Write.BytesPerRowBelowRow", "Write.BytesPerRowMaxValue",
        "Write.TightRows", "Write.DataLongerAllowed", "Write.PitchedRows", "Write.DataEmpty", "Write.DataNegative",
    ];

    private static (Action Pass, Action Fail, Type Exception, string Param, string Field, string Name) WriteRow(string rule)
    {
        Type outOfRange = typeof(ArgumentOutOfRangeException);
        Type invalid = typeof(ArgumentException);
        TextureWrite w = _write;
        TextureLocation at = w.Target;
        TextureRegion r = w.Region;
        TextureDescription depth16 = _texture with { Format = TextureFormat.Depth16Unorm };
        TextureDescription single = _multisampled with { SampleCount = 1 };
        TextureWrite depthWrite = w with { Target = at with { Aspect = TextureAspect.Depth } };
        TextureLocation lastMip = new(_writeTexture, MipLevel: 6);
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
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }

    private static Action WriteTexture(TextureWrite write, int dataLength) => WriteTexture(write, dataLength, _texture);

    private static Action WriteTexture(TextureWrite write, int dataLength, TextureDescription target) =>
        () => CopyValidator.ValidateWriteTexture(write, target, dataLength);
}
