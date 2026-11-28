using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The pipeline validator's bind group layout and bind group rules (docs/design/graphics.md section 8).</summary>
public sealed partial class PipelineValidatorTests
{
    private static readonly TextureViewHandle _view = new(new Handle(3, 1));
    private static readonly SamplerHandle _sampler = new(new Handle(4, 1));
    private static readonly BufferHandle _buffer = new(new Handle(5, 1));

    public static TheoryData<string> InvalidLayouts() =>
        ["DuplicateAdjacent", "DuplicateFirstAndLast", "DuplicateOnlyTwo", "CountZero", "CountNegative", "CountMinValue"];

    [Theory]
    [MemberData(nameof(InvalidLayouts))]
    public void Layout_DuplicateBindingOrZeroCount_Throws(string row)
    {
        (BindGroupLayoutEntry[] entries, Type exception, string error) = row switch
        {
            "DuplicateAdjacent" => (new[] { Entry(0), Entry(3), Entry(3) }, typeof(ArgumentException), "Entries[2].Binding must be unique, but 3 is also Entries[1]'s"),
            "DuplicateFirstAndLast" => (new[] { Entry(5), Entry(1), Entry(5) }, typeof(ArgumentException), "Entries[2].Binding must be unique, but 5 is also Entries[0]'s"),
            "DuplicateOnlyTwo" => (new[] { Entry(0), Entry(0) }, typeof(ArgumentException), "Entries[1].Binding must be unique, but 0 is also Entries[0]'s"),
            "CountZero" => (new[] { Entry(0), Entry(1, count: 0) }, typeof(ArgumentOutOfRangeException), "Entries[1].Count must be at least 1, but is 0"),
            "CountNegative" => (new[] { Entry(0, count: -1) }, typeof(ArgumentOutOfRangeException), "Entries[0].Count must be at least 1, but is -1"),
            "CountMinValue" => (new[] { Entry(0, count: int.MinValue) }, typeof(ArgumentOutOfRangeException), "Entries[0].Count must be at least 1, but is -2147483648"),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        AssertRejected(exception, "BindGroupLayoutDescription." + error, () => PipelineValidator.Validate(Layout(entries)));
    }

    public static TheoryData<string> InvalidLayoutValues() =>
        ["BindingNegative", "BindingMinValue", "VisibilityUndefined", "VisibilitySignBit", "TypeNegative", "TypeAboveLast"];

    [Theory]
    [MemberData(nameof(InvalidLayoutValues))]
    public void Layout_ValueOutOfRange_Throws(string row)
    {
        (BindGroupLayoutEntry entry, string error) = row switch
        {
            "BindingNegative" => (Entry(-1), "Binding must be 0 or more, but is -1"),
            "BindingMinValue" => (Entry(int.MinValue), "Binding must be 0 or more, but is -2147483648"),
            "VisibilityUndefined" => (Entry(1) with { Visibility = ShaderStages.Fragment | (ShaderStages)8 }, "Visibility has flags that aren't ShaderStages members, but is 10"),
            "VisibilitySignBit" => (Entry(1) with { Visibility = (ShaderStages)int.MinValue }, "Visibility has flags that aren't ShaderStages members, but is -2147483648"),
            "TypeNegative" => (Entry(1) with { Type = (BindingType)(-1) }, "Type isn't a BindingType member, but is -1"),
            "TypeAboveLast" => (Entry(1) with { Type = (BindingType)6 }, "Type isn't a BindingType member, but is 6"),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        AssertRejected(
            typeof(ArgumentOutOfRangeException),
            "BindGroupLayoutDescription.Entries[1]." + error,
            () => PipelineValidator.Validate(Layout(Entry(0), entry)));
    }

    [Fact]
    public void Layout_Valid_Accepted()
    {
        PipelineValidator.Validate(Layout());
        PipelineValidator.Validate(Layout(Entry(0), Entry(int.MaxValue, count: int.MaxValue), Entry(2, count: 1)));
        foreach (BindingType type in Enum.GetValues<BindingType>())
        {
            PipelineValidator.Validate(Layout(Entry(0) with { Type = type }));
        }

        for (int stages = 0; stages <= 7; stages++)
        {
            PipelineValidator.Validate(Layout(Entry(0) with { Visibility = (ShaderStages)stages }));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(2)]
    public void BindGroup_UndefinedLifetime_Throws(int lifetime)
    {
        Span<BindGroupEntry> entries = [BindGroupEntry.FromSampler(0, _sampler)];
        var description = new BindGroupDescription { Layout = default, Entries = entries, Lifetime = (BindGroupLifetime)lifetime, DebugName = DebugName };

        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(Validate(description));

        AssertMessage(e, "BindGroupDescription.Lifetime isn't a BindGroupLifetime member");
    }

    public static TheoryData<string> InvalidBindGroups() =>
        ["DefaultFirst", "DefaultLater", "DuplicateAdjacent", "DuplicateFirstAndLast", "DuplicateAcrossKinds", "DuplicateArrayElement"];

    [Theory]
    [MemberData(nameof(InvalidBindGroups))]
    public void BindGroup_Invalid_Throws(string row)
    {
        var texture = BindGroupEntry.FromTexture(1, _view);
        var sampler = BindGroupEntry.FromSampler(2, _sampler);
        (BindGroupEntry[] entries, string error) = row switch
        {
            "DefaultFirst" => (new[] { default(BindGroupEntry), texture }, "Entries[0] must be made by a BindGroupEntry factory, but its resource is the null handle"),
            "DefaultLater" => (new[] { texture, sampler, default }, "Entries[2] must be made by a BindGroupEntry factory, but its resource is the null handle"),
            "DuplicateAdjacent" => (new[] { sampler, texture, texture }, "Entries[2] must not repeat a binding and array element, but binding 1 element 0 is also Entries[1]'s"),
            "DuplicateFirstAndLast" => (new[] { sampler, texture, sampler }, "Entries[2] must not repeat a binding and array element, but binding 2 element 0 is also Entries[0]'s"),
            "DuplicateAcrossKinds" => (new[] { texture, BindGroupEntry.FromSampler(1, _sampler) }, "Entries[1] must not repeat a binding and array element, but binding 1 element 0 is also Entries[0]'s"),
            "DuplicateArrayElement" => (
                new[] { BindGroupEntry.FromTexture(1, _view, 3), BindGroupEntry.FromTexture(1, new TextureViewHandle(new Handle(9, 1)), 3) },
                "Entries[1] must not repeat a binding and array element, but binding 1 element 3 is also Entries[0]'s"),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };
        var description = new BindGroupDescription { Layout = default, Entries = entries, DebugName = DebugName };

        ArgumentException e = Assert.Throws<ArgumentException>(Validate(description));

        AssertMessage(e, "BindGroupDescription." + error);
    }

    [Fact]
    public void BindGroup_Valid_Accepted()
    {
        Span<BindGroupEntry> entries =
        [
            BindGroupEntry.FromBuffer(0, new BufferBinding(_buffer)),
            BindGroupEntry.FromTexture(1, _view, 0),
            BindGroupEntry.FromTexture(1, _view, 1),   // same binding, another array element
            BindGroupEntry.FromSampler(2, _sampler, 1), // same array element, another binding
            BindGroupEntry.FromSampler(3, _sampler),
        ];

        foreach (BindGroupLifetime lifetime in Enum.GetValues<BindGroupLifetime>())
        {
            PipelineValidator.Validate(new BindGroupDescription { Layout = default, Entries = entries, Lifetime = lifetime });
        }

        PipelineValidator.Validate(new BindGroupDescription { Layout = default, Entries = [] });
    }

    // The ref struct can't be captured by Assert.Throws' lambda, so each row validates a copy made from its parts.
    private static Action Validate(BindGroupDescription description)
    {
        BindGroupEntry[] entries = description.Entries.ToArray();
        BindGroupLifetime lifetime = description.Lifetime;
        string? name = description.DebugName;
        return () => PipelineValidator.Validate(new BindGroupDescription { Layout = default, Entries = entries, Lifetime = lifetime, DebugName = name });
    }

    private static void AssertMessage(ArgumentException exception, string error)
    {
        Assert.Equal("description", exception.ParamName);
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
        Assert.Contains("(debug name 'probe')", exception.Message, StringComparison.Ordinal);
    }

    private static BindGroupLayoutEntry Entry(int binding, int count = 1) =>
        new(binding, ShaderStages.Vertex | ShaderStages.Fragment, BindingType.UniformBuffer, count);

    private static BindGroupLayoutDescription Layout(params BindGroupLayoutEntry[] entries) =>
        new() { Entries = entries, DebugName = DebugName };
}
