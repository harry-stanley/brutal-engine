using System;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The bind group entry factories and read side (docs/design/graphics.md section 8).</summary>
public sealed class BindGroupEntryTests
{
    private static readonly BufferHandle _buffer = new(new Handle(1, 2));
    private static readonly BufferHandle _alias = new(new Handle(7, 3));
    private static readonly TextureViewHandle _view = new(new Handle(4, 5));
    private static readonly SamplerHandle _sampler = new(new Handle(6, 1));

    public static TheoryData<string> Kinds() => ["Buffer", "Transient", "Texture", "Sampler"];

    [Theory]
    [InlineData("Buffer", 3, 0)]
    [InlineData("Buffer", 0, 0)]
    [InlineData("Transient", 5, 0)]
    [InlineData("Transient", 0, 0)]
    [InlineData("Texture", 2, 4)]
    [InlineData("Texture", 0, 0)]
    [InlineData("Sampler", 9, 1)]
    [InlineData("Sampler", 0, 0)]
    public void Factories_AndReadSide(string kind, int binding, int arrayElement)
    {
        byte[] ring = new byte[64];
        BindGroupEntry entry = kind switch
        {
            "Buffer" => BindGroupEntry.FromBuffer(binding, new BufferBinding(_buffer, 256, 128)),
            "Transient" => BindGroupEntry.FromTransient(binding, new TransientAllocation(_alias, 512, ring), 48),
            "Texture" => BindGroupEntry.FromTexture(binding, _view, arrayElement),
            _ => BindGroupEntry.FromSampler(binding, _sampler, arrayElement),
        };

        Assert.Equal(binding, entry.Binding);
        Assert.Equal(arrayElement, entry.ArrayElement);
        switch (kind)
        {
            case "Buffer":
                Assert.Equal(BindGroupEntryKind.Buffer, entry.Kind);
                Assert.Equal(new BufferBinding(_buffer, 256, 128), entry.Buffer);
                break;
            case "Transient":
                // The transient alias: the ring's alias handle, the allocation's offset and the size given.
                Assert.Equal(BindGroupEntryKind.Buffer, entry.Kind);
                Assert.Equal(new BufferBinding(_alias, 512, 48), entry.Buffer);
                break;
            case "Texture":
                Assert.Equal(BindGroupEntryKind.Texture, entry.Kind);
                Assert.Equal(_view, entry.View);
                break;
            default:
                Assert.Equal(BindGroupEntryKind.Sampler, entry.Kind);
                Assert.Equal(_sampler, entry.Sampler);
                break;
        }
    }

    [Fact]
    public void Factories_DefaultArrayElement_IsZero()
    {
        Assert.Equal(0, BindGroupEntry.FromTexture(1, _view).ArrayElement);
        Assert.Equal(0, BindGroupEntry.FromSampler(1, _sampler).ArrayElement);
    }

    [Fact]
    public void WrongProperty_Throws()
    {
        var buffer = BindGroupEntry.FromBuffer(0, new BufferBinding(_buffer));
        var texture = BindGroupEntry.FromTexture(1, _view);
        var sampler = BindGroupEntry.FromSampler(2, _sampler);

        AssertWrongKind(() => buffer.View, "binding 0 is a Buffer entry, not a Texture entry");
        AssertWrongKind(() => buffer.Sampler, "binding 0 is a Buffer entry, not a Sampler entry");
        AssertWrongKind(() => texture.Buffer, "binding 1 is a Texture entry, not a Buffer entry");
        AssertWrongKind(() => texture.Sampler, "binding 1 is a Texture entry, not a Sampler entry");
        AssertWrongKind(() => sampler.Buffer, "binding 2 is a Sampler entry, not a Buffer entry");
        AssertWrongKind(() => sampler.View, "binding 2 is a Sampler entry, not a Texture entry");
    }

    [Fact]
    public void DefaultEntry_IsANullBufferEntry()
    {
        BindGroupEntry entry = default;

        Assert.Equal(BindGroupEntryKind.Buffer, entry.Kind);
        Assert.Equal(0, entry.Binding);
        Assert.True(entry.Buffer.Buffer.IsNull);
        Assert.Throws<InvalidOperationException>(() => entry.View);
        Assert.Throws<InvalidOperationException>(() => entry.Sampler);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Factories_NegativeBinding_Throws(string kind)
    {
        foreach (int binding in new[] { -1, int.MinValue })
        {
            ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => Make(kind, binding, arrayElement: 0));
            Assert.Equal("binding", e.ParamName);
        }
    }

    [Theory]
    [InlineData("Texture", -1)]
    [InlineData("Texture", int.MinValue)]
    [InlineData("Sampler", -1)]
    [InlineData("Sampler", int.MinValue)]
    public void Factories_NegativeArrayElement_Throws(string kind, int arrayElement)
    {
        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => Make(kind, binding: 1, arrayElement));

        Assert.Equal("arrayElement", e.ParamName);
    }

    [Fact]
    public void Factories_NullHandle_Throws()
    {
        byte[] ring = new byte[16];

        AssertNull(() => BindGroupEntry.FromBuffer(0, new BufferBinding(default, 0, 16)), "buffer");
        AssertNull(() => BindGroupEntry.FromTransient(0, new TransientAllocation(default, 0, ring), 16), "allocation");
        AssertNull(() => BindGroupEntry.FromTransient(0, default, 16), "allocation");
        AssertNull(() => BindGroupEntry.FromTexture(0, default), "view");
        AssertNull(() => BindGroupEntry.FromSampler(0, default), "sampler");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(65L)]
    [InlineData(long.MaxValue)]
    public void FromTransient_SizeOutsideAllocation_Throws(long size)
    {
        byte[] ring = new byte[64];

        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(
            () => BindGroupEntry.FromTransient(0, new TransientAllocation(_alias, 0, ring), size));

        Assert.Equal("size", e.ParamName);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(64L)]
    public void FromTransient_SizeInsideAllocation_IsBound(long size)
    {
        byte[] ring = new byte[64];

        var entry = BindGroupEntry.FromTransient(0, new TransientAllocation(_alias, 16, ring), size);

        Assert.Equal(new BufferBinding(_alias, 16, size), entry.Buffer);
    }

    [Fact]
    public void FromTransient_SizeIsCheckedAgainstTheAllocationsOwnLength()
    {
        // A 32-byte allocation at offset 512 inside a larger ring: its own length is the limit, not the ring's and not
        // its offset plus its length.
        byte[] ring = new byte[64];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => BindGroupEntry.FromTransient(0, new TransientAllocation(_alias, 512, ring.AsSpan(0, 32)), 33));
        Assert.Equal(32, BindGroupEntry.FromTransient(0, new TransientAllocation(_alias, 512, ring.AsSpan(0, 32)), 32).Buffer.Size);
    }

    [Fact]
    public void Equality_ComparesKindBindingElementAndResource()
    {
        Assert.Equal(BindGroupEntry.FromTexture(1, _view, 2), BindGroupEntry.FromTexture(1, _view, 2));
        Assert.NotEqual(BindGroupEntry.FromTexture(1, _view, 2), BindGroupEntry.FromTexture(1, _view, 3));
        Assert.NotEqual(BindGroupEntry.FromTexture(1, _view), BindGroupEntry.FromTexture(2, _view));
        Assert.NotEqual(BindGroupEntry.FromTexture(1, _view), BindGroupEntry.FromTexture(1, new TextureViewHandle(new Handle(4, 6))));
        Assert.NotEqual(BindGroupEntry.FromBuffer(1, new BufferBinding(_buffer)), BindGroupEntry.FromBuffer(1, new BufferBinding(_buffer, 4)));
        Assert.NotEqual(BindGroupEntry.FromSampler(1, _sampler), BindGroupEntry.FromSampler(1, new SamplerHandle(new Handle(6, 2))));
    }

    private static BindGroupEntry Make(string kind, int binding, int arrayElement)
    {
        byte[] ring = new byte[16];
        return kind switch
        {
            "Buffer" => BindGroupEntry.FromBuffer(binding, new BufferBinding(_buffer)),
            "Transient" => BindGroupEntry.FromTransient(binding, new TransientAllocation(_alias, 0, ring), 16),
            "Texture" => BindGroupEntry.FromTexture(binding, _view, arrayElement),
            _ => BindGroupEntry.FromSampler(binding, _sampler, arrayElement),
        };
    }

    private static void AssertWrongKind(Func<object> read, string message)
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(read);
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    private static void AssertNull(Action make, string paramName)
    {
        ArgumentException e = Assert.Throws<ArgumentException>(make);
        Assert.Equal(paramName, e.ParamName);
    }
}
