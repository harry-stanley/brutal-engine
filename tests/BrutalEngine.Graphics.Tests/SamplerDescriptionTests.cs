using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The sampler description's equality and text form (docs/design/graphics.md section 5).</summary>
public sealed class SamplerDescriptionTests
{
    private static readonly SamplerDescription _description = new()
    {
        MinFilter = Filter.Linear,
        MagFilter = Filter.Linear,
        MipmapFilter = Filter.Linear,
        AddressU = AddressMode.Repeat,
        AddressV = AddressMode.MirrorRepeat,
        BorderColor = BorderColor.OpaqueWhite,
        MaxAnisotropy = 4.5f,
        Compare = CompareFunction.LessOrEqual,
        DebugName = "shadow",
    };

    [Fact]
    public void ToString_UnderAnotherCulture_ListsEveryFieldInvariantly()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        // German writes 4.5 as "4,5".
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(
                "SamplerDescription { MinFilter = Linear, MagFilter = Linear, MipmapFilter = Linear, AddressU = Repeat, "
                + "AddressV = MirrorRepeat, BorderColor = OpaqueWhite, MaxAnisotropy = 4.5, Compare = LessOrEqual, DebugName = shadow }",
                _description.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToString_Defaults_LeavesCompareAndDebugNameEmpty()
    {
        Assert.Equal(
            "SamplerDescription { MinFilter = Nearest, MagFilter = Nearest, MipmapFilter = Nearest, AddressU = ClampToEdge, "
            + "AddressV = ClampToEdge, BorderColor = TransparentBlack, MaxAnisotropy = 1, Compare = , DebugName =  }",
            new SamplerDescription().ToString());
    }

    // One row per SamplerDescription property; Equals_FieldRows_CoverEveryProperty fails until a new property adds its row.
    private static readonly string[] _fields =
    [
        nameof(SamplerDescription.MinFilter),
        nameof(SamplerDescription.MagFilter),
        nameof(SamplerDescription.MipmapFilter),
        nameof(SamplerDescription.AddressU),
        nameof(SamplerDescription.AddressV),
        nameof(SamplerDescription.BorderColor),
        nameof(SamplerDescription.MaxAnisotropy),
        nameof(SamplerDescription.Compare),
        nameof(SamplerDescription.DebugName),
    ];

    public static TheoryData<string> Fields() => [.. _fields];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Equals_ComparesEveryField(string field)
    {
        SamplerDescription changed = field switch
        {
            nameof(SamplerDescription.MinFilter) => _description with { MinFilter = Filter.Nearest },
            nameof(SamplerDescription.MagFilter) => _description with { MagFilter = Filter.Nearest },
            nameof(SamplerDescription.MipmapFilter) => _description with { MipmapFilter = Filter.Nearest },
            nameof(SamplerDescription.AddressU) => _description with { AddressU = AddressMode.ClampToBorder },
            nameof(SamplerDescription.AddressV) => _description with { AddressV = AddressMode.Repeat },
            nameof(SamplerDescription.BorderColor) => _description with { BorderColor = BorderColor.OpaqueBlack },
            // The next float up: anisotropy compares exactly.
            nameof(SamplerDescription.MaxAnisotropy) => _description with { MaxAnisotropy = MathF.BitIncrement(4.5f) },
            nameof(SamplerDescription.Compare) => _description with { Compare = null },
            // Ordinal: a name that differs only in case is a different name.
            nameof(SamplerDescription.DebugName) => _description with { DebugName = "Shadow" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        Assert.NotEqual(_description, changed);
        Assert.True(_description != changed);
    }

    [Fact]
    public void Equals_FieldRows_CoverEveryProperty()
    {
        Assert.Equal(
            typeof(SamplerDescription).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal),
            _fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Equals_SameFields_AreEqual()
    {
        // A separately built name, so equality can't be passing by reference.
        SamplerDescription copy = _description with { DebugName = new string("shadow".AsSpan()) };

        Assert.Equal(_description, copy);
        Assert.True(_description == copy);
        Assert.Equal(_description.GetHashCode(), copy.GetHashCode());
    }
}
