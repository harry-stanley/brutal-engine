using System;
using System.Globalization;
using System.Linq;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The adapter override and its text form (docs/design/graphics.md section 2).</summary>
public sealed class AdapterSelectionTests
{
    // Every selection: auto, any non-negative index, any non-empty name (any characters), every defined type.
    private static readonly Gen<AdapterSelection> _selections = Gen.OneOf(
        Gen.Const(AdapterSelection.Auto),
        Gen.Int[0, int.MaxValue].Select(AdapterSelection.ByIndex),
        Gen.Char.Array[1, 20].Select(chars => AdapterSelection.ByName(new string(chars))),
        Gen.Enum<AdapterType>().Select(AdapterSelection.ByType));

    [Theory]
    [InlineData("auto", "Auto", null)]
    [InlineData("index:0", "ByIndex", "0")]
    [InlineData("index:7", "ByIndex", "7")]
    [InlineData("index:007", "ByIndex", "7")]
    [InlineData("index:2147483647", "ByIndex", "2147483647")]
    [InlineData("name:NVIDIA", "ByName", "NVIDIA")]
    [InlineData("name:rtx 2070", "ByName", "rtx 2070")]
    [InlineData("name: padded ", "ByName", " padded ")]
    [InlineData("name:type:1,2", "ByName", "type:1,2")]
    [InlineData("name:auto", "ByName", "auto")]
    [InlineData("type:other", "ByType", "Other")]
    [InlineData("type:discrete", "ByType", "Discrete")]
    [InlineData("type:integrated", "ByType", "Integrated")]
    [InlineData("type:virtual", "ByType", "Virtual")]
    [InlineData("type:cpu", "ByType", "Cpu")]
    public void TryParse_ValidForms(string text, string factory, string? argument)
    {
        Assert.True(AdapterSelection.TryParse(text, out AdapterSelection selection));
        Assert.Equal(Create(factory, argument), selection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("auto ")]
    [InlineData(" auto")]
    [InlineData("Auto")]
    [InlineData("AUTO")]
    [InlineData("auto:")]
    [InlineData("automatic")]
    [InlineData("type:1")]
    [InlineData("type:0")]
    [InlineData("type:-1")]
    [InlineData("type:Discrete")]
    [InlineData("type:DISCRETE")]
    [InlineData("type: discrete")]
    [InlineData("type:discrete ")]
    [InlineData("type:discrete,integrated")]
    [InlineData("type:gpu")]
    [InlineData("type:")]
    [InlineData("type")]
    [InlineData("Type:discrete")]
    [InlineData("index:-1")]
    [InlineData("index:+1")]
    [InlineData("index: 1")]
    [InlineData("index:1 ")]
    [InlineData("index:1,2")]
    [InlineData("index:1.0")]
    [InlineData("index:0x1")]
    [InlineData("index:1e3")]
    [InlineData("index:2147483648")]
    [InlineData("index:\u0661")]
    [InlineData("index:\uff11")]
    [InlineData("index:")]
    [InlineData("index")]
    [InlineData("Index:1")]
    [InlineData("name:")]
    [InlineData("name")]
    [InlineData("NAME:x")]
    [InlineData("gpu:1")]
    [InlineData(":")]
    [InlineData("discrete")]
    public void TryParse_InvalidForms_ReturnFalse(string? text)
    {
        Assert.False(AdapterSelection.TryParse(text, out AdapterSelection selection));
        Assert.Equal(default, selection);
    }

    [Fact]
    public void RoundTrip()
    {
        _selections.Sample(selection =>
        {
            string text = selection.ToString();

            Assert.True(AdapterSelection.TryParse(text, out AdapterSelection parsed));
            Assert.Equal(selection, parsed);
            Assert.Equal(text, parsed.ToString());
        });
    }

    [Theory]
    [InlineData("Auto", null, "auto")]
    [InlineData("ByIndex", "0", "index:0")]
    [InlineData("ByIndex", "2147483647", "index:2147483647")]
    [InlineData("ByName", " NVIDIA, Corp ", "name: NVIDIA, Corp ")]
    [InlineData("ByType", "Other", "type:other")]
    [InlineData("ByType", "Discrete", "type:discrete")]
    [InlineData("ByType", "Integrated", "type:integrated")]
    [InlineData("ByType", "Virtual", "type:virtual")]
    [InlineData("ByType", "Cpu", "type:cpu")]
    public void ToString_ReturnsTheTextForm(string factory, string? argument, string expected)
    {
        Assert.Equal(expected, Create(factory, argument).ToString());
    }

    [Fact]
    public void ToString_UnderAnotherCulture_UsesInvariantDigits()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        try
        {
            Assert.Equal("index:1234567", AdapterSelection.ByIndex(1234567).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Factories_Misuse_Throws()
    {
        Assert.Equal("index", Assert.Throws<ArgumentOutOfRangeException>(() => AdapterSelection.ByIndex(-1)).ParamName);
        Assert.Equal("index", Assert.Throws<ArgumentOutOfRangeException>(() => AdapterSelection.ByIndex(int.MinValue)).ParamName);
        Assert.Equal("nameSubstring", Assert.Throws<ArgumentNullException>(() => AdapterSelection.ByName(null!)).ParamName);
        Assert.Equal("nameSubstring", Assert.Throws<ArgumentException>(() => AdapterSelection.ByName(string.Empty)).ParamName);
        Assert.Equal("type", Assert.Throws<ArgumentOutOfRangeException>(() => AdapterSelection.ByType((AdapterType)(-1))).ParamName);
        Assert.Equal("type", Assert.Throws<ArgumentOutOfRangeException>(() => AdapterSelection.ByType((AdapterType)5)).ParamName);
    }

    [Fact]
    public void ByType_AcceptsExactlyTheDefinedValues()
    {
        foreach (int value in new[] { int.MinValue, -2, -1, 0, 1, 2, 3, 4, 5, 6, int.MaxValue })
        {
            var type = (AdapterType)value;
            bool accepted = Record.Exception(() => AdapterSelection.ByType(type)) is null;

            Assert.Equal(Enum.IsDefined(type), accepted);
        }
    }

    [Fact]
    public void Factories_StoreTheKindAndArgument()
    {
        Assert.Equal((AdapterSelectionKind.Auto, 0, (string?)null, AdapterType.Other), Parts(AdapterSelection.Auto));
        Assert.Equal((AdapterSelectionKind.Index, 3, (string?)null, AdapterType.Other), Parts(AdapterSelection.ByIndex(3)));
        Assert.Equal((AdapterSelectionKind.Name, 0, "Intel", AdapterType.Other), Parts(AdapterSelection.ByName("Intel")));
        Assert.Equal((AdapterSelectionKind.Type, 0, (string?)null, AdapterType.Cpu), Parts(AdapterSelection.ByType(AdapterType.Cpu)));
    }

    [Fact]
    public void Auto_IsTheDefaultValue()
    {
        Assert.Equal(default, AdapterSelection.Auto);
        Assert.Equal("auto", default(AdapterSelection).ToString());
    }

    [Fact]
    public void Equals_NamesDifferingOnlyInCase_AreNotEqual()
    {
        Assert.Equal(AdapterSelection.ByName("Intel"), AdapterSelection.ByName("Intel"));
        Assert.NotEqual(AdapterSelection.ByName("Intel"), AdapterSelection.ByName("intel"));
        Assert.NotEqual(AdapterSelection.ByIndex(0), AdapterSelection.Auto);
        Assert.NotEqual(AdapterSelection.ByType(AdapterType.Other), AdapterSelection.Auto);
    }

    [Fact]
    public void AdapterType_Values_MatchDesign()
    {
        Assert.False(typeof(AdapterType).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(AdapterType)));
        Assert.Equal(
            [("Other", 0), ("Discrete", 1), ("Integrated", 2), ("Virtual", 3), ("Cpu", 4)],
            Enum.GetValues<AdapterType>().Select(v => (v.ToString(), (int)v)));
    }

    private static (AdapterSelectionKind, int, string?, AdapterType) Parts(AdapterSelection selection) =>
        (selection.Kind, selection.Index, selection.Name, selection.Type);

    private static AdapterSelection Create(string factory, string? argument) => factory switch
    {
        "Auto" => AdapterSelection.Auto,
        "ByIndex" => AdapterSelection.ByIndex(int.Parse(argument!, CultureInfo.InvariantCulture)),
        "ByName" => AdapterSelection.ByName(argument!),
        "ByType" => AdapterSelection.ByType(Enum.Parse<AdapterType>(argument!)),
        _ => throw new ArgumentOutOfRangeException(nameof(factory), factory, "Unknown factory."),
    };
}
