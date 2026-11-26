using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Recording;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The command stream end to end (docs/design/graphics.md, "The frame plan"): records written by the internal writer
/// and read back by <see cref="CommandReader"/>, the reader's type checks, malformed streams, and allocation-free
/// steady state.
/// </summary>
public sealed class CommandStreamTests
{
    // Written from the note's payload table: each kind's payload struct and trailing element type (null for none).
    private static readonly (CommandKind Kind, Type Payload, Type? Trailing)[] _formats =
    [
        (CommandKind.Barrier, typeof(BarrierCommand), typeof(ResourceTransition)),
        (CommandKind.CopyBufferToBuffer, typeof(CopyBufferToBufferCommand), null),
        (CommandKind.CopyBufferToTexture, typeof(CopyBufferTextureCommand), null),
        (CommandKind.CopyTextureToBuffer, typeof(CopyBufferTextureCommand), null),
        (CommandKind.CopyTextureToTexture, typeof(CopyTextureToTextureCommand), null),
        (CommandKind.BeginRenderPass, typeof(BeginRenderPassCommand), typeof(AttachmentCommand)),
        (CommandKind.EndRenderPass, typeof(EndPassCommand), null),
        (CommandKind.BeginComputePass, typeof(BeginComputePassCommand), null),
        (CommandKind.EndComputePass, typeof(EndPassCommand), null),
        (CommandKind.SetPipeline, typeof(SetPipelineCommand), null),
        (CommandKind.SetBindGroup, typeof(SetBindGroupCommand), null),
        (CommandKind.SetVertexBuffer, typeof(SetVertexBufferCommand), null),
        (CommandKind.SetIndexBuffer, typeof(SetIndexBufferCommand), null),
        (CommandKind.SetViewport, typeof(SetViewportCommand), null),
        (CommandKind.SetScissor, typeof(SetScissorCommand), null),
        (CommandKind.PushConstants, typeof(PushConstantsCommand), typeof(byte)),
        (CommandKind.Draw, typeof(DrawCommand), null),
        (CommandKind.DrawIndexed, typeof(DrawIndexedCommand), null),
        (CommandKind.Dispatch, typeof(DispatchCommand), null),
        (CommandKind.PushDebugGroup, typeof(DebugGroupCommand), null),
        (CommandKind.PopDebugGroup, typeof(DebugGroupCommand), null),
        (CommandKind.ClearSwapchainImage, typeof(ClearSwapchainImageCommand), null),
    ];

    private static readonly DebugLabel _label = new(new Handle(5, 2));

    private static readonly ResourceTransition[] _transitions =
    [
        new() { Resource = 101, IsTexture = true, Before = ResourceState.Undefined, After = ResourceState.ColorTarget, MipLevelCount = 1, ArrayLayerCount = 1 },
        new() { Resource = 102, Before = ResourceState.CopyDestination, After = ResourceState.VertexBuffer },
    ];

    private static readonly AttachmentCommand[] _attachments =
    [
        new() { View = 201, Load = LoadOp.Clear, Store = StoreOp.Store, ClearColor = new ColorValue(0.1f, 0.2f, 0.3f, 1) },
        new() { View = 202, Load = LoadOp.Load, Store = StoreOp.DontCare },
        new() { View = 203, Load = LoadOp.Clear, Store = StoreOp.DontCare, ClearDepth = 1 },
    ];

    private static readonly byte[] _pushBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];

    public static TheoryData<CommandKind> Kinds() => [.. Enum.GetValues<CommandKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void RoundTrip_EachKind(CommandKind kind)
    {
        switch (kind)
        {
            case CommandKind.Barrier:
                RoundTrip(kind, new BarrierCommand { Count = 2 }, (ReadOnlySpan<ResourceTransition>)_transitions);
                break;
            case CommandKind.CopyBufferToBuffer:
                RoundTrip(kind, new CopyBufferToBufferCommand { Source = 1, SourceOffset = 4, Destination = 2, DestinationOffset = 8, Size = 64 });
                break;
            case CommandKind.CopyBufferToTexture or CommandKind.CopyTextureToBuffer:
                RoundTrip(kind, new CopyBufferTextureCommand
                {
                    Buffer = 3,
                    BufferOffset = 256,
                    BytesPerRow = 512,
                    Texture = 4,
                    MipLevel = 1,
                    ArrayLayer = 2,
                    Aspect = TextureAspect.Depth,
                    Region = new TextureRegion(1, 2, 30, 40),
                });
                break;
            case CommandKind.CopyTextureToTexture:
                RoundTrip(kind, new CopyTextureToTextureCommand
                {
                    Source = 5,
                    SourceMipLevel = 1,
                    SourceArrayLayer = 2,
                    SourceAspect = TextureAspect.Color,
                    Destination = 6,
                    DestinationMipLevel = 3,
                    DestinationArrayLayer = 4,
                    DestinationAspect = TextureAspect.Color,
                    SourceRegion = new TextureRegion(5, 6, 7, 8),
                    DestinationX = 9,
                    DestinationY = 10,
                });
                break;
            case CommandKind.BeginRenderPass:
                RoundTrip(kind, new BeginRenderPassCommand { ColorCount = 2, HasDepth = true, Label = _label, TimestampQueryIndex = 6 }, (ReadOnlySpan<AttachmentCommand>)_attachments);
                break;
            case CommandKind.EndRenderPass or CommandKind.EndComputePass:
                RoundTrip(kind, new EndPassCommand { TimestampQueryIndex = 7 });
                break;
            case CommandKind.BeginComputePass:
                RoundTrip(kind, new BeginComputePassCommand { Label = _label, TimestampQueryIndex = -1 });
                break;
            case CommandKind.SetPipeline:
                RoundTrip(kind, new SetPipelineCommand { Pipeline = 11, IsCompute = true });
                break;
            case CommandKind.SetBindGroup:
                RoundTrip(kind, new SetBindGroupCommand { Index = 1, BindGroup = 12 });
                break;
            case CommandKind.SetVertexBuffer:
                RoundTrip(kind, new SetVertexBufferCommand { Slot = 2, Buffer = 13, Offset = 96 });
                break;
            case CommandKind.SetIndexBuffer:
                RoundTrip(kind, new SetIndexBufferCommand { Format = IndexFormat.Uint32, Buffer = 14, Offset = 128 });
                break;
            case CommandKind.SetViewport:
                RoundTrip(kind, new SetViewportCommand { Viewport = new Viewport(0, 0, 1280, 720) });
                break;
            case CommandKind.SetScissor:
                RoundTrip(kind, new SetScissorCommand { Scissor = new ScissorRect(10, 20, 300, 400) });
                break;
            case CommandKind.PushConstants:
                RoundTrip(kind, new PushConstantsCommand { Stages = ShaderStages.Vertex, Offset = 0, Size = _pushBytes.Length }, (ReadOnlySpan<byte>)_pushBytes);
                break;
            case CommandKind.Draw:
                RoundTrip(kind, new DrawCommand { Arguments = new DrawArguments(3, 2) });
                break;
            case CommandKind.DrawIndexed:
                RoundTrip(kind, new DrawIndexedCommand { Arguments = new DrawIndexedArguments(6, 1, 3, -2) });
                break;
            case CommandKind.Dispatch:
                RoundTrip(kind, new DispatchCommand { GroupCountX = 8, GroupCountY = 4, GroupCountZ = 1 });
                break;
            case CommandKind.PushDebugGroup or CommandKind.PopDebugGroup:
                RoundTrip(kind, new DebugGroupCommand { Label = _label });
                break;
            case CommandKind.ClearSwapchainImage:
                RoundTrip(kind, new ClearSwapchainImageCommand { AcquiredIndex = 1 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "No sample for this kind.");
        }
    }

    [Fact]
    public void WrongPayloadType_Throws()
    {
        Type[] payloadTypes = [.. _formats.Select(f => f.Payload).Distinct()];
        foreach ((CommandKind kind, Type payload, _) in _formats)
        {
            byte[] stream = SampleStream(kind);
            foreach (Type candidate in payloadTypes)
            {
                // Exactly the kind's own payload struct is accepted; every other one throws InvalidOperationException.
                Exception? error = Record.Exception(() => ReadPayloadAs(candidate, stream));
                if (candidate == payload)
                {
                    Assert.Null(error);
                }
                else
                {
                    Assert.IsType<InvalidOperationException>(error, exactMatch: true);
                }
            }
        }

        // No current record: before the first MoveNext, after the last, and in a default reader (an empty stream).
        byte[] draw = SampleStream(CommandKind.Draw);
        Assert.False(default(CommandReader).MoveNext());
        Assert.Throws<InvalidOperationException>(() => _ = default(CommandReader).Kind);
        Assert.Throws<InvalidOperationException>(() => default(CommandReader).Payload<BarrierCommand>());
        Assert.Throws<InvalidOperationException>(() => new CommandReader(draw).Payload<DrawCommand>());
        Assert.Throws<InvalidOperationException>(() => _ = new CommandReader(draw).Kind);
        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new CommandReader(draw);
            Assert.True(reader.MoveNext());
            Assert.False(reader.MoveNext());
            _ = reader.Payload<DrawCommand>();
        });
    }

    [Fact]
    public void WrongTrailingType_Throws()
    {
        byte[] barrier = SampleStream(CommandKind.Barrier);
        byte[] renderPass = SampleStream(CommandKind.BeginRenderPass);
        byte[] push = SampleStream(CommandKind.PushConstants);

        Assert.Equal(2, ReadTrailing<ResourceTransition>(barrier));
        Assert.Throws<InvalidOperationException>(() => ReadTrailing<AttachmentCommand>(barrier));
        Assert.Throws<InvalidOperationException>(() => ReadTrailing<byte>(barrier));
        Assert.Equal(3, ReadTrailing<AttachmentCommand>(renderPass));
        Assert.Throws<InvalidOperationException>(() => ReadTrailing<ResourceTransition>(renderPass));
        Assert.Equal(13, ReadTrailing<byte>(push));
        Assert.Throws<InvalidOperationException>(() => ReadTrailing<int>(push));

        // A kind without a trailing part reads as empty, whatever the element type.
        byte[] draw = SampleStream(CommandKind.Draw);
        Assert.Equal(0, ReadTrailing<byte>(draw));
        Assert.Equal(0, ReadTrailing<ResourceTransition>(draw));

        // A trailing part that isn't a whole number of elements is malformed. The writer doesn't check element types,
        // so it can write one: a barrier followed by 5 bytes.
        var tracking = new TrackingAllocator(new HeapAllocator(), "stream");
        using (var writer = new CommandWriter(tracking))
        {
            writer.Append(CommandKind.Barrier, new BarrierCommand { Count = 1 }, (ReadOnlySpan<byte>)[1, 2, 3, 4, 5]);
            byte[] torn = writer.Written.ToArray();
            Assert.Throws<InvalidOperationException>(() => ReadTrailing<ResourceTransition>(torn));
        }

        tracking.ThrowIfLeaks();
    }

    public static TheoryData<string> MalformedStreams() =>
    [
        "truncated header", "truncated header after a record", "unknown kind", "largest unknown kind", "size past the end",
        "padding past the end", "size below the payload", "negative size", "reserved field set",
    ];

    [Theory]
    [MemberData(nameof(MalformedStreams))]
    public void MalformedStream_Throws(string stream)
    {
        // Built byte by byte: a header is the kind (2 bytes), the reserved field (2 bytes) and the size (4 bytes). A
        // Draw payload is 16 bytes, so a Draw record is 24 bytes; a PushConstants payload is 12 bytes, padded to 16.
        (byte[] bytes, int goodRecords) = stream switch
        {
            "truncated header" => (new byte[4], 0),
            "truncated header after a record" => ([.. RawRecord(CommandKind.Draw, 24, 24), .. new byte[4]], 1),
            "unknown kind" => (RawRecord((CommandKind)22, 24, 24), 0),
            "largest unknown kind" => (RawRecord((CommandKind)ushort.MaxValue, 24, 24), 0),
            "size past the end" => (RawRecord(CommandKind.Draw, 64, 24), 0),
            // 8 + 16 + 3 trailing bytes = 27, padded to 32: the stream stops at 28.
            "padding past the end" => (RawRecord(CommandKind.PushConstants, 27, 28), 0),
            "size below the payload" => (RawRecord(CommandKind.Draw, 16, 24), 0),
            "negative size" => (RawRecord(CommandKind.Draw, int.MinValue, 24), 0),
            "reserved field set" => (RawRecord(CommandKind.Draw, 24, 24, reserved: 1), 0),
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, "Unknown stream."),
        };

        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new CommandReader(bytes);
            for (int i = 0; i < goodRecords; i++)
            {
                Assert.True(reader.MoveNext());
            }

            reader.MoveNext();
        });
    }

    [Fact]
    public void RandomSequences_RoundTrip()
    {
        // Each command is its kind index, random payload bytes (read into the payload struct) and, for the kinds with a
        // trailing part, 0 to 4 random elements as bytes. Random bytes cover every enum and bool value a field can hold.
        Gen<(int Format, byte[] Payload, byte[] Trailing)> command =
            from format in Gen.Int[0, _formats.Length - 1]
            from payload in Gen.Byte.Array[SizeOf(_formats[format].Payload)]
            from count in Gen.Int[0, _formats[format].Trailing is null ? 0 : 4]
            from trailing in Gen.Byte.Array[count * (_formats[format].Trailing is { } t ? SizeOf(t) : 0)]
            select (format, payload, trailing);

        command.Array[0, 40].Sample(
            commands =>
            {
                var tracking = new TrackingAllocator(new HeapAllocator(), "stream");
                using (var writer = new CommandWriter(tracking, initialCapacity: 64))
                {
                    foreach ((int format, byte[] payload, byte[] trailing) in commands)
                    {
                        Write(writer, _formats[format].Kind, payload, trailing);
                    }

                    // The model's own layout: each record is 8 header bytes, the payload padded to 8, the trailing
                    // bytes, padded to 8.
                    long expectedLength = commands.Sum(c => Pad8(8 + Pad8(c.Payload.Length) + c.Trailing.Length));
                    Assert.Equal(expectedLength, writer.Written.Length);

                    var reader = new CommandReader(writer.Written);
                    foreach ((int format, byte[] payload, byte[] trailing) in commands)
                    {
                        Assert.True(reader.MoveNext());
                        Assert.Equal(_formats[format].Kind, reader.Kind);
                        (object read, byte[] readTrailing) = Read(ref reader);
                        AssertSameFields(ReadBoxed(_formats[format].Payload, payload), read);
                        Assert.Equal(trailing, readTrailing);
                    }

                    Assert.False(reader.MoveNext());
                }

                tracking.ThrowIfLeaks();
            },
            iter: 200,
            threads: 1);
    }

    [Fact]
    public void WriteRead_DoesNotAllocate()
    {
        var tracking = new TrackingAllocator(new HeapAllocator(), "stream");
        using (var writer = new CommandWriter(tracking, initialCapacity: 64))
        {
            // Warm-up: grows the writer to a frame's size and runs every generic instantiation once.
            long checksum = WriteAndReadFrame(writer);
            int capacity = writer.Capacity;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 1_000; frame++)
            {
                checksum += WriteAndReadFrame(writer);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(capacity, writer.Capacity);
            Assert.Equal(1_001 * FrameChecksum, checksum);
        }

        Assert.Equal(0, tracking.LiveAllocationCount);
        tracking.ThrowIfLeaks();
    }

    // What one frame of WriteAndReadFrame adds up: every kind's value (0 to 21), then, in kind order, one payload field
    // of each record plus its trailing length: the barrier's count and transitions (2 + 2), the buffer copy's size
    // (64), the two buffer-texture copies' bytes per row (256 each), the texture copy's destination (6), the render
    // pass's color count and attachments (2 + 3), its end's query index (-1), the compute pass's label index (5), its
    // end's query index (-1), the pipeline (11), bind group (12), vertex buffer (13) and index buffer (14), the
    // viewport's width (1280), the scissor's height (720), the push constants' size and bytes (13 + 13), the draw's
    // vertex count (3), the indexed draw's index count (6), the dispatch's group counts (1 + 1 + 1), the two debug
    // groups' label indices (5 each) and the swapchain clear's acquired index (1).
    private const long FrameChecksum =
        (21 * 22 / 2) + 4 + 64 + 256 + 256 + 6 + 5 - 1 + 5 - 1 + 11 + 12 + 13 + 14 + 1280 + 720 + 26 + 3 + 6 + 3 + 5 + 5 + 1;

    private static long WriteAndReadFrame(CommandWriter writer)
    {
        writer.Reset();
        for (int kind = 0; kind <= (int)CommandKind.ClearSwapchainImage; kind++)
        {
            WriteSample(writer, (CommandKind)kind);
        }

        // Every record is read through its own payload struct (each a separate Payload<T> instantiation) and its
        // trailing part (byte for the kinds without one), so an allocation on any kind's read path shows.
        long checksum = 0;
        var reader = new CommandReader(writer.Written);
        while (reader.MoveNext())
        {
            checksum += (int)reader.Kind;
            checksum += reader.Kind switch
            {
                CommandKind.Barrier => reader.Payload<BarrierCommand>().Count + reader.Trailing<ResourceTransition>().Length,
                CommandKind.CopyBufferToBuffer => reader.Payload<CopyBufferToBufferCommand>().Size + reader.Trailing<byte>().Length,
                CommandKind.CopyBufferToTexture or CommandKind.CopyTextureToBuffer =>
                    reader.Payload<CopyBufferTextureCommand>().BytesPerRow + reader.Trailing<byte>().Length,
                CommandKind.CopyTextureToTexture =>
                    (long)reader.Payload<CopyTextureToTextureCommand>().Destination + reader.Trailing<byte>().Length,
                CommandKind.BeginRenderPass =>
                    reader.Payload<BeginRenderPassCommand>().ColorCount + reader.Trailing<AttachmentCommand>().Length,
                CommandKind.EndRenderPass or CommandKind.EndComputePass =>
                    reader.Payload<EndPassCommand>().TimestampQueryIndex + reader.Trailing<byte>().Length,
                CommandKind.BeginComputePass =>
                    reader.Payload<BeginComputePassCommand>().Label.Value.Index + reader.Trailing<byte>().Length,
                CommandKind.SetPipeline => (long)reader.Payload<SetPipelineCommand>().Pipeline + reader.Trailing<byte>().Length,
                CommandKind.SetBindGroup => (long)reader.Payload<SetBindGroupCommand>().BindGroup + reader.Trailing<byte>().Length,
                CommandKind.SetVertexBuffer => (long)reader.Payload<SetVertexBufferCommand>().Buffer + reader.Trailing<byte>().Length,
                CommandKind.SetIndexBuffer => (long)reader.Payload<SetIndexBufferCommand>().Buffer + reader.Trailing<byte>().Length,
                CommandKind.SetViewport => (long)reader.Payload<SetViewportCommand>().Viewport.Width + reader.Trailing<byte>().Length,
                CommandKind.SetScissor => reader.Payload<SetScissorCommand>().Scissor.Height + reader.Trailing<byte>().Length,
                CommandKind.PushConstants => reader.Payload<PushConstantsCommand>().Size + reader.Trailing<byte>().Length,
                CommandKind.Draw => reader.Payload<DrawCommand>().Arguments.VertexCount + reader.Trailing<byte>().Length,
                CommandKind.DrawIndexed => reader.Payload<DrawIndexedCommand>().Arguments.IndexCount + reader.Trailing<byte>().Length,
                CommandKind.Dispatch => SumGroupCounts(in reader.Payload<DispatchCommand>()) + reader.Trailing<byte>().Length,
                CommandKind.PushDebugGroup or CommandKind.PopDebugGroup =>
                    reader.Payload<DebugGroupCommand>().Label.Value.Index + reader.Trailing<byte>().Length,
                CommandKind.ClearSwapchainImage =>
                    reader.Payload<ClearSwapchainImageCommand>().AcquiredIndex + reader.Trailing<byte>().Length,
                _ => throw new InvalidOperationException($"Unexpected kind {reader.Kind}."),
            };
        }

        return checksum;
    }

    private static long SumGroupCounts(in DispatchCommand dispatch) =>
        dispatch.GroupCountX + dispatch.GroupCountY + dispatch.GroupCountZ;

    // Writes one record of the kind with the same values RoundTrip_EachKind uses, without allocating.
    private static void WriteSample(CommandWriter writer, CommandKind kind)
    {
        switch (kind)
        {
            case CommandKind.Barrier:
                writer.Append(kind, new BarrierCommand { Count = 2 }, (ReadOnlySpan<ResourceTransition>)_transitions);
                break;
            case CommandKind.BeginRenderPass:
                writer.Append(kind, new BeginRenderPassCommand { ColorCount = 2, HasDepth = true, Label = _label }, (ReadOnlySpan<AttachmentCommand>)_attachments);
                break;
            case CommandKind.PushConstants:
                writer.Append(kind, new PushConstantsCommand { Stages = ShaderStages.Vertex, Size = _pushBytes.Length }, (ReadOnlySpan<byte>)_pushBytes);
                break;
            case CommandKind.CopyBufferToBuffer:
                writer.Append(kind, new CopyBufferToBufferCommand { Source = 1, Destination = 2, Size = 64 });
                break;
            case CommandKind.CopyBufferToTexture or CommandKind.CopyTextureToBuffer:
                writer.Append(kind, new CopyBufferTextureCommand { Buffer = 3, Texture = 4, BytesPerRow = 256 });
                break;
            case CommandKind.CopyTextureToTexture:
                writer.Append(kind, new CopyTextureToTextureCommand { Source = 5, Destination = 6 });
                break;
            case CommandKind.EndRenderPass or CommandKind.EndComputePass:
                writer.Append(kind, new EndPassCommand { TimestampQueryIndex = -1 });
                break;
            case CommandKind.BeginComputePass:
                writer.Append(kind, new BeginComputePassCommand { Label = _label, TimestampQueryIndex = -1 });
                break;
            case CommandKind.SetPipeline:
                writer.Append(kind, new SetPipelineCommand { Pipeline = 11 });
                break;
            case CommandKind.SetBindGroup:
                writer.Append(kind, new SetBindGroupCommand { BindGroup = 12 });
                break;
            case CommandKind.SetVertexBuffer:
                writer.Append(kind, new SetVertexBufferCommand { Buffer = 13 });
                break;
            case CommandKind.SetIndexBuffer:
                writer.Append(kind, new SetIndexBufferCommand { Buffer = 14 });
                break;
            case CommandKind.SetViewport:
                writer.Append(kind, new SetViewportCommand { Viewport = new Viewport(0, 0, 1280, 720) });
                break;
            case CommandKind.SetScissor:
                writer.Append(kind, new SetScissorCommand { Scissor = new ScissorRect(0, 0, 1280, 720) });
                break;
            case CommandKind.Draw:
                writer.Append(kind, new DrawCommand { Arguments = new DrawArguments(3) });
                break;
            case CommandKind.DrawIndexed:
                writer.Append(kind, new DrawIndexedCommand { Arguments = new DrawIndexedArguments(6) });
                break;
            case CommandKind.Dispatch:
                writer.Append(kind, new DispatchCommand { GroupCountX = 1, GroupCountY = 1, GroupCountZ = 1 });
                break;
            case CommandKind.PushDebugGroup or CommandKind.PopDebugGroup:
                writer.Append(kind, new DebugGroupCommand { Label = _label });
                break;
            case CommandKind.ClearSwapchainImage:
                writer.Append(kind, new ClearSwapchainImageCommand { AcquiredIndex = 1 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "No sample for this kind.");
        }
    }

    // A one-record stream of the kind's sample.
    private static byte[] SampleStream(CommandKind kind)
    {
        var tracking = new TrackingAllocator(new HeapAllocator(), "stream");
        byte[] bytes;
        using (var writer = new CommandWriter(tracking))
        {
            WriteSample(writer, kind);
            bytes = writer.Written.ToArray();
        }

        tracking.ThrowIfLeaks();
        return bytes;
    }

    // Writes the record, reads it back, and checks the kind and the payload and trailing bytes exactly.
    private static void RoundTrip<T>(CommandKind kind, T payload)
        where T : unmanaged
        => RoundTrip(kind, payload, ReadOnlySpan<byte>.Empty, trailingKind: false);

    private static void RoundTrip<T, TTrailing>(CommandKind kind, T payload, ReadOnlySpan<TTrailing> trailing, bool trailingKind = true)
        where T : unmanaged
        where TTrailing : unmanaged
    {
        // The expected payload bytes come from a zeroed buffer, so struct padding is zero, as the writer writes it.
        byte[] expectedPayload = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(expectedPayload, in payload);
        byte[] expectedTrailing = MemoryMarshal.AsBytes(trailing).ToArray();

        var tracking = new TrackingAllocator(new HeapAllocator(), "stream");
        using (var writer = new CommandWriter(tracking, initialCapacity: 8))
        {
            writer.Append(kind, payload, trailing);
            var reader = new CommandReader(writer.Written);

            Assert.True(reader.MoveNext());
            Assert.Equal(kind, reader.Kind);
            Assert.Equal(expectedPayload, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in reader.Payload<T>())).ToArray());
            Assert.Equal(expectedTrailing, MemoryMarshal.AsBytes(reader.Trailing<TTrailing>()).ToArray());
            if (!trailingKind)
            {
                Assert.True(reader.Trailing<ResourceTransition>().IsEmpty);
            }

            Assert.False(reader.MoveNext());
            Assert.False(reader.MoveNext());
        }

        tracking.ThrowIfLeaks();
    }

    private static void ReadPayloadAs(Type payload, byte[] stream) =>
        typeof(CommandStreamTests)
            .GetMethod(nameof(ReadPayload), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(payload)
            .CreateDelegate<Action<byte[]>>()
            .Invoke(stream);

    private static void ReadPayload<T>(byte[] stream)
        where T : unmanaged
    {
        var reader = new CommandReader(stream);
        Assert.True(reader.MoveNext());
        _ = reader.Payload<T>();
    }

    private static int ReadTrailing<T>(byte[] stream)
        where T : unmanaged
    {
        var reader = new CommandReader(stream);
        Assert.True(reader.MoveNext());
        return reader.Trailing<T>().Length;
    }

    // Writes a record from raw bytes, through the payload and trailing types the format table names.
    private static void Write(CommandWriter writer, CommandKind kind, byte[] payload, byte[] trailing)
    {
        MethodInfo method = _formats.First(f => f.Kind == kind) switch
        {
            (_, Type p, null) => typeof(CommandStreamTests).GetMethod(nameof(WriteRaw), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(p, typeof(byte)),
            (_, Type p, Type t) => typeof(CommandStreamTests).GetMethod(nameof(WriteRaw), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(p, t),
        };
        method.Invoke(null, [writer, kind, payload, trailing]);
    }

    private static void WriteRaw<T, TTrailing>(CommandWriter writer, CommandKind kind, byte[] payload, byte[] trailing)
        where T : unmanaged
        where TTrailing : unmanaged
        => writer.Append(kind, MemoryMarshal.Read<T>(payload), MemoryMarshal.Cast<byte, TTrailing>(trailing));

    // Reads the current record as its kind's types: the payload, boxed, and the trailing part's bytes.
    private static (object Payload, byte[] Trailing) Read(ref CommandReader reader) => reader.Kind switch
    {
        CommandKind.Barrier => (reader.Payload<BarrierCommand>(), MemoryMarshal.AsBytes(reader.Trailing<ResourceTransition>()).ToArray()),
        CommandKind.CopyBufferToBuffer => (reader.Payload<CopyBufferToBufferCommand>(), []),
        CommandKind.CopyBufferToTexture or CommandKind.CopyTextureToBuffer => (reader.Payload<CopyBufferTextureCommand>(), []),
        CommandKind.CopyTextureToTexture => (reader.Payload<CopyTextureToTextureCommand>(), []),
        CommandKind.BeginRenderPass => (reader.Payload<BeginRenderPassCommand>(), MemoryMarshal.AsBytes(reader.Trailing<AttachmentCommand>()).ToArray()),
        CommandKind.EndRenderPass or CommandKind.EndComputePass => (reader.Payload<EndPassCommand>(), []),
        CommandKind.BeginComputePass => (reader.Payload<BeginComputePassCommand>(), []),
        CommandKind.SetPipeline => (reader.Payload<SetPipelineCommand>(), []),
        CommandKind.SetBindGroup => (reader.Payload<SetBindGroupCommand>(), []),
        CommandKind.SetVertexBuffer => (reader.Payload<SetVertexBufferCommand>(), []),
        CommandKind.SetIndexBuffer => (reader.Payload<SetIndexBufferCommand>(), []),
        CommandKind.SetViewport => (reader.Payload<SetViewportCommand>(), []),
        CommandKind.SetScissor => (reader.Payload<SetScissorCommand>(), []),
        CommandKind.PushConstants => (reader.Payload<PushConstantsCommand>(), reader.Trailing<byte>().ToArray()),
        CommandKind.Draw => (reader.Payload<DrawCommand>(), []),
        CommandKind.DrawIndexed => (reader.Payload<DrawIndexedCommand>(), []),
        CommandKind.Dispatch => (reader.Payload<DispatchCommand>(), []),
        CommandKind.PushDebugGroup or CommandKind.PopDebugGroup => (reader.Payload<DebugGroupCommand>(), []),
        CommandKind.ClearSwapchainImage => (reader.Payload<ClearSwapchainImageCommand>(), []),
        _ => throw new ArgumentOutOfRangeException(nameof(reader), reader.Kind, "Unknown kind."),
    };

    // Compares two values of the same struct type field by field, down to the primitive fields (private backing
    // fields included). Floats compare by bits, so random NaN payloads compare too. Padding bytes aren't compared:
    // a struct copy may or may not carry them.
    private static void AssertSameFields(object expected, object actual)
    {
        Type type = expected.GetType();
        Assert.Equal(type, actual.GetType());
        if (type.IsPrimitive || type.IsEnum)
        {
            object e = expected is float f ? BitConverter.SingleToInt32Bits(f) : expected;
            object a = actual is float g ? BitConverter.SingleToInt32Bits(g) : actual;
            Assert.Equal(e, a);
            return;
        }

        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(fields);
        foreach (FieldInfo field in fields)
        {
            AssertSameFields(field.GetValue(expected)!, field.GetValue(actual)!);
        }
    }

    private static long Pad8(long value) => (value + 7) / 8 * 8;

    private static int SizeOf(Type type) =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;

    private static object ReadBoxed(Type type, byte[] bytes) =>
        typeof(CommandStreamTests)
            .GetMethod(nameof(ReadValue), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type)
            .Invoke(null, [bytes])!;

    private static object ReadValue<T>(byte[] bytes)
        where T : unmanaged
        => MemoryMarshal.Read<T>(bytes);

    private static byte[] RawRecord(CommandKind kind, int size, int length, ushort reserved = 0)
    {
        byte[] bytes = new byte[length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), (ushort)kind);
        BitConverter.TryWriteBytes(bytes.AsSpan(2, 2), reserved);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), size);
        return bytes;
    }
}
