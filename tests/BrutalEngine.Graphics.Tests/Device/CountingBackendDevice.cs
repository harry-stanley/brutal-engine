using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// Forwards every call to the fake's device and counts <see cref="Dispose"/> calls, including the ones the fake ignores
/// because it is already disposed, so a test sees a second dispose the fake alone would hide.
/// </summary>
internal sealed class CountingBackendDevice(IBackendDevice inner) : IBackendDevice
{
    public int DisposeCalls { get; private set; }

    public GraphicsAdapterInfo Adapter => inner.Adapter;

    public GraphicsCapabilities Capabilities => inner.Capabilities;

    public long ValidationErrors => inner.ValidationErrors;

    public long ValidationWarnings => inner.ValidationWarnings;

    public FormatSupport GetFormatSupport(TextureFormat format) => inner.GetFormatSupport(format);

    public Result<BackendBuffer> CreateBuffer(in BufferDescription description, BackendMemory memory) => inner.CreateBuffer(description, memory);

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

    public Result WaitForFrame(int frameSlot) => inner.WaitForFrame(frameSlot);

    public Result<bool> IsFrameComplete(int frameSlot) => inner.IsFrameComplete(frameSlot);

    public Result BeginFrame(int frameSlot) => inner.BeginFrame(frameSlot);

    public Result Execute(in FramePlan plan) => inner.Execute(plan);

    public bool TryReadTimestamps(int frameSlot, Span<ulong> ticks) => inner.TryReadTimestamps(frameSlot, ticks);

    public Result WaitIdle() => inner.WaitIdle();

    public void InjectValidationError() => inner.InjectValidationError();

    public void Dispose()
    {
        DisposeCalls++;
        inner.Dispose();
    }
}
