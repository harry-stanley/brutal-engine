using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The texture-view description's equality and text form (docs/design/graphics.md section 5).</summary>
public sealed class TextureViewDescriptionTests
{
    private static readonly TextureViewDescription _description = new()
    {
        Texture = new TextureHandle(new Handle(3, 1)),
        Dimension = TextureViewDimension.Texture2DArray,
        BaseMipLevel = -1,
        MipLevelCount = -2,
        BaseArrayLayer = -3,
        ArrayLayerCount = -4,
        DebugName = "layers",
    };

    [Fact]
    public void ToString_UnderAnotherCulture_ListsEveryFieldInvariantly()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish writes the minus sign as U+2212, so a culture-sensitive format of the (invalid) negative values shows.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.NotEqual("-1", (-1).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(
                "TextureViewDescription { Texture = Texture(3:1), Dimension = Texture2DArray, BaseMipLevel = -1, MipLevelCount = -2, "
                + "BaseArrayLayer = -3, ArrayLayerCount = -4, DebugName = layers }",
                _description.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToString_NullTextureAndNoDebugName_ShowsBoth()
    {
        Assert.Equal(
            "TextureViewDescription { Texture = Texture(null), Dimension = Texture2D, BaseMipLevel = 0, MipLevelCount = 1, "
            + "BaseArrayLayer = 0, ArrayLayerCount = 1, DebugName =  }",
            new TextureViewDescription { Texture = default }.ToString());
    }

    // One row per TextureViewDescription property; Equals_FieldRows_CoverEveryProperty fails until a new property adds its row.
    private static readonly string[] _fields =
    [
        nameof(TextureViewDescription.Texture),
        nameof(TextureViewDescription.Dimension),
        nameof(TextureViewDescription.BaseMipLevel),
        nameof(TextureViewDescription.MipLevelCount),
        nameof(TextureViewDescription.BaseArrayLayer),
        nameof(TextureViewDescription.ArrayLayerCount),
        nameof(TextureViewDescription.DebugName),
    ];

    public static TheoryData<string> Fields() => [.. _fields];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Equals_ComparesEveryField(string field)
    {
        TextureViewDescription changed = field switch
        {
            // Same index, next generation: a stale handle to the same slot is a different texture.
            nameof(TextureViewDescription.Texture) => _description with { Texture = new TextureHandle(new Handle(3, 2)) },
            nameof(TextureViewDescription.Dimension) => _description with { Dimension = TextureViewDimension.Texture2D },
            nameof(TextureViewDescription.BaseMipLevel) => _description with { BaseMipLevel = 1 },
            nameof(TextureViewDescription.MipLevelCount) => _description with { MipLevelCount = 2 },
            nameof(TextureViewDescription.BaseArrayLayer) => _description with { BaseArrayLayer = 3 },
            nameof(TextureViewDescription.ArrayLayerCount) => _description with { ArrayLayerCount = 4 },
            // Ordinal: a name that differs only in case is a different name.
            nameof(TextureViewDescription.DebugName) => _description with { DebugName = "Layers" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        Assert.NotEqual(_description, changed);
        Assert.True(_description != changed);
    }

    [Fact]
    public void Equals_FieldRows_CoverEveryProperty()
    {
        Assert.Equal(
            typeof(TextureViewDescription).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal),
            _fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Equals_SameFields_AreEqual()
    {
        // A separately built name and handle, so equality can't be passing by reference.
        TextureViewDescription copy = _description with
        {
            Texture = new TextureHandle(new Handle(3, 1)),
            DebugName = new string("layers".AsSpan()),
        };

        Assert.Equal(_description, copy);
        Assert.True(_description == copy);
        Assert.Equal(_description.GetHashCode(), copy.GetHashCode());
    }
}
