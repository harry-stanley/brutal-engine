using System;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics;

/// <summary>
/// The recorder of a device's open frame (docs/design/graphics.md sections 4 and 9): <see cref="GraphicsDevice.BeginFrame"/>
/// hands one out, and <see cref="GraphicsDevice.SubmitFrame"/> takes it back.
/// </summary>
/// <remarks>
/// A small value tied to one device and one frame number. Every copy is valid only while that frame is open: after the
/// frame was submitted, or on another device, using it throws <see cref="InvalidOperationException"/>.
/// <c>default(CommandList)</c> belongs to no frame. Owner thread of its device. Copying allocates nothing.
/// </remarks>
public readonly struct CommandList
{
    internal CommandList(GraphicsDevice device, long frameNumber)
    {
        Device = device;
        FrameNumber = frameNumber;
    }

    /// <summary>The device whose frame this is; <see langword="null"/> for <c>default</c>.</summary>
    internal GraphicsDevice? Device { get; }

    /// <summary>The frame's number on its device.</summary>
    internal long FrameNumber { get; }

    /// <summary>Allocates per-frame ring memory that the CPU writes and this frame's GPU work reads without a copy.</summary>
    /// <param name="byteCount">The bytes needed: from 1 to <see cref="int.MaxValue"/>.</param>
    /// <param name="usage">What the memory will be bound as: one or more <see cref="TransientUsage"/> flags.</param>
    /// <param name="allocation">
    /// The memory, its alias buffer handle and its offset, valid until <see cref="GraphicsDevice.SubmitFrame"/>;
    /// <c>default</c> on failure.
    /// </param>
    /// <returns>
    /// Success once the memory is reserved. Fails with <see cref="ErrorKind.Unavailable"/> on a lost device, or with the
    /// backend's failure (<see cref="ErrorKind.OutOfMemory"/>) when the ring or a dedicated block can't be created; then
    /// nothing is reserved or counted.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The list isn't its device's open frame: <c>default</c>, or a copy from a submitted frame. Or every buffer handle
    /// slot is live or retired (the device's buffer handle allocator is exhausted), so no alias handle can be issued;
    /// the memory stays unused.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="byteCount"/> is below 1 or above <see cref="int.MaxValue"/>, or <paramref name="usage"/> has a flag
    /// that isn't a <see cref="TransientUsage"/> member.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="usage"/> has no flag.</exception>
    /// <exception cref="OutOfMemoryException">The device's handle tables couldn't grow; the memory stays unused.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the device's owner thread.</exception>
    /// <remarks>
    /// <para>
    /// The memory comes from the frame slot's upload ring, after the frame's writes and earlier allocations, at the next
    /// offset aligned for <paramref name="usage"/> (see <see cref="TransientAllocation.Offset"/>; several flags take the
    /// largest of their alignments). An allocation that doesn't fit gets a dedicated block for this frame only, destroyed
    /// once the frame completes, as for <see cref="GraphicsDevice.WriteBuffer"/>: counted in
    /// <see cref="GraphicsStatistics.UploadOverflowBytes"/> once the allocation has succeeded, and logged at
    /// <see cref="Core.Logging.LogLevel.Warning"/> at most once per frame. Transient memory isn't a copy, so
    /// <see cref="GraphicsStatistics.UploadBytesLastFrame"/> doesn't count it.
    /// </para>
    /// <para>
    /// Owner thread. Allocates no managed memory once the slot's ring exists and the device has made as many alias handles
    /// in an earlier frame, except the exception; making the ring or a dedicated block allocates.
    /// </para>
    /// </remarks>
    public Result AllocateTransient(long byteCount, TransientUsage usage, out TransientAllocation allocation) =>
        DeviceFor(nameof(AllocateTransient)).AllocateTransient(this, byteCount, usage, out allocation);

    /// <summary>Records a copy of a byte range from one buffer to another, or within one.</summary>
    /// <param name="copy">The buffers, the offsets and the size; checked by the copy rules of docs/design/graphics.md section 6.</param>
    /// <exception cref="InvalidOperationException">
    /// The list isn't its device's open frame: <c>default</c>, or a copy from a submitted frame.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A buffer is the null handle, or the copy breaks a copy rule (<see cref="ArgumentOutOfRangeException"/> for a range);
    /// nothing is recorded.
    /// </exception>
    /// <exception cref="StaleHandleException">A buffer isn't a live buffer of this device; nothing is recorded.</exception>
    /// <exception cref="OutOfMemoryException">The frame's command stream couldn't grow; nothing is recorded.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the device's owner thread.</exception>
    /// <remarks>
    /// The copy runs in the frame after its writes (<see cref="GraphicsDevice.WriteBuffer"/>), in call order with the other
    /// recorded commands. A transient allocation's buffer can be the source. On a lost device the copy is checked, then
    /// nothing is recorded. Owner thread. Allocates nothing, except the exception and the stream's growth.
    /// </remarks>
    public void CopyBufferToBuffer(in BufferCopy copy) => DeviceFor(nameof(CopyBufferToBuffer)).CopyBufferToBuffer(this, copy);

    /// <summary>Records a copy from one region of one texture subresource to rows in a buffer.</summary>
    /// <param name="copy">The texture location, the region, the buffer and its rows; checked as for <see cref="CopyBufferToBuffer"/>.</param>
    /// <exception cref="InvalidOperationException">As for <see cref="CopyBufferToBuffer"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The texture or buffer is the null handle, or the copy breaks a copy rule (<see cref="ArgumentOutOfRangeException"/>
    /// for a range); nothing is recorded.
    /// </exception>
    /// <exception cref="StaleHandleException">The texture or buffer isn't live on this device; nothing is recorded.</exception>
    /// <exception cref="OutOfMemoryException">The frame's command stream couldn't grow; nothing is recorded.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the device's owner thread.</exception>
    /// <remarks>Ordering, lost-device behaviour and allocation as for <see cref="CopyBufferToBuffer"/>.</remarks>
    public void CopyTextureToBuffer(in TextureBufferCopy copy) => DeviceFor(nameof(CopyTextureToBuffer)).CopyTextureToBuffer(this, copy);

    /// <summary>Records a copy from rows in a buffer to one region of one texture subresource.</summary>
    /// <param name="copy">The buffer and its rows, the texture location and the region; checked as for <see cref="CopyBufferToBuffer"/>.</param>
    /// <exception cref="InvalidOperationException">As for <see cref="CopyBufferToBuffer"/>.</exception>
    /// <exception cref="ArgumentException">As for <see cref="CopyTextureToBuffer"/>; depth is never copied from a buffer.</exception>
    /// <exception cref="StaleHandleException">The texture or buffer isn't live on this device; nothing is recorded.</exception>
    /// <exception cref="OutOfMemoryException">The frame's command stream couldn't grow; nothing is recorded.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the device's owner thread.</exception>
    /// <remarks>
    /// Ordering, lost-device behaviour and allocation as for <see cref="CopyBufferToBuffer"/>; a transient allocation's
    /// buffer can be the source.
    /// </remarks>
    public void CopyBufferToTexture(in TextureBufferCopy copy) => DeviceFor(nameof(CopyBufferToTexture)).CopyBufferToTexture(this, copy);

    /// <summary>Records a copy of a region from one texture subresource to another, of the same format and size.</summary>
    /// <param name="copy">Both locations, the source region and where it lands; checked as for <see cref="CopyBufferToBuffer"/>.</param>
    /// <exception cref="InvalidOperationException">As for <see cref="CopyBufferToBuffer"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A texture is the null handle, or the copy breaks a copy rule (<see cref="ArgumentOutOfRangeException"/> for a
    /// range); nothing is recorded.
    /// </exception>
    /// <exception cref="StaleHandleException">A texture isn't a live texture of this device; nothing is recorded.</exception>
    /// <exception cref="OutOfMemoryException">The frame's command stream couldn't grow; nothing is recorded.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the device's owner thread.</exception>
    /// <remarks>No scaling. Ordering, lost-device behaviour and allocation as for <see cref="CopyBufferToBuffer"/>.</remarks>
    public void CopyTextureToTexture(in TextureCopy copy) => DeviceFor(nameof(CopyTextureToTexture)).CopyTextureToTexture(this, copy);

    /// <summary>Acquires the next image of a swapchain for this frame, recreating the swapchain first when it needs it.</summary>
    /// <param name="swapchain">A live swapchain of the list's device, not yet acquired in this frame.</param>
    /// <param name="image">
    /// The acquired image (its texture, view, size and format), valid in this frame only, when the result is
    /// <see cref="AcquireStatus.Acquired"/> or <see cref="AcquireStatus.Recreated"/>; <c>default</c> otherwise.
    /// </param>
    /// <returns>
    /// What happened (docs/design/graphics.md section 3): <see cref="AcquireStatus.Acquired"/>;
    /// <see cref="AcquireStatus.Recreated"/> after the swapchain was recreated (a real size change, a suboptimal or out-of-date
    /// acquire or present, or an earlier failed recreation); <see cref="AcquireStatus.Minimised"/> for a minimised window or
    /// a zero side, without recreating; <see cref="AcquireStatus.Skipped"/> when it is still out of date after one
    /// recreation, or a recreation failed for another reason (logged at <see cref="Core.Logging.LogLevel.Error"/>);
    /// <see cref="AcquireStatus.SurfaceLost"/> once the surface is gone, from then on; or
    /// <see cref="AcquireStatus.DeviceLost"/>. Only the first two come with an image.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The list isn't its device's open frame (<c>default</c>, or a copy from a submitted frame), or the swapchain already
    /// has an image acquired in this frame.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="swapchain"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="swapchain"/> isn't a live swapchain of this device.</exception>
    /// <exception cref="OutOfMemoryException">The device's handle tables couldn't grow while recreating; the swapchain is left without images, so the next acquire creates it again.</exception>
    /// <exception cref="ObjectDisposedException">The device or the window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the device's owner thread.</exception>
    /// <remarks>
    /// <para>
    /// The window's size and state are read first (snapshots, no native call). The swapchain is recreated only when its
    /// window's pixel size really differs from the size it was created for, when an acquire or present marked it, or when an
    /// earlier recreation failed (then it is created again with no old swapchain). Recreation waits for the GPU to go idle,
    /// creates the new swapchain from the old one, and destroys the old one whatever the result. An out-of-date acquire
    /// recreates and retries once; an acquire timeout reports out of date, so a GPU that is really hung turns into a loss
    /// at the recreation's bounded wait.
    /// </para>
    /// <para>
    /// The device presents an acquired image when the frame is submitted, and clears it to black first if no recorded copy
    /// wrote it. Owner thread. Allocates nothing on the steady path (no recreation, no loss), except the exception; a
    /// recreation allocates.
    /// </para>
    /// </remarks>
    public AcquireStatus AcquireSwapchainImage(SwapchainHandle swapchain, out SwapchainImage image) =>
        DeviceFor(nameof(AcquireSwapchainImage)).AcquireSwapchainImage(this, swapchain, out image);

    // The list's device, for a member that needs its open frame; the device checks that the frame is open.
    private GraphicsDevice DeviceFor(string member) => Device ?? throw new InvalidOperationException(
        $"CommandList.{member}: the command list is default and belongs to no frame. Use the one BeginFrame returned.");
}
