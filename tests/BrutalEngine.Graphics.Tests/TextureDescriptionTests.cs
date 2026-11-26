using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The texture description's equality and text form (docs/design/graphics.md section 5).</summary>
public sealed class TextureDescriptionTests
{
    private static readonly TextureDescription _description = new()
    {
        Format = TextureFormat.Bgra8UnormSrgb,
        Width = -256,
        Height = -128,
        ArrayLayers = -2,
        MipLevels = -3,
        SampleCount = -4,
        Usage = TextureUsage.Sampled | TextureUsage.CopyDestination,
        DebugName = "atlas",
    };

    [Fact]
    public void ToString_UnderAnotherCulture_ListsEveryFieldInvariantly()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish writes the minus sign as U+2212, so a culture-sensitive format of the (invalid) negative counts shows.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.NotEqual("-1", (-1).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(
                "TextureDescription { Format = Bgra8UnormSrgb, Width = -256, Height = -128, ArrayLayers = -2, MipLevels = -3, "
                + "SampleCount = -4, Usage = Sampled, CopyDestination, DebugName = atlas }",
                _description.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToString_NoDebugName_LeavesItEmpty()
    {
        Assert.Equal(
            "TextureDescription { Format = R8Unorm, Width = 1, Height = 2, ArrayLayers = 1, MipLevels = 1, SampleCount = 1, "
            + "Usage = ColorTarget, DebugName =  }",
            new TextureDescription { Format = TextureFormat.R8Unorm, Width = 1, Height = 2, Usage = TextureUsage.ColorTarget }.ToString());
    }

    // One row per TextureDescription property; Equals_FieldRows_CoverEveryProperty fails until a new property adds its row.
    private static readonly string[] _fields =
    [
        nameof(TextureDescription.Format),
        nameof(TextureDescription.Width),
        nameof(TextureDescription.Height),
        nameof(TextureDescription.ArrayLayers),
        nameof(TextureDescription.MipLevels),
        nameof(TextureDescription.SampleCount),
        nameof(TextureDescription.Usage),
        nameof(TextureDescription.DebugName),
    ];

    public static TheoryData<string> Fields() => [.. _fields];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Equals_ComparesEveryField(string field)
    {
        TextureDescription changed = field switch
        {
            nameof(TextureDescription.Format) => _description with { Format = TextureFormat.Bgra8Unorm },
            nameof(TextureDescription.Width) => _description with { Width = 256 },
            nameof(TextureDescription.Height) => _description with { Height = 128 },
            nameof(TextureDescription.ArrayLayers) => _description with { ArrayLayers = 2 },
            nameof(TextureDescription.MipLevels) => _description with { MipLevels = 3 },
            nameof(TextureDescription.SampleCount) => _description with { SampleCount = 4 },
            nameof(TextureDescription.Usage) => _description with { Usage = TextureUsage.Sampled },
            // Ordinal: a name that differs only in case is a different name.
            nameof(TextureDescription.DebugName) => _description with { DebugName = "Atlas" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        Assert.NotEqual(_description, changed);
        Assert.True(_description != changed);
    }

    [Fact]
    public void Equals_FieldRows_CoverEveryProperty()
    {
        Assert.Equal(
            typeof(TextureDescription).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal),
            _fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Equals_SameFields_AreEqual()
    {
        // A separately built name, so equality can't be passing by reference.
        TextureDescription copy = _description with { DebugName = new string("atlas".AsSpan()) };

        Assert.Equal(_description, copy);
        Assert.True(_description == copy);
        Assert.Equal(_description.GetHashCode(), copy.GetHashCode());
    }
}
