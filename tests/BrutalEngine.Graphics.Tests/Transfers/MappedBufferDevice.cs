using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Tests.Transfers;

/// <summary>
/// Forwards every call to the fake's device and keeps what the fake doesn't expose: each buffer's description, memory and
/// mapped pointer, so a test can read host memory, plus a hook that runs inside <see cref="Execute"/>, when the GPU would
/// start reading the frame's memory. <see cref="Execute"/> also runs the plan's copies between two mapped buffers, as the
/// GPU would, so a readback buffer holds what a frame copied into it; <see cref="ForbidWaits"/> turns every blocking call
/// into a test failure.
/// </summary>
internal sealed unsafe class MappedBufferDevice(IBackendDevice inner) : IBackendDevice
{
    /// <summary>Every buffer created, by id, with its description, memory and mapped pointer (0 when not mapped).</summary>
    public Dictionary<ulong, (BufferDescription Description, BackendMemory Memory, nint Pointer)> Buffers { get; } = [];

    /// <summary>Every host-visible buffer created, in order.</summary>
    public List<ulong> HostBuffers { get; } = [];

    /// <summary>Runs at the start of each <see cref="Execute"/>; <see langword="null"/> for none.</summary>
    public Action? OnExecute { get; set; }

    /// <summary>While set, <see cref="WaitForFrame"/> and <see cref="WaitIdle"/> throw instead of waiting.</summary>
    public bool ForbidWaits { get; set; }

    public GraphicsAdapterInfo Adapter => inner.Adapter;

    public GraphicsCapabilities Capabilities => inner.Capabilities;

    public long ValidationErrors => inner.ValidationErrors;

    public long ValidationWarnings => inner.ValidationWarnings;

    /// <summary>Copies bytes out of a live host-visible buffer's mapped memory.</summary>
    public byte[] Read(ulong buffer, long offset, int length) =>
        new ReadOnlySpan<byte>((byte*)Buffers[buffer].Pointer + offset, length).ToArray();

    public Result<BackendBuffer> CreateBuffer(in BufferDescription description, BackendMemory memory)
    {
        Result<BackendBuffer> created = inner.CreateBuffer(description, memory);
        if (created.TryGetValue(out BackendBuffer buffer))
        {
            Buffers[buffer.Id] = (description, memory, buffer.MappedPointer);
            if (buffer.MappedPointer != 0)
            {
                HostBuffers.Add(buffer.Id);
            }
        }

        return created;
    }

    public Result Execute(in FramePlan plan)
    {
        OnExecute?.Invoke();
        var reader = new CommandReader(plan.Commands);
        while (reader.MoveNext())
        {
            if (reader.Kind == CommandKind.CopyBufferToBuffer)
            {
                ref readonly CopyBufferToBufferCommand copy = ref reader.Payload<CopyBufferToBufferCommand>();
                nint source = Buffers[copy.Source].Pointer;
                nint destination = Buffers[copy.Destination].Pointer;
                if (source != 0 && destination != 0)
                {
                    new ReadOnlySpan<byte>((byte*)source + copy.SourceOffset, (int)copy.Size)
                        .CopyTo(new Span<byte>((byte*)destination + copy.DestinationOffset, (int)copy.Size));
                }
            }
        }

        return inner.Execute(plan);
    }

    public FormatSupport GetFormatSupport(TextureFormat format) => inner.GetFormatSupport(format);

    public Result<ulong> CreateTexture(in TextureDescription description) => inner.CreateTexture(description);

    public Result<ulong> CreateTextureView(in TextureViewDescription description) => inner.CreateTextureView(description);

    public Result<ulong> CreateSampler(in SamplerDescription description) => inner.CreateSampler(description);

    public void SetDebugName(BackendObjectKind kind, ulong id, ReadOnlySpan<byte> utf8Name) => inner.SetDebugName(kind, id, utf8Name);

    public void Destroy(BackendObjectKind kind, ulong id) => inner.Destroy(kind, id);

    public Result<ulong> CreateSurface(WindowSurfaceSource source) => inner.CreateSurface(source);

    public void DestroySurface(ulong surface) => inner.DestroySurface(surface);

    public Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest request) => inner.CreateSwapchain(request);

    public void DestroySwapchain(ulong swapchain) => inner.DestroySwapchain(swapchain);

    public void GetSwapchainImage(ulong swapchain, int imageIndex, out ulong texture, out ulong view) =>
        inner.GetSwapchainImage(swapchain, imageIndex, out texture, out view);

    public BackendAcquire AcquireImage(ulong swapchain, int frameSlot) => inner.AcquireImage(swapchain, frameSlot);

    public BackendPresent Present(ulong swapchain, int imageIndex) => inner.Present(swapchain, imageIndex);

    public Result WaitForFrame(int frameSlot) =>
        ForbidWaits ? throw new InvalidOperationException("WaitForFrame while waits are forbidden.") : inner.WaitForFrame(frameSlot);

    public Result<bool> IsFrameComplete(int frameSlot) => inner.IsFrameComplete(frameSlot);

    public Result BeginFrame(int frameSlot) => inner.BeginFrame(frameSlot);

    public bool TryReadTimestamps(int frameSlot, Span<ulong> ticks) => inner.TryReadTimestamps(frameSlot, ticks);

    public Result WaitIdle() =>
        ForbidWaits ? throw new InvalidOperationException("WaitIdle while waits are forbidden.") : inner.WaitIdle();

    public void InjectValidationError() => inner.InjectValidationError();

    public void Dispose() => inner.Dispose();
}
