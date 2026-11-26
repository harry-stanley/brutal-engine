using System;
using System.Text;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Validation;

namespace BrutalEngine.Graphics;

/// <content>Resource creation and destruction (docs/design/graphics.md sections 1 and 5).</content>
public sealed partial class GraphicsDevice
{
    private const string DestroyApi = nameof(GraphicsDevice) + "." + nameof(Destroy);

    /// <summary>Creates a buffer.</summary>
    /// <param name="description">The buffer's size, usage, memory intent and debug name.</param>
    /// <returns>
    /// The buffer's handle; the device owns the buffer until <see cref="Destroy(BufferHandle)"/> or <see cref="Dispose"/>.
    /// A backend failure is returned unchanged: <see cref="ErrorKind.OutOfMemory"/>, or <see cref="ErrorKind.Unavailable"/>
    /// after device loss.
    /// </returns>
    /// <exception cref="ArgumentException">The description breaks a structural rule (docs/design/graphics.md section 5).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value in the description is outside its range or enum.</exception>
    /// <exception cref="OutOfMemoryException">The device's handle table couldn't grow; the new backend buffer is destroyed.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The description is checked before the backend sees it. The memory intent picks the backend memory:
    /// <see cref="MemoryIntent.GpuOnly"/> is <see cref="BackendMemory.DeviceLocal"/>, <see cref="MemoryIntent.Upload"/>
    /// <see cref="BackendMemory.DeviceLocalHostVisiblePreferred"/> and <see cref="MemoryIntent.Readback"/>
    /// <see cref="BackendMemory.HostReadback"/>. A non-empty debug name names the backend object. Owner thread. Allocates.
    /// </remarks>
    public Result<BufferHandle> CreateBuffer(in BufferDescription description)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(CreateBuffer));
        ResourceValidator.Validate(description);
        BackendMemory memory = description.Memory switch
        {
            MemoryIntent.Upload => BackendMemory.DeviceLocalHostVisiblePreferred,
            MemoryIntent.Readback => BackendMemory.HostReadback,
            _ => BackendMemory.DeviceLocal,
        };

        Result<BackendBuffer> created = _backend.CreateBuffer(description, memory);
        return created.TryGetValue(out BackendBuffer buffer)
            ? new BufferHandle(Register(BackendObjectKind.Buffer, buffer.Id, description.DebugName))
            : created.Error;
    }

    /// <summary>Creates a sampler.</summary>
    /// <param name="description">The sampler's filters, address modes, border color, anisotropy, comparison and debug name.</param>
    /// <returns>
    /// The sampler's handle; the device owns the sampler until <see cref="Destroy(SamplerHandle)"/> or <see cref="Dispose"/>.
    /// A backend failure is returned unchanged: <see cref="ErrorKind.OutOfMemory"/>, or <see cref="ErrorKind.Unavailable"/>
    /// after device loss.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is outside its enum, <see cref="SamplerDescription.MaxAnisotropy"/> isn't positive, or it exceeds
    /// <see cref="GraphicsLimits.MaxSamplerAnisotropy"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="SamplerDescription.MaxAnisotropy"/> is above 1 without <see cref="GraphicsFeatures.SamplerAnisotropy"/>
    /// in <see cref="Capabilities"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The device's handle table couldn't grow; the new backend sampler is destroyed.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The description is checked against <see cref="Capabilities"/> before the backend sees it. A non-empty debug name
    /// names the backend object. Owner thread. Allocates.
    /// </remarks>
    public Result<SamplerHandle> CreateSampler(in SamplerDescription description)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(CreateSampler));
        ResourceValidator.Validate(description, Capabilities.Limits, Capabilities.Features);
        Result<ulong> created = _backend.CreateSampler(description);
        return created.TryGetValue(out ulong id)
            ? new SamplerHandle(Register(BackendObjectKind.Sampler, id, description.DebugName))
            : created.Error;
    }

    /// <summary>Destroys a buffer: its handle and every copy become stale at once.</summary>
    /// <param name="buffer">A live buffer of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="buffer"/> was destroyed already, or isn't this device's.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// With no frame submitted yet, the backend buffer is destroyed at once. Owner thread. Allocates nothing, except the
    /// exception.
    /// </remarks>
    public void Destroy(BufferHandle buffer) => DestroyObject(BackendObjectKind.Buffer, buffer.Value, nameof(buffer));

    /// <summary>Destroys a texture: its handle and every copy become stale at once.</summary>
    /// <param name="texture">A live texture of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="texture"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="texture"/> was destroyed already, or isn't this device's. Until textures can be created, every
    /// non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(TextureHandle texture) => DestroyObject(BackendObjectKind.Texture, texture.Value, nameof(texture));

    /// <summary>Destroys a texture view: its handle and every copy become stale at once.</summary>
    /// <param name="view">A live texture view of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="view"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="view"/> was destroyed already, or isn't this device's. Until texture views can be created, every
    /// non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(TextureViewHandle view) => DestroyObject(BackendObjectKind.TextureView, view.Value, nameof(view));

    /// <summary>Destroys a sampler: its handle and every copy become stale at once.</summary>
    /// <param name="sampler">A live sampler of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="sampler"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="sampler"/> was destroyed already, or isn't this device's.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// With no frame submitted yet, the backend sampler is destroyed at once. Owner thread. Allocates nothing, except the
    /// exception.
    /// </remarks>
    public void Destroy(SamplerHandle sampler) => DestroyObject(BackendObjectKind.Sampler, sampler.Value, nameof(sampler));

    /// <summary>Destroys a shader module: its handle and every copy become stale at once.</summary>
    /// <param name="shaderModule">A live shader module of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="shaderModule"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="shaderModule"/> was destroyed already, or isn't this device's. Until shader modules can be created,
    /// every non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(ShaderModuleHandle shaderModule) =>
        DestroyObject(BackendObjectKind.ShaderModule, shaderModule.Value, nameof(shaderModule));

    /// <summary>Destroys a bind group layout: its handle and every copy become stale at once.</summary>
    /// <param name="layout">A live bind group layout of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="layout"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="layout"/> was destroyed already, or isn't this device's. Until bind group layouts can be created,
    /// every non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(BindGroupLayoutHandle layout) => DestroyObject(BackendObjectKind.BindGroupLayout, layout.Value, nameof(layout));

    /// <summary>Destroys a pipeline layout: its handle and every copy become stale at once.</summary>
    /// <param name="layout">A live pipeline layout of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="layout"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="layout"/> was destroyed already, or isn't this device's. Until pipeline layouts can be created,
    /// every non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(PipelineLayoutHandle layout) => DestroyObject(BackendObjectKind.PipelineLayout, layout.Value, nameof(layout));

    /// <summary>Destroys a bind group: its handle and every copy become stale at once.</summary>
    /// <param name="bindGroup">A live bind group of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="bindGroup"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="bindGroup"/> was destroyed already, or isn't this device's. Until bind groups can be created, every
    /// non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(BindGroupHandle bindGroup) => DestroyObject(BackendObjectKind.BindGroup, bindGroup.Value, nameof(bindGroup));

    /// <summary>Destroys a graphics pipeline: its handle and every copy become stale at once.</summary>
    /// <param name="pipeline">A live graphics pipeline of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="pipeline"/> was destroyed already, or isn't this device's. Until graphics pipelines can be created,
    /// every non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(GraphicsPipelineHandle pipeline) =>
        DestroyObject(BackendObjectKind.GraphicsPipeline, pipeline.Value, nameof(pipeline));

    /// <summary>Destroys a compute pipeline: its handle and every copy become stale at once.</summary>
    /// <param name="pipeline">A live compute pipeline of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="pipeline"/> was destroyed already, or isn't this device's. Until compute pipelines can be created,
    /// every non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(ComputePipelineHandle pipeline) =>
        DestroyObject(BackendObjectKind.ComputePipeline, pipeline.Value, nameof(pipeline));

    /// <summary>Destroys a swapchain: its handle and every copy become stale at once.</summary>
    /// <param name="swapchain">A live swapchain of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="swapchain"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="swapchain"/> isn't a live swapchain of this device. Until swapchains can be created, every non-null
    /// handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(SwapchainHandle swapchain)
    {
        ThrowIfUnusable(DestroyApi);
        // TODO(#115): destroy a live swapchain and its surface once swapchains can be created; none can be live yet.
        HandleTables.ThrowSwapchainNotLive(swapchain.Value, nameof(swapchain));
    }

    // Records a new backend object under a new handle and names it; if recording fails, the backend object is destroyed
    // so nothing leaks, and the exception propagates.
    private Handle Register(BackendObjectKind kind, ulong id, string? debugName)
    {
        Handle handle;
        try
        {
            handle = _handles.Add(kind, id, debugName);
        }
        catch
        {
            _backend.Destroy(kind, id);
            throw;
        }

        if (!string.IsNullOrEmpty(debugName))
        {
            _backend.SetDebugName(kind, id, Encoding.UTF8.GetBytes(debugName));
        }

        return handle;
    }

    private void DestroyObject(BackendObjectKind kind, Handle handle, string parameterName)
    {
        ThrowIfUnusable(DestroyApi);
        // No frame has been submitted yet, so nothing can still use the object: it goes straight to the backend.
        // TODO(#113): queue it on the open frame's deletion queue, or the last submitted frame's, once frames exist.
        _backend.Destroy(kind, _handles.Remove(kind, handle, parameterName));
    }
}
