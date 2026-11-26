using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The state, draw, push-constant and debug payload structs of the command stream (docs/design/graphics.md, "The frame
/// plan"): their shape, checked against a table written from the note, and that their properties keep what they're
/// given.
/// </summary>
public sealed class DrawCommandPayloadTests
{
    public static TheoryData<string> Payloads() =>
    [
        "SetPipelineCommand", "SetBindGroupCommand", "SetVertexBufferCommand", "SetIndexBufferCommand", "SetViewportCommand",
        "SetScissorCommand", "PushConstantsCommand", "DrawCommand", "DrawIndexedCommand", "DispatchCommand",
        "DebugGroupCommand",
    ];

    // Worked out by hand from the field table in Fields_MatchDesign: sequential layout, each field at its natural
    // alignment (bool 1, int, float and enums 4, ulong, long and DebugLabel 8; the section-9 records are 4-aligned:
    // Viewport 24 bytes, ScissorRect and DrawArguments 16, DrawIndexedArguments 20), the size rounded up to the
    // largest alignment. A literal, so a field that changes the record's size in the stream fails here.
    public static TheoryData<string, int> Sizes() => new()
    {
        { "SetPipelineCommand", 16 },     // ulong 0, bool 8, padded to 16
        { "SetBindGroupCommand", 16 },    // int 0, ulong 8
        { "SetVertexBufferCommand", 24 }, // int 0, ulong 8, long 16
        { "SetIndexBufferCommand", 24 },  // IndexFormat 0, ulong 8, long 16
        { "SetViewportCommand", 24 },     // Viewport 0
        { "SetScissorCommand", 16 },      // ScissorRect 0
        { "PushConstantsCommand", 12 },   // ShaderStages 0, int 4, int 8
        { "DrawCommand", 16 },            // DrawArguments 0
        { "DrawIndexedCommand", 20 },     // DrawIndexedArguments 0
        { "DispatchCommand", 12 },        // int 0, 4, 8
        { "DebugGroupCommand", 8 },       // DebugLabel 0
    };

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
        // struct's properties in declaration order. Ids are ulong; push-constant stages are the existing ShaderStages.
        (string Name, Type Type)[] expected = type switch
        {
            "SetPipelineCommand" => [("Pipeline", typeof(ulong)), ("IsCompute", typeof(bool))],
            "SetBindGroupCommand" => [("Index", typeof(int)), ("BindGroup", typeof(ulong))],
            "SetVertexBufferCommand" => [("Slot", typeof(int)), ("Buffer", typeof(ulong)), ("Offset", typeof(long))],
            "SetIndexBufferCommand" => [("Format", typeof(IndexFormat)), ("Buffer", typeof(ulong)), ("Offset", typeof(long))],
            "SetViewportCommand" => [("Viewport", typeof(Viewport))],
            "SetScissorCommand" => [("Scissor", typeof(ScissorRect))],
            "PushConstantsCommand" => [("Stages", typeof(ShaderStages)), ("Offset", typeof(int)), ("Size", typeof(int))],
            "DrawCommand" => [("Arguments", typeof(DrawArguments))],
            "DrawIndexedCommand" => [("Arguments", typeof(DrawIndexedArguments))],
            "DispatchCommand" => [("GroupCountX", typeof(int)), ("GroupCountY", typeof(int)), ("GroupCountZ", typeof(int))],
            "DebugGroupCommand" => [("Label", typeof(DebugLabel))],
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

        // Every property is auto-implemented, so each has exactly one backing field. Counting the non-public fields
        // too means a hidden member (an internal property or a private field) also changes the result.
        FieldInfo[] fields = PayloadType(type).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Equal(expected.Length, fields.Length);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Size_MatchesDesignedLayout(string type, int size)
    {
        int actual = (int)typeof(Unsafe)
            .GetMethod(nameof(Unsafe.SizeOf))!
            .MakeGenericMethod(PayloadType(type))
            .Invoke(null, null)!;

        Assert.Equal(size, actual);
    }

    [Fact]
    public void Constructors_KeepEveryField()
    {
        // Distinct values per property, so a property that returned another's value would show.
        var label = new DebugLabel(new Handle(9, 4));
        var viewport = new Viewport(1, 2, 3, 4, 0.25f, 0.75f);
        var scissor = new ScissorRect(5, 6, 7, 8);
        var draw = new DrawArguments(9, 10, 11, 12);
        var drawIndexed = new DrawIndexedArguments(13, 14, 15, -16, 17);

        SetPipelineCommand pipeline = new() { Pipeline = 21, IsCompute = true };
        SetBindGroupCommand bindGroup = new() { Index = 2, BindGroup = 31 };
        SetVertexBufferCommand vertexBuffer = new() { Slot = 3, Buffer = 41, Offset = 42 };
        SetIndexBufferCommand indexBuffer = new() { Format = IndexFormat.Uint32, Buffer = 51, Offset = 52 };
        SetViewportCommand setViewport = new() { Viewport = viewport };
        SetScissorCommand setScissor = new() { Scissor = scissor };
        PushConstantsCommand pushConstants = new() { Stages = ShaderStages.Vertex | ShaderStages.Fragment, Offset = 16, Size = 64 };
        DrawCommand drawCommand = new() { Arguments = draw };
        DrawIndexedCommand drawIndexedCommand = new() { Arguments = drawIndexed };
        DispatchCommand dispatch = new() { GroupCountX = 61, GroupCountY = 62, GroupCountZ = 63 };
        DebugGroupCommand debugGroup = new() { Label = label };

        Assert.Equal((21ul, true), (pipeline.Pipeline, pipeline.IsCompute));
        Assert.Equal((2, 31ul), (bindGroup.Index, bindGroup.BindGroup));
        Assert.Equal((3, 41ul, 42L), (vertexBuffer.Slot, vertexBuffer.Buffer, vertexBuffer.Offset));
        Assert.Equal((IndexFormat.Uint32, 51ul, 52L), (indexBuffer.Format, indexBuffer.Buffer, indexBuffer.Offset));
        Assert.Equal(viewport, setViewport.Viewport);
        Assert.Equal(scissor, setScissor.Scissor);
        Assert.Equal(
            (ShaderStages.Vertex | ShaderStages.Fragment, 16, 64),
            (pushConstants.Stages, pushConstants.Offset, pushConstants.Size));
        Assert.Equal(draw, drawCommand.Arguments);
        Assert.Equal(drawIndexed, drawIndexedCommand.Arguments);
        Assert.Equal((61, 62, 63), (dispatch.GroupCountX, dispatch.GroupCountY, dispatch.GroupCountZ));
        Assert.Equal(label, debugGroup.Label);
    }

    // Looked up by name, so a missing payload is a test failure rather than a build break.
    private static Type PayloadType(string name)
    {
        Type? type = typeof(CommandKind).Assembly.GetType("BrutalEngine.Graphics.Backend." + name);
        Assert.NotNull(type);
        return type;
    }
}
