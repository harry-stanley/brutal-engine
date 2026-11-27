using System;
using System.Globalization;
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
    /// <exception cref="InvalidOperationException">
    /// Every buffer handle slot is live or retired (the kind's handle allocator is exhausted); the new backend buffer is
    /// destroyed.
    /// </exception>
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
            ? new BufferHandle(Register(BackendObjectKind.Buffer, buffer.Id, description.DebugName, link: default))
            : created.Error;
    }

    /// <summary>Creates a texture and its default view.</summary>
    /// <param name="description">The texture's format, size, layers, mip levels, sample count, usage and debug name.</param>
    /// <returns>
    /// The texture's handle; the device owns the texture and its default view until <see cref="Destroy(TextureHandle)"/> or
    /// <see cref="Dispose"/>. Fails with <see cref="ErrorKind.Unsupported"/> when <see cref="GetFormatSupport"/> lacks a use
    /// the usage needs, or <see cref="FormatSupport.Multisample"/> for a sample count above 1 (the message names the
    /// format and what is missing), or when a color format's sample count above 1 isn't in
    /// <see cref="GraphicsLimits.ColorSampleCounts"/> (the message names the count). A backend failure is returned
    /// unchanged: <see cref="ErrorKind.OutOfMemory"/>, or <see cref="ErrorKind.Unavailable"/> after device loss; a failed
    /// default view destroys the texture first.
    /// </returns>
    /// <exception cref="ArgumentException">The description breaks a structural rule (docs/design/graphics.md section 5).</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value in the description is outside its range or enum, or a side or the layers exceed <see cref="Capabilities"/>'
    /// limits.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The device's handle table couldn't grow; the new backend objects are destroyed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Every texture or texture view handle slot is live or retired (that kind's handle allocator is exhausted); the new
    /// backend objects are destroyed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// <para>
    /// The description is checked first, then the format's support: <see cref="TextureUsage.Sampled"/>,
    /// <see cref="TextureUsage.Storage"/>, <see cref="TextureUsage.ColorTarget"/>, <see cref="TextureUsage.DepthStencilTarget"/>,
    /// <see cref="TextureUsage.CopySource"/> and <see cref="TextureUsage.CopyDestination"/> each need the
    /// <see cref="FormatSupport"/> flag of the same name, and a sample count above 1 needs
    /// <see cref="FormatSupport.Multisample"/>. For a color format the count must also be in
    /// <see cref="GraphicsLimits.ColorSampleCounts"/>; depth formats need only the flag, because their sample counts
    /// aren't reported yet. Only then does the backend see it.
    /// </para>
    /// <para>
    /// The default view covers every mip level and layer: a <see cref="TextureViewDimension.Texture2D"/> view for one layer,
    /// <see cref="TextureViewDimension.Texture2DArray"/> for more; <see cref="GetDefaultView"/> returns it. A non-empty debug
    /// name names both backend objects. Owner thread. Allocates.
    /// </para>
    /// </remarks>
    public Result<TextureHandle> CreateTexture(in TextureDescription description)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(CreateTexture));
        ResourceValidator.Validate(description, Capabilities.Limits, Capabilities.Features);
        FormatSupport missing = RequiredSupport(description) & ~_backend.GetFormatSupport(description.Format);
        if (missing != FormatSupport.None)
        {
            return new EngineError(ErrorKind.Unsupported, string.Create(
                CultureInfo.InvariantCulture,
                $"CreateTexture '{description.DebugName}': format {description.Format} lacks {missing} support on this device (usage {description.Usage}, {description.SampleCount} samples)."));
        }

        SampleCounts colorCounts = Capabilities.Limits.ColorSampleCounts;
        if (description.SampleCount > 1 && !TextureFormatInfo.IsDepth(description.Format)
            && (colorCounts & (SampleCounts)description.SampleCount) == 0)
        {
            return new EngineError(ErrorKind.Unsupported, string.Create(
                CultureInfo.InvariantCulture,
                $"CreateTexture '{description.DebugName}': {description.SampleCount} samples aren't supported for color format {description.Format} on this device (supported counts: {colorCounts})."));
        }

        Result<ulong> created = _backend.CreateTexture(description);
        if (!created.TryGetValue(out ulong textureId))
        {
            return created.Error;
        }

        Handle texture = Register(BackendObjectKind.Texture, textureId, description.DebugName, link: default);
        var viewDescription = new TextureViewDescription
        {
            Texture = new TextureHandle(texture),
            Dimension = description.ArrayLayers > 1 ? TextureViewDimension.Texture2DArray : TextureViewDimension.Texture2D,
            MipLevelCount = description.MipLevels,
            ArrayLayerCount = description.ArrayLayers,
            DebugName = description.DebugName,
        };
        Result<ulong> view = _backend.CreateTextureView(viewDescription);
        Handle defaultView = default;
        try
        {
            if (view.TryGetValue(out ulong viewId))
            {
                defaultView = Register(BackendObjectKind.TextureView, viewId, description.DebugName, texture);
            }
        }
        finally
        {
            if (defaultView.IsNull)
            {
                _backend.Destroy(BackendObjectKind.Texture, _handles.Remove(BackendObjectKind.Texture, texture, nameof(description)));
            }
        }

        if (defaultView.IsNull)
        {
            return view.Error;
        }

        _handles.SetTexture(texture, description, defaultView);
        return new TextureHandle(texture);
    }

    /// <summary>Returns the default view a texture was created with.</summary>
    /// <param name="texture">A live texture of this device.</param>
    /// <returns>
    /// The view of every mip level and layer, live as long as the texture; the same handle on every call.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="texture"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="texture"/> isn't a live texture of this device: it was destroyed already, or was never issued
    /// here. A handle from another device is detected only when its index or generation doesn't match a live texture
    /// here; one that matches can't be told apart from it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public TextureViewHandle GetDefaultView(TextureHandle texture)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(GetDefaultView));
        _handles.ThrowIfNotLive(BackendObjectKind.Texture, texture.Value, nameof(texture));
        return new TextureViewHandle(_handles.GetLink(BackendObjectKind.Texture, texture.Value));
    }

    /// <summary>Creates a view of part of a texture.</summary>
    /// <param name="description">The texture, the view's dimension, its mip and layer range, and its debug name.</param>
    /// <returns>
    /// The view's handle; the device owns the view until <see cref="Destroy(TextureViewHandle)"/>, the destruction of its
    /// texture, or <see cref="Dispose"/>. A backend failure is returned unchanged: <see cref="ErrorKind.OutOfMemory"/>, or
    /// <see cref="ErrorKind.Unavailable"/> after device loss.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The description's texture is the null handle, or a <see cref="TextureViewDimension.Texture2D"/> view has more than one
    /// layer.
    /// </exception>
    /// <exception cref="StaleHandleException">
    /// The description's texture isn't a live texture of this device: it was destroyed already, or was never issued
    /// here. A handle from another device is detected only when its index or generation doesn't match a live texture
    /// here; one that matches can't be told apart from it.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The view's range lies outside its texture, or a value is outside its enum.</exception>
    /// <exception cref="OutOfMemoryException">The device's handle table couldn't grow; the new backend view is destroyed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Every texture view handle slot is live or retired (the kind's handle allocator is exhausted); the new backend view
    /// is destroyed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The texture and the range are checked before the backend sees the description. A non-empty debug name names the
    /// backend object. Owner thread. Allocates.
    /// </remarks>
    public Result<TextureViewHandle> CreateTextureView(in TextureViewDescription description)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(CreateTextureView));
        Handle texture = description.Texture.Value;
        _handles.ThrowIfNotLive(BackendObjectKind.Texture, texture, nameof(description));
        ResourceValidator.Validate(description, _handles.GetTexture(texture));
        Result<ulong> created = _backend.CreateTextureView(description);
        return created.TryGetValue(out ulong id)
            ? new TextureViewHandle(Register(BackendObjectKind.TextureView, id, description.DebugName, texture))
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
    /// in <see cref="Capabilities"/>, or every sampler handle slot is live or retired (the kind's handle allocator is
    /// exhausted), in which case the new backend sampler is destroyed.
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
            ? new SamplerHandle(Register(BackendObjectKind.Sampler, id, description.DebugName, link: default))
            : created.Error;
    }

    /// <summary>Destroys a buffer: its handle and every copy become stale at once.</summary>
    /// <param name="buffer">A live buffer of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="buffer"/> isn't a live buffer of this device: it was destroyed already, or was never issued
    /// here. A handle from another device is detected only when its index or generation doesn't match a live buffer
    /// here; one that matches can't be told apart from it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// With no frame submitted yet, the backend buffer is destroyed at once. Owner thread. Allocates nothing, except the
    /// exception.
    /// </remarks>
    public void Destroy(BufferHandle buffer) => DestroyObject(BackendObjectKind.Buffer, buffer.Value, nameof(buffer));

    /// <summary>Destroys a texture and every view of it: their handles and every copy become stale at once.</summary>
    /// <param name="texture">A live texture of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="texture"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="texture"/> isn't a live texture of this device: it was destroyed already, or was never issued
    /// here. A handle from another device is detected only when its index or generation doesn't match a live texture
    /// here; one that matches can't be told apart from it, so that texture and its views are destroyed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The default view and every view <see cref="CreateTextureView"/> made of the texture are destroyed first, then the
    /// texture. With no frame submitted yet, the backend objects are destroyed at once. Owner thread. Allocates nothing,
    /// except the exception.
    /// </remarks>
    public void Destroy(TextureHandle texture)
    {
        ThrowIfUnusable(DestroyApi);
        _handles.ThrowIfNotLive(BackendObjectKind.Texture, texture.Value, nameof(texture));
        int cursor = 0;
        while (_handles.TryRemoveViewOf(texture.Value, ref cursor, out ulong viewId))
        {
            // TODO(#113): queue the view on the frame's deletion queue, as DestroyObject will, once frames exist.
            _backend.Destroy(BackendObjectKind.TextureView, viewId);
        }

        // TODO(#113): queue the texture on the frame's deletion queue, after its views, once frames exist.
        _backend.Destroy(BackendObjectKind.Texture, _handles.Remove(BackendObjectKind.Texture, texture.Value, nameof(texture)));
    }

    /// <summary>Destroys a texture view made by <see cref="CreateTextureView"/>: its handle and every copy become stale at once.</summary>
    /// <param name="view">A live texture view of this device, not a default view.</param>
    /// <exception cref="ArgumentException"><paramref name="view"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="view"/> isn't a live texture view of this device: it was destroyed already (also by destroying its
    /// texture), or was never issued here. A handle from another device is detected only when its index or generation
    /// doesn't match a live texture view here; one that matches can't be told apart from it (and if that view is a
    /// default view, the call throws <see cref="InvalidOperationException"/> instead).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="view"/> is a texture's default view (<see cref="GetDefaultView"/>), which lives as long as its
    /// texture; nothing is destroyed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// With no frame submitted yet, the backend view is destroyed at once. Owner thread. Allocates nothing, except the
    /// exception.
    /// </remarks>
    public void Destroy(TextureViewHandle view)
    {
        ThrowIfUnusable(DestroyApi);
        Handle handle = view.Value;
        _handles.ThrowIfNotLive(BackendObjectKind.TextureView, handle, nameof(view));
        if (_handles.GetLink(BackendObjectKind.Texture, _handles.GetLink(BackendObjectKind.TextureView, handle)) == handle)
        {
            throw new InvalidOperationException($"Destroy: {view} is its texture's default view, which is destroyed with the texture.");
        }

        // TODO(#113): queue the view on the frame's deletion queue, as DestroyObject will, once frames exist.
        _backend.Destroy(BackendObjectKind.TextureView, _handles.Remove(BackendObjectKind.TextureView, handle, nameof(view)));
    }

    /// <summary>Destroys a sampler: its handle and every copy become stale at once.</summary>
    /// <param name="sampler">A live sampler of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="sampler"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="sampler"/> isn't a live sampler of this device: it was destroyed already, or was never issued
    /// here. A handle from another device is detected only when its index or generation doesn't match a live sampler
    /// here; one that matches can't be told apart from it.
    /// </exception>
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
    /// <paramref name="shaderModule"/> isn't a live shader module of this device: it was destroyed already, or was
    /// never issued here. A handle from another device is detected only when its index or generation doesn't match a
    /// live shader module here; one that matches can't be told apart from it. Until shader modules can be created,
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
    /// <paramref name="layout"/> isn't a live bind group layout of this device: it was destroyed already, or was never
    /// issued here. A handle from another device is detected only when its index or generation doesn't match a live
    /// bind group layout here; one that matches can't be told apart from it. Until bind group layouts can be created,
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
    /// <paramref name="layout"/> isn't a live pipeline layout of this device: it was destroyed already, or was never
    /// issued here. A handle from another device is detected only when its index or generation doesn't match a live
    /// pipeline layout here; one that matches can't be told apart from it. Until pipeline layouts can be created, every
    /// non-null handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(PipelineLayoutHandle layout) => DestroyObject(BackendObjectKind.PipelineLayout, layout.Value, nameof(layout));

    /// <summary>Destroys a bind group: its handle and every copy become stale at once.</summary>
    /// <param name="bindGroup">A live bind group of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="bindGroup"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="bindGroup"/> isn't a live bind group of this device: it was destroyed already, or was never
    /// issued here. A handle from another device is detected only when its index or generation doesn't match a live
    /// bind group here; one that matches can't be told apart from it. Until bind groups can be created, every non-null
    /// handle is stale.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public void Destroy(BindGroupHandle bindGroup) => DestroyObject(BackendObjectKind.BindGroup, bindGroup.Value, nameof(bindGroup));

    /// <summary>Destroys a graphics pipeline: its handle and every copy become stale at once.</summary>
    /// <param name="pipeline">A live graphics pipeline of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeline"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="pipeline"/> isn't a live graphics pipeline of this device: it was destroyed already, or was
    /// never issued here. A handle from another device is detected only when its index or generation doesn't match a
    /// live graphics pipeline here; one that matches can't be told apart from it. Until graphics pipelines can be
    /// created, every non-null handle is stale.
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
    /// <paramref name="pipeline"/> isn't a live compute pipeline of this device: it was destroyed already, or was never
    /// issued here. A handle from another device is detected only when its index or generation doesn't match a live
    /// compute pipeline here; one that matches can't be told apart from it. Until compute pipelines can be created,
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
    private Handle Register(BackendObjectKind kind, ulong id, string? debugName, Handle link)
    {
        Handle handle;
        try
        {
            handle = _handles.Add(kind, id, debugName, link);
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

    // The format support each usage needs, plus multisampling for more than one sample.
    private static FormatSupport RequiredSupport(in TextureDescription description)
    {
        TextureUsage usage = description.Usage;
        FormatSupport needed = FormatSupport.None;
        needed |= (usage & TextureUsage.Sampled) != 0 ? FormatSupport.Sampled : FormatSupport.None;
        needed |= (usage & TextureUsage.Storage) != 0 ? FormatSupport.Storage : FormatSupport.None;
        needed |= (usage & TextureUsage.ColorTarget) != 0 ? FormatSupport.ColorTarget : FormatSupport.None;
        needed |= (usage & TextureUsage.DepthStencilTarget) != 0 ? FormatSupport.DepthStencilTarget : FormatSupport.None;
        needed |= (usage & TextureUsage.CopySource) != 0 ? FormatSupport.CopySource : FormatSupport.None;
        needed |= (usage & TextureUsage.CopyDestination) != 0 ? FormatSupport.CopyDestination : FormatSupport.None;
        needed |= description.SampleCount > 1 ? FormatSupport.Multisample : FormatSupport.None;
        return needed;
    }

    private void DestroyObject(BackendObjectKind kind, Handle handle, string parameterName)
    {
        ThrowIfUnusable(DestroyApi);
        // No frame has been submitted yet, so nothing can still use the object: it goes straight to the backend.
        // TODO(#113): queue it on the open frame's deletion queue, or the last submitted frame's, once frames exist.
        _backend.Destroy(kind, _handles.Remove(kind, handle, parameterName));
    }
}
