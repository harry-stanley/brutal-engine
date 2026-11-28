using System;
using System.Globalization;
using System.Linq;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The bind group enums, defaults and diagnostics, written from docs/design/graphics.md section 8.</summary>
public sealed class BindGroupTypesTests
{
    [Fact]
    public void BindingType_Values_MatchDesign() => AssertValues<BindingType>(
        ("UniformBuffer", 0), ("StorageBuffer", 1), ("ReadOnlyStorageBuffer", 2), ("SampledTexture", 3), ("StorageTexture", 4), ("Sampler", 5));

    [Fact]
    public void BindGroupEntryKind_Values_MatchDesign() => AssertValues<BindGroupEntryKind>(("Buffer", 0), ("Texture", 1), ("Sampler", 2));

    [Fact]
    public void BindGroupLifetime_Values_MatchDesign() => AssertValues<BindGroupLifetime>(("Persistent", 0), ("Frame", 1));

    [Fact]
    public void Defaults_MatchDesign()
    {
        var layoutEntry = new BindGroupLayoutEntry(0, ShaderStages.Fragment, BindingType.SampledTexture);
        var binding = new BufferBinding(new BufferHandle(new Handle(1, 1)));
        var layout = new BindGroupLayoutDescription { Entries = new[] { layoutEntry } };
        Span<BindGroupEntry> entries = stackalloc BindGroupEntry[1];
        var group = new BindGroupDescription { Layout = default, Entries = entries };

        Assert.Equal(1, layoutEntry.Count);
        Assert.Equal(0, binding.Offset);
        Assert.Equal(0, binding.Size);
        Assert.Null(layout.DebugName);
        Assert.Equal(BindGroupLifetime.Persistent, group.Lifetime);
        Assert.Null(group.DebugName);
        Assert.Equal(1, group.Entries.Length);
    }

    [Fact]
    public void ToString_UnderAnotherCulture_IsInvariant()
    {
        byte[] ring = new byte[8];
        var alias = new BufferHandle(new Handle(7, 3));

        Assert.Equal(
            "BufferBinding { Buffer = Buffer(1:2), Offset = -4, Size = -8 }",
            CultureProbe.Format(() => new BufferBinding(new BufferHandle(new Handle(1, 2)), -4, -8).ToString()));
        Assert.Equal(
            "BindGroupLayoutEntry { Binding = -1, Visibility = Vertex, Fragment, Type = Sampler, Count = -2 }",
            CultureProbe.Format(() => new BindGroupLayoutEntry(-1, ShaderStages.Vertex | ShaderStages.Fragment, BindingType.Sampler, -2).ToString()));
        Assert.Equal(
            "BindGroupEntry { Kind = Buffer, Binding = 3, ArrayElement = 0, Buffer = BufferBinding { Buffer = Buffer(7:3), Offset = -16, Size = 8 } }",
            CultureProbe.Format(() => BindGroupEntry.FromTransient(3, new TransientAllocation(alias, -16, ring), 8).ToString()));
        Assert.Equal(
            "BindGroupEntry { Kind = Texture, Binding = 1, ArrayElement = 2, View = TextureView(4:5) }",
            CultureProbe.Format(() => BindGroupEntry.FromTexture(1, new TextureViewHandle(new Handle(4, 5)), 2).ToString()));
        Assert.Equal(
            "BindGroupEntry { Kind = Sampler, Binding = 0, ArrayElement = 1, Sampler = Sampler(6:1) }",
            CultureProbe.Format(() => BindGroupEntry.FromSampler(0, new SamplerHandle(new Handle(6, 1)), 1).ToString()));
    }

    private static void AssertValues<T>(params (string Name, int Value)[] expected)
        where T : struct, Enum
    {
        Assert.False(typeof(T).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(T)));
        Assert.Equal(expected, Enum.GetValues<T>().Select(v => (v.ToString(), Convert.ToInt32(v, CultureInfo.InvariantCulture))));
    }
}
