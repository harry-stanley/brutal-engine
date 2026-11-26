namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The kind of one record in a frame's command stream, which says what the record does and which payload struct it carries
/// (docs/design/graphics.md, "The frame plan").
/// </summary>
/// <remarks>
/// <para>
/// Each record starts with its kind, followed by an unmanaged payload struct named after the kind plus <c>Command</c>
/// (some kinds share one, as each member says) and, for some kinds, a variable-length trailing part. A backend reads the
/// records in order with <c>CommandReader</c> and switches on the kind.
/// </para>
/// <para>
/// The numeric values are part of the stream format and never change: new kinds are added at the end. A 16-bit value, so
/// it fits the record header.
/// </para>
/// </remarks>
public enum CommandKind : ushort
{
    /// <summary>
    /// Resource transitions the planner inserted before the work after it. Trailing: <c>ResourceTransition[Count]</c>.
    /// Payload: <c>BarrierCommand</c>.
    /// </summary>
    Barrier = 0,

    /// <summary>A copy of a byte range from one buffer to another. Payload: <c>CopyBufferToBufferCommand</c>.</summary>
    CopyBufferToBuffer = 1,

    /// <summary>
    /// A copy from rows in a buffer to a region of one texture subresource. Shares its payload with <see
    /// cref="CopyTextureToBuffer"/>. Payload: <c>CopyBufferTextureCommand</c>.
    /// </summary>
    CopyBufferToTexture = 2,

    /// <summary>
    /// A copy from a region of one texture subresource to rows in a buffer. Shares its payload with <see
    /// cref="CopyBufferToTexture"/>. Payload: <c>CopyBufferTextureCommand</c>.
    /// </summary>
    CopyTextureToBuffer = 3,

    /// <summary>
    /// A copy of a region from one texture subresource to another. Payload: <c>CopyTextureToTextureCommand</c>.
    /// </summary>
    CopyTextureToTexture = 4,

    /// <summary>
    /// The start of a render pass. Trailing: one <c>AttachmentCommand</c> per color attachment, then one for the depth
    /// attachment if the pass has one. Payload: <c>BeginRenderPassCommand</c>.
    /// </summary>
    BeginRenderPass = 5,

    /// <summary>
    /// The end of the open render pass. Shares its payload with <see cref="EndComputePass"/>. Payload:
    /// <c>EndPassCommand</c>.
    /// </summary>
    EndRenderPass = 6,

    /// <summary>The start of a compute pass. Payload: <c>BeginComputePassCommand</c>.</summary>
    BeginComputePass = 7,

    /// <summary>
    /// The end of the open compute pass. Shares its payload with <see cref="EndRenderPass"/>. Payload:
    /// <c>EndPassCommand</c>.
    /// </summary>
    EndComputePass = 8,

    /// <summary>
    /// Binds a graphics or compute pipeline for the draws or dispatches after it. Payload: <c>SetPipelineCommand</c>.
    /// </summary>
    SetPipeline = 9,

    /// <summary>Binds a bind group at a group index. Payload: <c>SetBindGroupCommand</c>.</summary>
    SetBindGroup = 10,

    /// <summary>Binds a vertex buffer at a slot. Payload: <c>SetVertexBufferCommand</c>.</summary>
    SetVertexBuffer = 11,

    /// <summary>Binds the index buffer and its index format. Payload: <c>SetIndexBufferCommand</c>.</summary>
    SetIndexBuffer = 12,

    /// <summary>Sets the viewport for the draws after it. Payload: <c>SetViewportCommand</c>.</summary>
    SetViewport = 13,

    /// <summary>Sets the scissor rectangle for the draws after it. Payload: <c>SetScissorCommand</c>.</summary>
    SetScissor = 14,

    /// <summary>
    /// Writes push-constant bytes. Trailing: the bytes, <c>byte[Size]</c>. Payload: <c>PushConstantsCommand</c>.
    /// </summary>
    PushConstants = 15,

    /// <summary>A non-indexed draw. Payload: <c>DrawCommand</c>.</summary>
    Draw = 16,

    /// <summary>An indexed draw. Payload: <c>DrawIndexedCommand</c>.</summary>
    DrawIndexed = 17,

    /// <summary>A compute dispatch. Payload: <c>DispatchCommand</c>.</summary>
    Dispatch = 18,

    /// <summary>
    /// Opens a labelled debug group. Shares its payload with <see cref="PopDebugGroup"/>. Payload:
    /// <c>DebugGroupCommand</c>.
    /// </summary>
    PushDebugGroup = 19,

    /// <summary>
    /// Closes the innermost open debug group. Shares its payload with <see cref="PushDebugGroup"/>. Payload:
    /// <c>DebugGroupCommand</c>.
    /// </summary>
    PopDebugGroup = 20,

    /// <summary>
    /// Clears an acquired swapchain image that no pass wrote, so the presentation engine never shows uninitialised
    /// memory. Payload: <c>ClearSwapchainImageCommand</c>.
    /// </summary>
    ClearSwapchainImage = 21,
}
