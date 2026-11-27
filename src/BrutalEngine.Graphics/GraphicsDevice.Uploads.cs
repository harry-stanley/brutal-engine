using System;
using System.Diagnostics.CodeAnalysis;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Recording;
using BrutalEngine.Graphics.Validation;

namespace BrutalEngine.Graphics;

/// <content>Buffer and texture writes through the upload ring and pending upload blocks (docs/design/graphics.md section 6).</content>
public sealed partial class GraphicsDevice
{
    private const string WriteBufferApi = nameof(GraphicsDevice) + "." + nameof(WriteBuffer);
    private const string WriteTextureApi = nameof(GraphicsDevice) + "." + nameof(WriteTexture);

    // Every staging block can be bound as any transient usage: the ring and overflow blocks serve transient allocations
    // as well as writes, and pending blocks share their creation path.
    private const BufferUsage StagingUsage = BufferUsage.CopySource | BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Uniform
        | BufferUsage.Storage | BufferUsage.Indirect;

    private readonly PendingUploadPool _pendingUploads = new();
    private readonly long _ringBytes;
    private readonly INativeAllocator _allocator;
    private DeletionQueue? _deferred;
    private long _uploadBytes;
    private long _uploadBytesLastFrame;
    private long _uploadOverflowBytes;
    private bool _overflowLogged;

    /// <summary>Writes bytes into a buffer at the start of the next submitted frame.</summary>
    /// <param name="buffer">A live buffer of this device with <see cref="BufferUsage.CopyDestination"/>.</param>
    /// <param name="offset">The byte offset in the buffer the data goes to; any alignment.</param>
    /// <param name="data">The bytes, copied before the call returns: the span can be reused at once.</param>
    /// <returns>
    /// Success once the bytes are staged. Fails with <see cref="ErrorKind.Unavailable"/> on a lost device, or with the
    /// backend's failure (<see cref="ErrorKind.OutOfMemory"/>) when a staging block can't be created; then nothing is
    /// written.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is the null handle or lacks <see cref="BufferUsage.CopyDestination"/>.
    /// </exception>
    /// <exception cref="StaleHandleException"><paramref name="buffer"/> isn't a live buffer of this device.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative or past the buffer's end, or <paramref name="data"/> is empty or reaches past
    /// it.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The device's unmanaged memory couldn't grow; the copy wasn't recorded.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// <para>
    /// The copy runs at the start of the next submitted frame (the open one, inside a frame), before its recorded
    /// commands, in call order with the other writes. Inside a frame the bytes go into the open slot's upload ring,
    /// between frames into a pending upload block, which is reused once the frame that copied it has completed. A write
    /// that doesn't fit gets a dedicated block for that frame only: counted in
    /// <see cref="GraphicsStatistics.UploadOverflowBytes"/>, and logged at <see cref="Core.Logging.LogLevel.Warning"/> at
    /// most once per frame. <see cref="GraphicsStatistics.UploadBytesLastFrame"/> counts the bytes of both paths.
    /// </para>
    /// <para>
    /// Owner thread. Allocates nothing once the frame slot's ring exists, except the exception; making a ring, pending or
    /// overflow block allocates.
    /// </para>
    /// </remarks>
    public Result WriteBuffer(BufferHandle buffer, long offset, ReadOnlySpan<byte> data)
    {
        ThrowIfUnusable(WriteBufferApi);
        _handles.ThrowIfNotLive(BackendObjectKind.Buffer, buffer.Value, nameof(buffer));
        CopyValidator.ValidateWriteBuffer(_handles.GetBuffer(buffer.Value), offset, data.Length);
        if (!TryStage(data.Length, UploadRing.Alignment, WriteBufferApi, out UploadRing? block, out long staged, out bool overflow, out EngineError error))
        {
            return error;
        }

        data.CopyTo(block.Bytes(staged, data.Length));
        CurrentUploads.Append(CommandKind.CopyBufferToBuffer, new CopyBufferToBufferCommand
        {
            Source = block.Id,
            SourceOffset = staged,
            Destination = _handles.GetId(BackendObjectKind.Buffer, buffer.Value, nameof(buffer)),
            DestinationOffset = offset,
            Size = data.Length,
        });
        if (overflow)
        {
            CountOverflow(data.Length, WriteBufferApi);
        }

        _uploadBytes += data.Length;
        return Result.Success;
    }

    /// <summary>Writes texels into one mip level and layer of a color texture at the start of the next submitted frame.</summary>
    /// <param name="write">The texture location, the region and the row pitch of <paramref name="data"/>.</param>
    /// <param name="data">
    /// The rows, <see cref="TextureWrite.BytesPerRow"/> apart (0: tightly packed), copied before the call returns.
    /// </param>
    /// <returns>As for <see cref="WriteBuffer"/>.</returns>
    /// <exception cref="ArgumentException">
    /// The texture is the null handle, lacks <see cref="TextureUsage.CopyDestination"/>, has a depth format or more than
    /// one sample, or the aspect isn't <see cref="TextureAspect.Color"/>.
    /// </exception>
    /// <exception cref="StaleHandleException">The texture isn't a live texture of this device.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The mip level, layer or region lies outside the texture, the row pitch is too small, or <paramref name="data"/> is
    /// shorter than <c>BytesPerRow * (Region.Height - 1) + Region.Width * texel size</c> bytes.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The device's unmanaged memory couldn't grow; the copy wasn't recorded.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The rows are repacked to a multiple of <see cref="TextureBufferCopy.RowPitchAlignment"/> as they are staged; the
    /// staging, ordering and overflow rules are those of <see cref="WriteBuffer"/>.
    /// <see cref="GraphicsStatistics.UploadBytesLastFrame"/> counts the region's texels, and
    /// <see cref="GraphicsStatistics.UploadOverflowBytes"/> the staged bytes (the repacked rows, padding between them
    /// included). Owner thread. Allocation as for <see cref="WriteBuffer"/>.
    /// </remarks>
    public Result WriteTexture(in TextureWrite write, ReadOnlySpan<byte> data)
    {
        ThrowIfUnusable(WriteTextureApi);
        Handle texture = write.Target.Texture.Value;
        _handles.ThrowIfNotLive(BackendObjectKind.Texture, texture, nameof(write));
        ref readonly TextureDescription description = ref _handles.GetTexture(texture);
        CopyValidator.ValidateWriteTexture(write, description, data.Length);
        int rowBytes = write.Region.Width * TextureFormatInfo.TexelSize(description.Format);
        int sourcePitch = write.BytesPerRow == 0 ? rowBytes : write.BytesPerRow;
        int pitch = (rowBytes + TextureBufferCopy.RowPitchAlignment - 1) & ~(TextureBufferCopy.RowPitchAlignment - 1);
        long size = ((long)pitch * (write.Region.Height - 1)) + rowBytes;
        if (!TryStage(size, UploadRing.Alignment, WriteTextureApi, out UploadRing? block, out long staged, out bool overflow, out EngineError error))
        {
            return error;
        }

        for (int row = 0; row < write.Region.Height; row++)
        {
            data.Slice(row * sourcePitch, rowBytes).CopyTo(block.Bytes(staged + ((long)row * pitch), rowBytes));
        }

        CurrentUploads.Append(CommandKind.CopyBufferToTexture, new CopyBufferTextureCommand
        {
            Buffer = block.Id,
            BufferOffset = staged,
            BytesPerRow = pitch,
            Texture = _handles.GetId(BackendObjectKind.Texture, texture, nameof(write)),
            MipLevel = write.Target.MipLevel,
            ArrayLayer = write.Target.ArrayLayer,
            Aspect = TextureAspect.Color,
            Region = write.Region,
        });
        if (overflow)
        {
            CountOverflow(size, WriteTextureApi);
        }

        _uploadBytes += (long)rowBytes * write.Region.Height;
        return Result.Success;
    }

    // The copies the next submitted frame lands: the open frame's slot, or the next frame's between frames.
    private CommandWriter CurrentUploads => _slots[CurrentSlot()].Uploads;

    // Finds room for a write or transient allocation: the open slot's ring at the alignment given, else (between frames,
    // writes only) the pending blocks, else a dedicated overflow block that the frame using it destroys once done
    // (overflow; the caller counts it once the copy or alias is recorded). Fails, staging nothing, on a lost device or
    // when a block can't be created.
    private bool TryStage(
        long size,
        long alignment,
        string api,
        [NotNullWhen(true)] out UploadRing? block,
        out long offset,
        out bool overflow,
        out EngineError error)
    {
        (block, offset, overflow, error) = (null, -1, false, default);
        if (IsLostCore)
        {
            error = new EngineError(ErrorKind.Unavailable, $"{api}: the device is lost.");
            return false;
        }

        FrameSlot slot = _slots[CurrentSlot()];
        if (_frameOpen)
        {
            if (slot.Ring is null && !TryCreateBlock(_ringBytes, out block, out error))
            {
                return false;
            }

            block = slot.Ring ??= block;
            offset = block!.TryReserve(size, alignment);
        }
        else
        {
            block = _pendingUploads.TryReserve(size, out offset);
            if (block is null && size <= _ringBytes)
            {
                if (!TryCreateBlock(_ringBytes, out block, out error))
                {
                    return false;
                }

                _pendingUploads.Add(block);
                offset = block.TryReserve(size);
            }
        }

        if (offset >= 0 && block is not null)
        {
            return true;
        }

        // Overflow: the queue gets room before the block exists, so a block is never left unqueued.
        DeletionQueue queue = _frameOpen ? ReserveDeletion()! : DeferredDeletions();
        if (!TryCreateBlock(size, out block, out error))
        {
            return false;
        }

        queue.Add(BackendObjectKind.Buffer, block.Id);
        overflow = true;
        offset = block.TryReserve(size);
        return true;
    }

    private bool TryCreateBlock(long size, [NotNullWhen(true)] out UploadRing? block, out EngineError error)
    {
        var description = new BufferDescription { Size = size, Usage = StagingUsage, Memory = MemoryIntent.Upload };
        Result<BackendBuffer> created = _backend.CreateBuffer(description, BackendMemory.HostUpload);
        block = created.TryGetValue(out BackendBuffer buffer) ? new UploadRing(buffer, size) : null;
        error = block is null ? created.Error : default;
        return block is not null;
    }

    private void CountOverflow(long size, string api)
    {
        _uploadOverflowBytes += size;
        if (!_overflowLogged)
        {
            _overflowLogged = true;
            _log.Warning($"{api}: {size} bytes didn't fit the {_ringBytes}-byte upload ring; frame {_frameNumber} uses a dedicated block. Raise GraphicsDeviceOptions.UploadRingBytesPerFrame if this repeats.");
        }
    }

    // The queue for objects pending copies may still use: handed to the next frame's slot when it begins.
    private DeletionQueue DeferredDeletions()
    {
        _deferred ??= new DeletionQueue(_allocator);
        _deferred.Reserve();
        return _deferred;
    }

    // At BeginFrame, once the slot's previous frame completed: its pending blocks come back, the pending ones and the
    // deferred deletions go to the new frame, and its ring slice is free again.
    private void BeginUploads(FrameSlot slot)
    {
        if (_deferred is not null)
        {
            (slot.Deletions, _deferred) = (_deferred, slot.Deletions);
        }

        _pendingUploads.Return(slot.PendingBlocks);
        _pendingUploads.HandOver(slot.PendingBlocks);
        slot.Ring?.Reset();
    }

    // At SubmitFrame, after Execute: the copies are submitted, the frame's transient aliases go stale, and the counters
    // move on to the next frame.
    private void EndUploads(FrameSlot slot)
    {
        slot.Uploads.Reset();
        EndTransients();
        _uploadBytesLastFrame = _uploadBytes;
        _uploadBytes = 0;
        _overflowLogged = false;
    }

    // At Dispose, after the GPU went idle: every staging buffer and deferred object goes to the backend.
    private void DestroyUploads()
    {
        _deferred?.Retire(_backend);
        foreach (FrameSlot slot in _slots)
        {
            if (slot.Ring is { } ring)
            {
                _backend.Destroy(BackendObjectKind.Buffer, ring.Id);
            }

            PendingUploadPool.Destroy(_backend, slot.PendingBlocks);
        }

        _pendingUploads.Destroy(_backend);
    }
}
