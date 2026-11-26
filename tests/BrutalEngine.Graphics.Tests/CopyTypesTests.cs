using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The copy and upload records' defaults, text forms and equality (docs/design/graphics.md section 6).</summary>
public sealed class CopyTypesTests
{
    private static readonly TextureHandle _texture = new(new Handle(3, 1));
    private static readonly BufferHandle _buffer = new(new Handle(5, 2));

    // Negative values where the types allow them (validation is the device's), so a culture-sensitive format shows.
    private static readonly TextureRegion _region = new(-1, -2, -3, -4);
    private static readonly TextureLocation _location = new(_texture, -5, -6, TextureAspect.Depth);
    private static readonly TextureWrite _write = new() { Target = _location, Region = _region, BytesPerRow = -7 };

    private static readonly BufferCopy _bufferCopy = new()
    {
        Source = _buffer,
        SourceOffset = -8,
        Destination = new BufferHandle(new Handle(6, 1)),
        DestinationOffset = -9,
        Size = -10,
    };

    private static readonly TextureBufferCopy _textureBufferCopy = new()
    {
        Texture = _location,
        Region = _region,
        Buffer = _buffer,
        BufferOffset = -11,
        BytesPerRow = -12,
    };

    private static readonly TextureCopy _textureCopy = new()
    {
        Source = _location,
        SourceRegion = _region,
        Destination = new TextureLocation(new TextureHandle(new Handle(4, 1))),
        DestinationX = -13,
        DestinationY = -14,
    };

    private const string RegionText = "TextureRegion { X = -1, Y = -2, Width = -3, Height = -4 }";
    private const string LocationText = "TextureLocation { Texture = Texture(3:1), MipLevel = -5, ArrayLayer = -6, Aspect = Depth }";

    [Fact]
    public void TextureLocation_OptionalParameters_DefaultToFirstSubresourceAndColor()
    {
        var location = new TextureLocation(_texture);

        Assert.Equal(_texture, location.Texture);
        Assert.Equal(0, location.MipLevel);
        Assert.Equal(0, location.ArrayLayer);
        Assert.Equal(TextureAspect.Color, location.Aspect);
    }

    [Fact]
    public void TextureLocation_Default_IsNullTextureFirstSubresourceAndColor()
    {
        TextureLocation location = default;

        Assert.True(location.Texture.IsNull);
        Assert.Equal(new TextureLocation(default, 0, 0, TextureAspect.Color), location);
    }

    [Fact]
    public void OptionalProperties_DefaultToZero()
    {
        var write = new TextureWrite { Target = _location, Region = _region };
        var bufferCopy = new BufferCopy { Source = _buffer, Destination = _buffer, Size = 4 };
        var textureBufferCopy = new TextureBufferCopy { Texture = _location, Region = _region, Buffer = _buffer, BytesPerRow = 256 };
        var textureCopy = new TextureCopy { Source = _location, SourceRegion = _region, Destination = _location };

        Assert.Equal(0, write.BytesPerRow);
        Assert.Equal(0, bufferCopy.SourceOffset);
        Assert.Equal(0, bufferCopy.DestinationOffset);
        Assert.Equal(0, textureBufferCopy.BufferOffset);
        Assert.Equal(0, textureCopy.DestinationX);
        Assert.Equal(0, textureCopy.DestinationY);
    }

    public static TheoryData<string> Types() =>
        [nameof(TextureRegion), nameof(TextureLocation), nameof(TextureWrite), nameof(BufferCopy), nameof(TextureBufferCopy), nameof(TextureCopy)];

    [Theory]
    [MemberData(nameof(Types))]
    public void ToString_UnderAnotherCulture_ListsEveryFieldInvariantly(string type)
    {
        (object value, string expected) = type switch
        {
            nameof(TextureRegion) => ((object)_region, RegionText),
            nameof(TextureLocation) => (_location, LocationText),
            nameof(TextureWrite) => (_write, $"TextureWrite {{ Target = {LocationText}, Region = {RegionText}, BytesPerRow = -7 }}"),
            nameof(BufferCopy) => (
                _bufferCopy,
                "BufferCopy { Source = Buffer(5:2), SourceOffset = -8, Destination = Buffer(6:1), DestinationOffset = -9, Size = -10 }"),
            nameof(TextureBufferCopy) => (
                _textureBufferCopy,
                $"TextureBufferCopy {{ Texture = {LocationText}, Region = {RegionText}, Buffer = Buffer(5:2), BufferOffset = -11, BytesPerRow = -12 }}"),
            nameof(TextureCopy) => (
                _textureCopy,
                $"TextureCopy {{ Source = {LocationText}, SourceRegion = {RegionText}, "
                + "Destination = TextureLocation { Texture = Texture(4:1), MipLevel = 0, ArrayLayer = 0, Aspect = Color }, DestinationX = -13, DestinationY = -14 }"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown type."),
        };

        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish writes the minus sign as U+2212, so a culture-sensitive format of the negative values shows.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.NotEqual("-1", (-1).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(expected, value.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToString_NullHandles_ShowNull()
    {
        Assert.Equal(
            "TextureLocation { Texture = Texture(null), MipLevel = 0, ArrayLayer = 0, Aspect = Color }",
            default(TextureLocation).ToString());
        Assert.Equal(
            "BufferCopy { Source = Buffer(null), SourceOffset = 0, Destination = Buffer(null), DestinationOffset = 0, Size = 0 }",
            default(BufferCopy).ToString());
    }

    // One row per public property of the six records; Equals_FieldRows_CoverEveryProperty fails until a new property adds its row.
    private static readonly string[] _fields =
    [
        "TextureRegion.X", "TextureRegion.Y", "TextureRegion.Width", "TextureRegion.Height",
        "TextureLocation.Texture", "TextureLocation.MipLevel", "TextureLocation.ArrayLayer", "TextureLocation.Aspect",
        "TextureWrite.Target", "TextureWrite.Region", "TextureWrite.BytesPerRow",
        "BufferCopy.Source", "BufferCopy.SourceOffset", "BufferCopy.Destination", "BufferCopy.DestinationOffset", "BufferCopy.Size",
        "TextureBufferCopy.Texture", "TextureBufferCopy.Region", "TextureBufferCopy.Buffer", "TextureBufferCopy.BufferOffset",
        "TextureBufferCopy.BytesPerRow",
        "TextureCopy.Source", "TextureCopy.SourceRegion", "TextureCopy.Destination", "TextureCopy.DestinationX", "TextureCopy.DestinationY",
    ];

    public static TheoryData<string> Fields() => [.. _fields];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Equals_ComparesEveryField(string field)
    {
        // Same index, next generation: a stale handle to the same slot is a different object.
        var otherTexture = new TextureHandle(new Handle(3, 2));
        var otherBuffer = new BufferHandle(new Handle(5, 3));
        TextureRegion otherRegion = _region with { Height = 4 };
        TextureLocation otherLocation = _location with { Aspect = TextureAspect.Color };
        Action check = field switch
        {
            "TextureRegion.X" => () => AssertDiffers(_region, _region with { X = 1 }),
            "TextureRegion.Y" => () => AssertDiffers(_region, _region with { Y = 2 }),
            "TextureRegion.Width" => () => AssertDiffers(_region, _region with { Width = 3 }),
            "TextureRegion.Height" => () => AssertDiffers(_region, otherRegion),
            "TextureLocation.Texture" => () => AssertDiffers(_location, _location with { Texture = otherTexture }),
            "TextureLocation.MipLevel" => () => AssertDiffers(_location, _location with { MipLevel = 5 }),
            "TextureLocation.ArrayLayer" => () => AssertDiffers(_location, _location with { ArrayLayer = 6 }),
            "TextureLocation.Aspect" => () => AssertDiffers(_location, otherLocation),
            "TextureWrite.Target" => () => AssertDiffers(_write, _write with { Target = otherLocation }),
            "TextureWrite.Region" => () => AssertDiffers(_write, _write with { Region = otherRegion }),
            "TextureWrite.BytesPerRow" => () => AssertDiffers(_write, _write with { BytesPerRow = 7 }),
            "BufferCopy.Source" => () => AssertDiffers(_bufferCopy, _bufferCopy with { Source = otherBuffer }),
            "BufferCopy.SourceOffset" => () => AssertDiffers(_bufferCopy, _bufferCopy with { SourceOffset = 8 }),
            "BufferCopy.Destination" => () => AssertDiffers(_bufferCopy, _bufferCopy with { Destination = otherBuffer }),
            "BufferCopy.DestinationOffset" => () => AssertDiffers(_bufferCopy, _bufferCopy with { DestinationOffset = 9 }),
            "BufferCopy.Size" => () => AssertDiffers(_bufferCopy, _bufferCopy with { Size = 10 }),
            "TextureBufferCopy.Texture" => () => AssertDiffers(_textureBufferCopy, _textureBufferCopy with { Texture = otherLocation }),
            "TextureBufferCopy.Region" => () => AssertDiffers(_textureBufferCopy, _textureBufferCopy with { Region = otherRegion }),
            "TextureBufferCopy.Buffer" => () => AssertDiffers(_textureBufferCopy, _textureBufferCopy with { Buffer = otherBuffer }),
            "TextureBufferCopy.BufferOffset" => () => AssertDiffers(_textureBufferCopy, _textureBufferCopy with { BufferOffset = 11 }),
            "TextureBufferCopy.BytesPerRow" => () => AssertDiffers(_textureBufferCopy, _textureBufferCopy with { BytesPerRow = 12 }),
            "TextureCopy.Source" => () => AssertDiffers(_textureCopy, _textureCopy with { Source = otherLocation }),
            "TextureCopy.SourceRegion" => () => AssertDiffers(_textureCopy, _textureCopy with { SourceRegion = otherRegion }),
            "TextureCopy.Destination" => () => AssertDiffers(_textureCopy, _textureCopy with { Destination = otherLocation }),
            "TextureCopy.DestinationX" => () => AssertDiffers(_textureCopy, _textureCopy with { DestinationX = 13 }),
            "TextureCopy.DestinationY" => () => AssertDiffers(_textureCopy, _textureCopy with { DestinationY = 14 }),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        check();
    }

    [Fact]
    public void Equals_FieldRows_CoverEveryProperty()
    {
        Type[] types = [typeof(TextureRegion), typeof(TextureLocation), typeof(TextureWrite), typeof(BufferCopy), typeof(TextureBufferCopy), typeof(TextureCopy)];

        Assert.Equal(
            types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => t.Name + "." + p.Name)).Order(StringComparer.Ordinal),
            _fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Equals_SameFields_AreEqual()
    {
        // Separately built handles and nested records, so equality can't be passing by reference or by a shared copy.
        var texture = new TextureHandle(new Handle(3, 1));
        var region = new TextureRegion(-1, -2, -3, -4);
        var location = new TextureLocation(texture, -5, -6, TextureAspect.Depth);
        var buffer = new BufferHandle(new Handle(5, 2));

        AssertSame(_region, region, _region == region, _region != region);
        AssertSame(_location, location, _location == location, _location != location);
        TextureWrite write = _write with { Target = location, Region = region };
        AssertSame(_write, write, _write == write, _write != write);
        BufferCopy bufferCopy = _bufferCopy with { Source = buffer, Destination = new BufferHandle(new Handle(6, 1)) };
        AssertSame(_bufferCopy, bufferCopy, _bufferCopy == bufferCopy, _bufferCopy != bufferCopy);
        TextureBufferCopy textureBufferCopy = _textureBufferCopy with { Texture = location, Region = region, Buffer = buffer };
        AssertSame(_textureBufferCopy, textureBufferCopy, _textureBufferCopy == textureBufferCopy, _textureBufferCopy != textureBufferCopy);
        TextureCopy textureCopy = _textureCopy with { Source = location, SourceRegion = region };
        AssertSame(_textureCopy, textureCopy, _textureCopy == textureCopy, _textureCopy != textureCopy);
    }

    [Fact]
    public void EqualityAndHash_DoNotAllocate()
    {
        // The records document that no member allocates except ToString. The project turns background garbage
        // collection off (AllocationMeasurementTests guards it), so the count is exact. The comparisons call the
        // records' own Equals and operators, never a generic helper that could box them.
        bool correct = CompareAll();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            correct &= CompareAll();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(correct, "Equality gave a wrong answer inside the measured loop.");
        Assert.Equal(0, allocated);
    }

    private static bool CompareAll()
    {
        TextureWrite write = _write with { BytesPerRow = 0 };
        BufferCopy bufferCopy = _bufferCopy with { Size = 4 };
        TextureBufferCopy textureBufferCopy = _textureBufferCopy with { BytesPerRow = 256 };
        TextureCopy textureCopy = _textureCopy with { DestinationX = 0 };
        TextureLocation location = _location with { MipLevel = 0 };
        TextureRegion region = _region with { X = 0 };
        return _region.Equals(_region) && _region != region && _region.GetHashCode() == _region.GetHashCode()
            && _location.Equals(_location) && _location != location && _location.GetHashCode() == _location.GetHashCode()
            && _write.Equals(_write) && _write != write && _write.GetHashCode() == _write.GetHashCode()
            && _bufferCopy.Equals(_bufferCopy) && _bufferCopy != bufferCopy && _bufferCopy.GetHashCode() == _bufferCopy.GetHashCode()
            && _textureBufferCopy.Equals(_textureBufferCopy) && _textureBufferCopy != textureBufferCopy
            && _textureBufferCopy.GetHashCode() == _textureBufferCopy.GetHashCode()
            && _textureCopy.Equals(_textureCopy) && _textureCopy != textureCopy && _textureCopy.GetHashCode() == _textureCopy.GetHashCode();
    }

    private static void AssertDiffers<T>(T value, T changed)
        where T : struct, IEquatable<T>
    {
        Assert.False(value.Equals(changed));
        Assert.False(changed.Equals(value));
    }

    private static void AssertSame<T>(T value, T copy, bool equalOperator, bool notEqualOperator)
        where T : struct, IEquatable<T>
    {
        Assert.True(value.Equals(copy));
        Assert.True(equalOperator);
        Assert.False(notEqualOperator);
        Assert.Equal(value.GetHashCode(), copy.GetHashCode());
    }
}
