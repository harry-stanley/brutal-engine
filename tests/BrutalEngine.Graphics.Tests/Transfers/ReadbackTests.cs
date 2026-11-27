using System;
using System.Linq;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.Transfers;

/// <summary>Reading <see cref="MemoryIntent.Readback"/> buffers without waiting (docs/design/graphics.md section 6).</summary>
public sealed unsafe class ReadbackTests : IDisposable
{
    // Where the tests write in the readback buffer, so a read that ignores its offset sees other bytes.
    private const int WriteOffset = 32;

    private static readonly byte[] _written = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 11))];

    private readonly DeviceFixture _fixture = new();
    private readonly MappedBufferBackend _backend;

    public ReadbackTests() => _backend = new MappedBufferBackend(_fixture.Backend);

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<int, CommandKind> CopiedFrames()
    {
        var rows = new TheoryData<int, CommandKind>();
        for (int frames = 1; frames <= 3; frames++)
        {
            rows.Add(frames, CommandKind.CopyBufferToBuffer);
            rows.Add(frames, CommandKind.CopyTextureToBuffer);
        }

        return rows;
    }

    public static TheoryData<int, bool> WrittenFrames()
    {
        var rows = new TheoryData<int, bool>();
        for (int frames = 1; frames <= 3; frames++)
        {
            rows.Add(frames, false);
            rows.Add(frames, true);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(CopiedFrames))]
    public void TryRead_FalseUntilComplete_ThenBytes(int framesInFlight, CommandKind kind)
    {
        GraphicsDevice device = Create(framesInFlight);
        var resources = TransferResources.Create(device);
        RunFrames(device, framesInFlight);

        // Frame N copies into the readback buffer: from a transient allocation, or from a texture, whose bytes the
        // simulated GPU writes when the frame executes.
        long written = device.FrameNumber;
        _ = device.BeginFrame(out CommandList commands);
        if (kind == CommandKind.CopyBufferToBuffer)
        {
            Assert.True(commands.AllocateTransient(_written.Length, TransientUsage.Vertex, out TransientAllocation source).IsSuccess);
            _written.CopyTo(source.Data);
            commands.CopyBufferToBuffer(new BufferCopy
            {
                Source = source.Buffer,
                SourceOffset = source.Offset,
                Destination = resources.Readback,
                DestinationOffset = WriteOffset,
                Size = _written.Length,
            });
        }
        else
        {
            commands.CopyTextureToBuffer(new TextureBufferCopy
            {
                Texture = new TextureLocation(resources.Depth, Aspect: TextureAspect.Depth),
                Region = new TextureRegion(0, 0, 4, 1),
                Buffer = resources.Readback,
                BufferOffset = WriteOffset,
                BytesPerRow = TextureBufferCopy.RowPitchAlignment,
            });
            ulong readback = _fixture.BackendDevice.Objects.GetId(resources.Readback);
            _backend.Device!.OnExecute = () => _written.CopyTo(new Span<byte>((byte*)_backend.Device.Buffers[readback].Pointer + WriteOffset, _written.Length));
        }

        AssertUnreadable(device, resources.Readback);
        _ = device.SubmitFrame(commands);
        _backend.Device!.OnExecute = null;

        AssertReadableFrom(device, resources.Readback, written, framesInFlight);
    }

    [Theory]
    [MemberData(nameof(WrittenFrames))]
    public void WriteBufferIntoReadback_CountsAsWrite(int framesInFlight, bool betweenFrames)
    {
        GraphicsDevice device = Create(framesInFlight);
        var resources = TransferResources.Create(device);
        RunFrames(device, framesInFlight);

        // Between frames the write lands in the next submitted frame, inside one in the open frame: frame N either way.
        long written = device.FrameNumber;
        CommandList commands = default;
        if (!betweenFrames)
        {
            _ = device.BeginFrame(out commands);
        }

        Assert.True(device.WriteBuffer(resources.Readback, WriteOffset, _written).IsSuccess);
        AssertUnreadable(device, resources.Readback);
        if (betweenFrames)
        {
            _ = device.BeginFrame(out commands);
            AssertUnreadable(device, resources.Readback);
        }

        _ = device.SubmitFrame(commands);
        AssertReadableFrom(device, resources.Readback, written, framesInFlight);
    }

    [Fact]
    public void TryRead_Unwritten_ReadsMemoryAsIs()
    {
        GraphicsDevice device = Create();
        var resources = TransferResources.Create(device);
        byte* memory = Mapped(resources.Readback);
        for (int i = 0; i < TransferResources.ReadbackSize; i++)
        {
            memory[i] = (byte)(i * 7);
        }

        // Never written: readable at once, in and between frames, from the offset given, up to the buffer's last byte.
        byte[] read = new byte[24];
        Assert.True(device.TryReadBuffer(resources.Readback, 100, read));
        Assert.Equal(Enumerable.Range(100, 24).Select(i => (byte)(i * 7)), read);
        _ = device.BeginFrame(out CommandList commands);
        byte[] whole = new byte[TransferResources.ReadbackSize];
        Assert.True(device.TryReadBuffer(resources.Readback, 0, whole));
        Assert.Equal(Enumerable.Range(0, (int)TransferResources.ReadbackSize).Select(i => (byte)(i * 7)), whole);
        Assert.True(device.TryReadBuffer(resources.Readback, TransferResources.ReadbackSize - 1, new byte[1]));

        // A buffer created in the slot of a written one starts unwritten.
        Assert.True(device.WriteBuffer(resources.Readback, 0, [1]).IsSuccess);
        AssertUnreadable(device, resources.Readback);
        device.Destroy(resources.Readback);
        BufferHandle again = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.CopyDestination, Memory = MemoryIntent.Readback }).Value;
        Assert.Equal(resources.Readback.Value.Index, again.Value.Index);
        Assert.True(device.TryReadBuffer(again, 0, new byte[64]));
        _ = device.SubmitFrame(commands);
    }

    [Fact]
    public void TryRead_NeverBlocks()
    {
        GraphicsDevice device = Create();
        var resources = TransferResources.Create(device);
        Assert.True(device.WriteBuffer(resources.Readback, WriteOffset, _written).IsSuccess);
        RunFrames(device, 1);

        // Pending: false without waiting or calling the backend at all, however often it is asked.
        byte[] read = new byte[_written.Length];
        int calls = _fixture.BackendDevice.Calls.Count;
        _backend.Device!.ForbidWaits = true;
        for (int i = 0; i < 3; i++)
        {
            Assert.False(device.TryReadBuffer(resources.Readback, WriteOffset, read));
        }

        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        _backend.Device.ForbidWaits = false;

        // Completed once slot 0 is waited for again: true, again without a backend call.
        RunFrames(device, 2);
        calls = _fixture.BackendDevice.Calls.Count;
        _backend.Device.ForbidWaits = true;
        Assert.True(device.TryReadBuffer(resources.Readback, WriteOffset, read));
        Assert.Equal(_written, read);
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        _backend.Device.ForbidWaits = false;
    }

    [Fact]
    public void LostDevice_RecordsNothingAndReadsFalse()
    {
        GraphicsDevice device = Create();
        var resources = TransferResources.Create(device);
        _fixture.Script.FailNext(Op.Execute, new EngineError(ErrorKind.Unavailable, "Injected loss.", -4));
        RunFrames(device, 1);
        Assert.Equal(FrameStatus.DeviceLost, device.BeginFrame(out CommandList commands));
        int calls = _fixture.BackendDevice.Calls.Count;
        long allocations = _fixture.Allocator.TotalAllocationCount;

        // Valid copies are taken and dropped: enough of them to outgrow the frame's stream if they were recorded.
        for (int i = 0; i < 100; i++)
        {
            foreach (CommandKind kind in TransferResources.Kinds)
            {
                resources.Record(commands, kind);
            }
        }

        // Invalid ones are still refused.
        _ = Assert.Throws<ArgumentException>(() => commands.CopyBufferToBuffer(resources.BufferToBuffer with { Size = 6 }));
        _ = Assert.Throws<StaleHandleException>(() => commands.CopyTextureToTexture(resources.TextureToTexture with
        {
            Source = new TextureLocation(new TextureHandle(Handle.FromRawValue(resources.Color.Value.RawValue + (1UL << 32)))),
        }));
        Assert.Equal(allocations, _fixture.Allocator.TotalAllocationCount);
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);

        // Never written, yet unreadable; misuse still throws.
        Assert.False(device.TryReadBuffer(resources.Readback, 0, new byte[4]));
        _ = Assert.Throws<ArgumentException>(() => device.TryReadBuffer(resources.Destination, 0, new byte[4]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => device.TryReadBuffer(resources.Readback, -1, new byte[4]));
        _ = device.SubmitFrame(commands);
        Assert.False(device.TryReadBuffer(resources.Readback, 0, new byte[4]));
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
    }

    public static TheoryData<long, int, string> OutOfRange() => new()
    {
        { -1, 4, "offset" },
        { long.MinValue, 4, "offset" },
        { TransferResources.ReadbackSize, 1, "offset" },
        { long.MaxValue, 1, "offset" },
        { 0, 0, "destination" },
        { 0, (int)TransferResources.ReadbackSize + 1, "destination" },
        { TransferResources.ReadbackSize - 4, 5, "destination" },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void TryRead_OutOfRange_Throws(long offset, int length, string parameter)
    {
        GraphicsDevice device = Create();
        var resources = TransferResources.Create(device);
        byte[] destination = new byte[length];
        Array.Fill(destination, (byte)0xEE);

        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => device.TryReadBuffer(resources.Readback, offset, destination));

        Assert.Equal(parameter, e.ParamName);
        Assert.Contains("'readback'", e.Message, StringComparison.Ordinal);
        Assert.All(destination, b => Assert.Equal(0xEE, b));
    }

    [Fact]
    public void TryRead_Misuse_Throws()
    {
        GraphicsDevice device = Create();
        var resources = TransferResources.Create(device);
        BufferHandle upload = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.CopySource, Memory = MemoryIntent.Upload }).Value;
        int calls = _fixture.BackendDevice.Calls.Count;

        // Only Readback buffers can be read; the message names the buffer and its memory.
        foreach (BufferHandle buffer in new[] { resources.Destination, upload })
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => device.TryReadBuffer(buffer, 0, new byte[4]));
            Assert.Equal("buffer", e.ParamName);
            Assert.Contains("Readback", e.Message, StringComparison.Ordinal);
        }

        Assert.Equal("buffer", Assert.Throws<ArgumentException>(() => device.TryReadBuffer(default, 0, new byte[4])).ParamName);
        device.Destroy(upload);
        _ = Assert.Throws<StaleHandleException>(() => device.TryReadBuffer(upload, 0, new byte[4]));
        Exception? offThread = null;
        var thread = new Thread(() => offThread = Record.Exception(() => device.TryReadBuffer(resources.Readback, 0, new byte[4])));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        _ = Assert.IsType<WrongThreadException>(offThread);
        Assert.Equal(calls + 1, _fixture.BackendDevice.Calls.Count);

        device.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => device.TryReadBuffer(resources.Readback, 0, new byte[4]));
    }

    private static void RunFrames(GraphicsDevice device, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _ = device.BeginFrame(out CommandList commands);
            _ = device.SubmitFrame(commands);
        }
    }

    private static void AssertUnreadable(GraphicsDevice device, BufferHandle readback)
    {
        byte[] read = new byte[TransferResources.ReadbackSize];
        Array.Fill(read, (byte)0xEE);
        Assert.False(device.TryReadBuffer(readback, 0, read));
        Assert.All(read, b => Assert.Equal(0xEE, b));
    }

    // From the frame after the write, the buffer stays unreadable until CompletedFrameNumber reaches the writing frame,
    // which the BeginFrame of frame written + framesInFlight (the slot's next frame) reports; then it reads the bytes.
    private static void AssertReadableFrom(GraphicsDevice device, BufferHandle readback, long written, int framesInFlight)
    {
        while (device.FrameNumber <= written + framesInFlight)
        {
            AssertUnreadable(device, readback);
            _ = device.BeginFrame(out CommandList commands);
            Assert.Equal(device.FrameNumber < written + framesInFlight, device.CompletedFrameNumber < written);
            if (device.CompletedFrameNumber < written)
            {
                AssertUnreadable(device, readback);
            }

            _ = device.SubmitFrame(commands);
        }

        Assert.Equal((written, written + framesInFlight + 1), (device.CompletedFrameNumber, device.FrameNumber));
        byte[] read = new byte[WriteOffset + _written.Length];
        Assert.True(device.TryReadBuffer(readback, 0, read));
        Assert.Equal([.. new byte[WriteOffset], .. _written], read);
    }

    private byte* Mapped(BufferHandle buffer) => (byte*)_backend.Device!.Buffers[_fixture.BackendDevice.Objects.GetId(buffer)].Pointer;

    private GraphicsDevice Create(int framesInFlight = 2) =>
        _fixture.Create(_backend, _fixture.Options() with { FramesInFlight = framesInFlight });
}
