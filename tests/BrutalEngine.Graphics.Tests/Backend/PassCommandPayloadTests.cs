using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The copy, barrier and pass payload structs of the command stream (docs/design/graphics.md, "The frame plan"): their
/// shape, checked against a table written from the note, and that their properties keep what they're given.
/// </summary>
public sealed class PassCommandPayloadTests
{
    public static TheoryData<string> Payloads() =>
    [
        "BarrierCommand", "CopyBufferToBufferCommand", "CopyBufferTextureCommand", "CopyTextureToTextureCommand",
        "BeginRenderPassCommand", "AttachmentCommand", "BeginComputePassCommand", "EndPassCommand",
        "ClearSwapchainImageCommand",
    ];

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Payloads_AreUnmanagedAndReadOnly(string type)
    {
        Type clrType = PayloadType(type);

        Assert.True(clrType.IsPublic);
        Assert.True(clrType.IsValueType);
        Assert.False(clrType.IsEnum);
        Assert.True(clrType.IsDefined(typeof(IsReadOnlyAttribute), inherit: false));
        // The stream stores payloads as raw bytes, so they may hold no references.
        bool containsReferences = (bool)typeof(RuntimeHelpers)
            .GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
            .MakeGenericMethod(clrType)
            .Invoke(null, null)!;
        Assert.False(containsReferences);
        Assert.Empty(clrType.GetFields(BindingFlags.Public | BindingFlags.Instance));
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Fields_MatchDesign(string type)
    {
        // Written from the note's payload table and the layout decisions recorded with it, not from the code: each
        // struct's properties in declaration order. Ids are ulong, labels DebugLabel, timestamp query indices int.
        (string Name, Type Type)[] expected = type switch
        {
            "BarrierCommand" => [("Count", typeof(int))],
            "CopyBufferToBufferCommand" =>
            [
                ("Source", typeof(ulong)), ("SourceOffset", typeof(long)), ("Destination", typeof(ulong)),
                ("DestinationOffset", typeof(long)), ("Size", typeof(long)),
            ],
            "CopyBufferTextureCommand" =>
            [
                ("Buffer", typeof(ulong)), ("BufferOffset", typeof(long)), ("BytesPerRow", typeof(int)), ("Texture", typeof(ulong)),
                ("MipLevel", typeof(int)), ("ArrayLayer", typeof(int)), ("Aspect", typeof(TextureAspect)),
                ("Region", typeof(TextureRegion)),
            ],
            "CopyTextureToTextureCommand" =>
            [
                ("Source", typeof(ulong)), ("SourceMipLevel", typeof(int)), ("SourceArrayLayer", typeof(int)),
                ("SourceAspect", typeof(TextureAspect)), ("Destination", typeof(ulong)), ("DestinationMipLevel", typeof(int)),
                ("DestinationArrayLayer", typeof(int)), ("DestinationAspect", typeof(TextureAspect)),
                ("SourceRegion", typeof(TextureRegion)), ("DestinationX", typeof(int)), ("DestinationY", typeof(int)),
            ],
            "BeginRenderPassCommand" =>
            [
                ("ColorCount", typeof(int)), ("HasDepth", typeof(bool)), ("Label", typeof(DebugLabel)),
                ("TimestampQueryIndex", typeof(int)),
            ],
            "AttachmentCommand" =>
            [
                ("View", typeof(ulong)), ("Load", typeof(LoadOp)), ("Store", typeof(StoreOp)), ("ClearColor", typeof(ColorValue)),
                ("ClearDepth", typeof(float)),
            ],
            "BeginComputePassCommand" => [("Label", typeof(DebugLabel)), ("TimestampQueryIndex", typeof(int))],
            "EndPassCommand" => [("TimestampQueryIndex", typeof(int))],
            "ClearSwapchainImageCommand" => [("AcquiredIndex", typeof(int))],
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown payload."),
        };

        PropertyInfo[] properties = [.. PayloadType(type)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.MetadataToken)];

        Assert.Equal(expected, properties.Select(p => (p.Name, p.PropertyType)));
        foreach (PropertyInfo property in properties)
        {
            Assert.NotNull(property.GetMethod);
            Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }
    }

    [Fact]
    public void Constructors_KeepEveryField()
    {
        // Distinct values per property, so a property that returned another's value would show.
        var label = new DebugLabel(new Handle(7, 3));
        var region = new TextureRegion(1, 2, 3, 4);
        var otherRegion = new TextureRegion(5, 6, 7, 8);
        var color = new ColorValue(0.1f, 0.2f, 0.3f, 0.4f);

        BarrierCommand barrier = new() { Count = 3 };
        CopyBufferToBufferCommand bufferCopy = new()
        {
            Source = 11,
            SourceOffset = 12,
            Destination = 13,
            DestinationOffset = 14,
            Size = 15,
        };
        CopyBufferTextureCommand bufferTexture = new()
        {
            Buffer = 21,
            BufferOffset = 22,
            BytesPerRow = 23,
            Texture = 24,
            MipLevel = 25,
            ArrayLayer = 26,
            Aspect = TextureAspect.Depth,
            Region = region,
        };
        CopyTextureToTextureCommand textureCopy = new()
        {
            Source = 31,
            SourceMipLevel = 32,
            SourceArrayLayer = 33,
            SourceAspect = TextureAspect.Depth,
            Destination = 34,
            DestinationMipLevel = 35,
            DestinationArrayLayer = 36,
            DestinationAspect = TextureAspect.Color,
            SourceRegion = otherRegion,
            DestinationX = 37,
            DestinationY = 38,
        };
        BeginRenderPassCommand renderPass = new() { ColorCount = 2, HasDepth = true, Label = label, TimestampQueryIndex = 41 };
        AttachmentCommand attachment = new()
        {
            View = 51,
            Load = LoadOp.Clear,
            Store = StoreOp.DontCare,
            ClearColor = color,
            ClearDepth = 0.5f,
        };
        BeginComputePassCommand computePass = new() { Label = label, TimestampQueryIndex = 61 };
        EndPassCommand endPass = new() { TimestampQueryIndex = -1 };
        ClearSwapchainImageCommand clear = new() { AcquiredIndex = 71 };

        Assert.Equal(3, barrier.Count);
        Assert.Equal(
            (11ul, 12L, 13ul, 14L, 15L),
            (bufferCopy.Source, bufferCopy.SourceOffset, bufferCopy.Destination, bufferCopy.DestinationOffset, bufferCopy.Size));
        Assert.Equal(
            (21ul, 22L, 23, 24ul, 25, 26, TextureAspect.Depth, region),
            (bufferTexture.Buffer, bufferTexture.BufferOffset, bufferTexture.BytesPerRow, bufferTexture.Texture,
                bufferTexture.MipLevel, bufferTexture.ArrayLayer, bufferTexture.Aspect, bufferTexture.Region));
        Assert.Equal(
            (31ul, 32, 33, TextureAspect.Depth, 34ul, 35, 36),
            (textureCopy.Source, textureCopy.SourceMipLevel, textureCopy.SourceArrayLayer, textureCopy.SourceAspect,
                textureCopy.Destination, textureCopy.DestinationMipLevel, textureCopy.DestinationArrayLayer));
        Assert.Equal(
            (TextureAspect.Color, otherRegion, 37, 38),
            (textureCopy.DestinationAspect, textureCopy.SourceRegion, textureCopy.DestinationX, textureCopy.DestinationY));
        Assert.Equal(
            (2, true, label, 41),
            (renderPass.ColorCount, renderPass.HasDepth, renderPass.Label, renderPass.TimestampQueryIndex));
        Assert.Equal(
            (51ul, LoadOp.Clear, StoreOp.DontCare, color, 0.5f),
            (attachment.View, attachment.Load, attachment.Store, attachment.ClearColor, attachment.ClearDepth));
        Assert.Equal((label, 61), (computePass.Label, computePass.TimestampQueryIndex));
        Assert.Equal(-1, endPass.TimestampQueryIndex);
        Assert.Equal(71, clear.AcquiredIndex);
    }

    // Looked up by name, so a missing payload is a test failure rather than a build break.
    private static Type PayloadType(string name)
    {
        Type? type = typeof(CommandKind).Assembly.GetType("BrutalEngine.Graphics.Backend." + name);
        Assert.NotNull(type);
        return type;
    }
}
