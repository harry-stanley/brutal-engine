using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// Forwards every call to the fake's device, but hands the front end each object id the fake issues negated
/// (<see cref="Flip"/>: 1 becomes <see cref="ulong.MaxValue"/>, 2 becomes one less, and so on), so ids fall as objects are
/// created where the fake's rise. A test run on both sees whether the front end orders objects by creation or by id. Ids
/// the front end passes back are turned into the fake's again before they reach it.
/// </summary>
/// <remarks>
/// The fake resolves a view's texture handle through the table it was created with. That is <paramref name="fakeObjects"/>,
/// not the front end's <paramref name="objects"/>, which holds negated ids: <see cref="CreateTextureView"/> records the
/// texture's fake id there for the length of the call. Surface and swapchain ids aren't object ids and pass through
/// unchanged. <see cref="GetSwapchainImage"/> and <see cref="Execute"/> name object ids this wrapper doesn't map, so they
/// throw <see cref="NotSupportedException"/> rather than hand the fake ids it never issued.
/// </remarks>
internal sealed class ReversedIdBackendDevice(IBackendDevice inner, BackendObjectTable objects, BackendObjectTable fakeObjects) : IBackendDevice
{
    /// <summary>The front end's table, whose ids are the negated ones.</summary>
    public BackendObjectTable Objects => objects;

    public GraphicsAdapterInfo Adapter => inner.Adapter;

    public GraphicsCapabilities Capabilities => inner.Capabilities;

    public long ValidationErrors => inner.ValidationErrors;

    public long ValidationWarnings => inner.ValidationWarnings;

    /// <summary>Turns a fake id into the front end's, and back: two's complement negation, which keeps 0 as 0.</summary>
    public static ulong Flip(ulong id) => unchecked(0UL - id);

    public FormatSupport GetFormatSupport(TextureFormat format) => inner.GetFormatSupport(format);

    public Result<BackendBuffer> CreateBuffer(in BufferDescription description, BackendMemory memory)
    {
        Result<BackendBuffer> created = inner.CreateBuffer(description, memory);
        return created.TryGetValue(out BackendBuffer buffer) ? buffer with { Id = Flip(buffer.Id) } : created;
    }

    public Result<ulong> CreateTexture(in TextureDescription description) => FlipResult(inner.CreateTexture(description));

    public Result<ulong> CreateTextureView(in TextureViewDescription description)
    {
        Handle texture = description.Texture.Value;
        fakeObjects.Set(BackendObjectKind.Texture, texture, Flip(objects.GetId(description.Texture)));
        try
        {
            return FlipResult(inner.CreateTextureView(description));
        }
        finally
        {
            fakeObjects.Remove(BackendObjectKind.Texture, texture);
        }
    }

    public Result<ulong> CreateSampler(in SamplerDescription description) => FlipResult(inner.CreateSampler(description));

    public void SetDebugName(BackendObjectKind kind, ulong id, ReadOnlySpan<byte> utf8Name) => inner.SetDebugName(kind, Flip(id), utf8Name);

    public void Destroy(BackendObjectKind kind, ulong id) => inner.Destroy(kind, Flip(id));

    public Result<ulong> CreateSurface(WindowSurfaceSource source) => inner.CreateSurface(source);

    public void DestroySurface(ulong surface) => inner.DestroySurface(surface);

    public Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest request) => inner.CreateSwapchain(request);

    public void DestroySwapchain(ulong swapchain) => inner.DestroySwapchain(swapchain);

    public void GetSwapchainImage(ulong swapchain, int imageIndex, out ulong texture, out ulong view) =>
        throw new NotSupportedException("Swapchain image ids aren't negated by this wrapper.");

    public BackendAcquire AcquireImage(ulong swapchain, int frameSlot) => inner.AcquireImage(swapchain, frameSlot);

    public BackendPresent Present(ulong swapchain, int imageIndex) => inner.Present(swapchain, imageIndex);

    public Result WaitForFrame(int frameSlot) => inner.WaitForFrame(frameSlot);

    public Result<bool> IsFrameComplete(int frameSlot) => inner.IsFrameComplete(frameSlot);

    public Result BeginFrame(int frameSlot) => inner.BeginFrame(frameSlot);

    public Result Execute(in FramePlan plan) =>
        throw new NotSupportedException("Frame plans resolve handles the fake's table doesn't hold under this wrapper.");

    public bool TryReadTimestamps(int frameSlot, Span<ulong> ticks) => inner.TryReadTimestamps(frameSlot, ticks);

    public Result WaitIdle() => inner.WaitIdle();

    public void InjectValidationError() => inner.InjectValidationError();

    public void Dispose() => inner.Dispose();

    private static Result<ulong> FlipResult(Result<ulong> created) => created.TryGetValue(out ulong id) ? Flip(id) : created;
}
