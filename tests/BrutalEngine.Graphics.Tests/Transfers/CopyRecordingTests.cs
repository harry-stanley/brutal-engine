using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Transfers;

/// <summary>The four copy commands a <see cref="CommandList"/> records (docs/design/graphics.md section 6).</summary>
public sealed class CopyRecordingTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();
    private readonly MappedBufferBackend _backend;

    public CopyRecordingTests()
    {
        _fixture.Script.RecordPlans = true;
        _backend = new MappedBufferBackend(_fixture.Backend);
    }

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<CommandKind> Kinds() => [.. TransferResources.Kinds];

    public static TheoryData<int> FramesInFlight() => [1, 2, 3];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachCopy_EncodedWithIds(CommandKind first)
    {
        GraphicsDevice device = Create();
        var resources = TransferResources.Create(device);
        CommandKind[] order = [first, .. TransferResources.Kinds.Where(kind => kind != first)];

        // A write first: its upload copy goes ahead of every recorded command, which then follow in call order.
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(device.WriteBuffer(resources.Destination, 0, [1, 2, 3, 4]).IsSuccess);
        foreach (CommandKind kind in order)
        {
            resources.Record(commands, kind);
        }

        _ = device.SubmitFrame(commands);

        var reader = new CommandReader(_fixture.BackendDevice.Plans[^1].Commands);
        Assert.True(reader.MoveNext());
        Assert.Equal(CommandKind.CopyBufferToBuffer, reader.Kind);
        Assert.Equal((Id(resources.Destination), 0L, 4L), (reader.Payload<CopyBufferToBufferCommand>().Destination, reader.Payload<CopyBufferToBufferCommand>().DestinationOffset, reader.Payload<CopyBufferToBufferCommand>().Size));
        foreach (CommandKind kind in order)
        {
            Assert.True(reader.MoveNext());
            Assert.Equal(kind, reader.Kind);
            AssertEncoded(reader, resources);
        }

        Assert.False(reader.MoveNext());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Invalid_ThrowsAndRecordsNothing(CommandKind kind)
    {
        GraphicsDevice device = Create();
        var resources = TransferResources.Create(device);
        BufferHandle staleBuffer = device.CreateBuffer(new BufferDescription { Size = 8192, Usage = BufferUsage.CopySource | BufferUsage.CopyDestination }).Value;
        TextureHandle staleTexture = device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Rgba8Unorm,
            Width = 128,
            Height = 64,
            ArrayLayers = 3,
            MipLevels = 2,
            Usage = TextureUsage.CopySource | TextureUsage.CopyDestination,
        }).Value;
        device.Destroy(staleBuffer);
        device.Destroy(staleTexture);
        _ = device.BeginFrame(out CommandList stale);
        _ = device.SubmitFrame(stale);
        _ = device.BeginFrame(out CommandList commands);
        int calls = _fixture.BackendDevice.Calls.Count;

        // Not the open frame's list.
        _ = Assert.Throws<InvalidOperationException>(() => resources.Record(default, kind));
        _ = Assert.Throws<InvalidOperationException>(() => resources.Record(stale, kind));

        // Each case breaks one rule.
        foreach ((Type expected, Action<CommandList> record) in InvalidCopies(kind, resources, staleBuffer, staleTexture))
        {
            Exception thrown = Assert.Throws(expected, () => record(commands));
            if (thrown is ArgumentException argument)
            {
                Assert.Equal("copy", argument.ParamName);
            }

        }

        Exception? offThread = null;
        var thread = new Thread(() => offThread = Record.Exception(() => resources.Record(commands, kind)));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        _ = Assert.IsType<WrongThreadException>(offThread);
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);

        // Nothing reached the frame's stream.
        _ = device.SubmitFrame(commands);
        Assert.True(_fixture.BackendDevice.Plans[^1].Commands.IsEmpty);

        device.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => resources.Record(commands, kind));
    }

    [Theory]
    [MemberData(nameof(FramesInFlight))]
    public void SlotStream_StartsEmptyNextFrame(int framesInFlight)
    {
        GraphicsDevice device = Create(framesInFlight);
        var resources = TransferResources.Create(device);

        // Frame 0 fills slot 0's stream; every later frame, the slot-0 one included, holds only its own copy.
        _ = device.BeginFrame(out CommandList commands);
        foreach (CommandKind kind in TransferResources.Kinds)
        {
            resources.Record(commands, kind);
        }

        _ = device.SubmitFrame(commands);
        for (int frame = 1; frame <= framesInFlight; frame++)
        {
            _ = device.BeginFrame(out commands);
            commands.CopyBufferToBuffer(resources.BufferToBuffer with { DestinationOffset = 4 * frame });
            _ = device.SubmitFrame(commands);

            var reader = new CommandReader(_fixture.BackendDevice.Plans[^1].Commands);
            Assert.True(reader.MoveNext());
            Assert.Equal((CommandKind.CopyBufferToBuffer, 4L * frame), (reader.Kind, reader.Payload<CopyBufferToBufferCommand>().DestinationOffset));
            Assert.False(reader.MoveNext());
        }
    }

    // One invalid copy per rule kind the device itself is responsible for: a null and a stale handle in each role, and
    // one rule CopyValidator checks against each role's own description, so a check against the wrong resource passes it.
    private static List<(Type, Action<CommandList>)> InvalidCopies(CommandKind kind, TransferResources r, BufferHandle staleBuffer, TextureHandle staleTexture)
    {
        Type invalid = typeof(ArgumentException);
        Type range = typeof(ArgumentOutOfRangeException);
        Type stale = typeof(StaleHandleException);
        return kind switch
        {
            CommandKind.CopyBufferToBuffer =>
            [
                (invalid, c => c.CopyBufferToBuffer(r.BufferToBuffer with { Source = default })),
                (invalid, c => c.CopyBufferToBuffer(r.BufferToBuffer with { Destination = default })),
                (stale, c => c.CopyBufferToBuffer(r.BufferToBuffer with { Source = staleBuffer })),
                (stale, c => c.CopyBufferToBuffer(r.BufferToBuffer with { Destination = staleBuffer })),
                (range, c => c.CopyBufferToBuffer(r.BufferToBuffer with { SourceOffset = 4096 - 60 })),
                (range, c => c.CopyBufferToBuffer(r.BufferToBuffer with { DestinationOffset = 8192 - 60 })),
                (invalid, c => c.CopyBufferToBuffer(r.BufferToBuffer with { Destination = r.Readback, DestinationOffset = 0, Size = 6 })),
            ],
            CommandKind.CopyBufferToTexture =>
            [
                (invalid, c => c.CopyBufferToTexture(r.BufferToTexture with { Buffer = default })),
                (invalid, c => c.CopyBufferToTexture(r.BufferToTexture with { Texture = new TextureLocation(default) })),
                (stale, c => c.CopyBufferToTexture(r.BufferToTexture with { Buffer = staleBuffer })),
                (stale, c => c.CopyBufferToTexture(r.BufferToTexture with { Texture = new TextureLocation(staleTexture) })),
                (range, c => c.CopyBufferToTexture(r.BufferToTexture with { BufferOffset = 4096 - 1024 })),
                (range, c => c.CopyBufferToTexture(r.BufferToTexture with { Texture = new TextureLocation(r.Target, 2) })),
            ],
            CommandKind.CopyTextureToBuffer =>
            [
                (invalid, c => c.CopyTextureToBuffer(r.TextureToBuffer with { Buffer = default })),
                (invalid, c => c.CopyTextureToBuffer(r.TextureToBuffer with { Texture = new TextureLocation(default) })),
                (stale, c => c.CopyTextureToBuffer(r.TextureToBuffer with { Buffer = staleBuffer })),
                (stale, c => c.CopyTextureToBuffer(r.TextureToBuffer with { Texture = new TextureLocation(staleTexture) })),
                (range, c => c.CopyTextureToBuffer(r.TextureToBuffer with { BufferOffset = 8192 - 1024 })),
                (range, c => c.CopyTextureToBuffer(r.TextureToBuffer with { Texture = r.TextureToBuffer.Texture with { ArrayLayer = 3 } })),
                (invalid, c => c.CopyTextureToBuffer(r.TextureToBuffer with { Buffer = r.Readback, BufferOffset = 0, BytesPerRow = 100 })),
            ],
            _ =>
            [
                (invalid, c => c.CopyTextureToTexture(r.TextureToTexture with { Source = new TextureLocation(default) })),
                (invalid, c => c.CopyTextureToTexture(r.TextureToTexture with { Destination = new TextureLocation(default) })),
                (stale, c => c.CopyTextureToTexture(r.TextureToTexture with { Source = new TextureLocation(staleTexture) })),
                (stale, c => c.CopyTextureToTexture(r.TextureToTexture with { Destination = new TextureLocation(staleTexture) })),
                (range, c => c.CopyTextureToTexture(r.TextureToTexture with { Source = new TextureLocation(r.Color, ArrayLayer: 2) })),
                (range, c => c.CopyTextureToTexture(r.TextureToTexture with { DestinationX = 64 - 10 })),
            ],
        };
    }

    private void AssertEncoded(CommandReader reader, TransferResources r)
    {
        switch (reader.Kind)
        {
            case CommandKind.CopyBufferToBuffer:
                CopyBufferToBufferCommand buffers = reader.Payload<CopyBufferToBufferCommand>();
                Assert.Equal(
                    (Id(r.Source), 8L, Id(r.Destination), 4100L, 64L),
                    (buffers.Source, buffers.SourceOffset, buffers.Destination, buffers.DestinationOffset, buffers.Size));
                break;
            case CommandKind.CopyBufferToTexture:
                CopyBufferTextureCommand upload = reader.Payload<CopyBufferTextureCommand>();
                Assert.Equal(
                    (Id(r.Source), 1024L, 256, Id(r.Target), 1, 1, TextureAspect.Color, new TextureRegion(5, 6, 7, 8)),
                    (upload.Buffer, upload.BufferOffset, upload.BytesPerRow, upload.Texture, upload.MipLevel, upload.ArrayLayer, upload.Aspect, upload.Region));
                break;
            case CommandKind.CopyTextureToBuffer:
                CopyBufferTextureCommand download = reader.Payload<CopyBufferTextureCommand>();
                Assert.Equal(
                    (Id(r.Destination), 4608L, 512, Id(r.Depth), 1, 2, TextureAspect.Depth, new TextureRegion(1, 2, 3, 4)),
                    (download.Buffer, download.BufferOffset, download.BytesPerRow, download.Texture, download.MipLevel, download.ArrayLayer, download.Aspect, download.Region));
                break;
            default:
                CopyTextureToTextureCommand textures = reader.Payload<CopyTextureToTextureCommand>();
                Assert.Equal(
                    (Id(r.Color), 0, 1, TextureAspect.Color, new TextureRegion(9, 10, 11, 12)),
                    (textures.Source, textures.SourceMipLevel, textures.SourceArrayLayer, textures.SourceAspect, textures.SourceRegion));
                Assert.Equal(
                    (Id(r.Target), 1, 0, TextureAspect.Color, 13, 14),
                    (textures.Destination, textures.DestinationMipLevel, textures.DestinationArrayLayer, textures.DestinationAspect, textures.DestinationX, textures.DestinationY));
                break;
        }
    }

    private ulong Id(BufferHandle buffer) => _fixture.BackendDevice.Objects.GetId(buffer);

    private ulong Id(TextureHandle texture) => _fixture.BackendDevice.Objects.GetId(texture);

    private GraphicsDevice Create(int framesInFlight = 2) =>
        _fixture.Create(_backend, _fixture.Options() with { FramesInFlight = framesInFlight });
}
