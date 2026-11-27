using System;
using System.Globalization;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Recording;
using BrutalEngine.Graphics.Validation;

namespace BrutalEngine.Graphics;

/// <content>Recorded copies and buffer readback (docs/design/graphics.md section 6).</content>
public sealed partial class GraphicsDevice
{
    private const string TryReadBufferApi = nameof(GraphicsDevice) + "." + nameof(TryReadBuffer);
    private const string CopyBufferToBufferApi = nameof(CommandList) + "." + nameof(CommandList.CopyBufferToBuffer);
    private const string CopyTextureToBufferApi = nameof(CommandList) + "." + nameof(CommandList.CopyTextureToBuffer);
    private const string CopyBufferToTextureApi = nameof(CommandList) + "." + nameof(CommandList.CopyBufferToTexture);
    private const string CopyTextureToTextureApi = nameof(CommandList) + "." + nameof(CommandList.CopyTextureToTexture);

    /// <summary>Copies bytes out of a <see cref="MemoryIntent.Readback"/> buffer once the frames that wrote it have completed.</summary>
    /// <param name="buffer">A live <see cref="MemoryIntent.Readback"/> buffer of this device.</param>
    /// <param name="offset">The byte offset in the buffer to read from; any alignment.</param>
    /// <param name="destination">Receives the bytes: one per byte of the span, from <paramref name="offset"/>.</param>
    /// <returns>
    /// <see langword="true"/> once the bytes are copied. <see langword="false"/>, with <paramref name="destination"/>
    /// unchanged, while <see cref="CompletedFrameNumber"/> is below the last frame that writes the buffer, or when the
    /// device is lost.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is the null handle or isn't a <see cref="MemoryIntent.Readback"/> buffer.
    /// </exception>
    /// <exception cref="StaleHandleException"><paramref name="buffer"/> isn't a live buffer of this device.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative or past the buffer's end, or <paramref name="destination"/> is empty or reaches
    /// past it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// <para>
    /// A buffer is written by a recorded copy into it (<see cref="CommandList.CopyBufferToBuffer"/>,
    /// <see cref="CommandList.CopyTextureToBuffer"/>) in that frame, and by <see cref="WriteBuffer"/> in the frame that
    /// lands it: the open one, or the next submitted one between frames. A buffer nothing has written yet reads
    /// <see langword="true"/>, with its memory as it is. Never waits and never calls the backend: the result changes only
    /// when <see cref="CompletedFrameNumber"/> advances (<see cref="BeginFrame"/>, <see cref="WaitIdle"/>).
    /// </para>
    /// <para>Owner thread. Allocates nothing, except the exception.</para>
    /// </remarks>
    public bool TryReadBuffer(BufferHandle buffer, long offset, Span<byte> destination)
    {
        ThrowIfUnusable(TryReadBufferApi);
        Handle handle = buffer.Value;
        _handles.ThrowIfNotLive(BackendObjectKind.Buffer, handle, nameof(buffer));
        ref readonly BufferDescription description = ref _handles.GetBuffer(handle);
        if (description.Memory != MemoryIntent.Readback)
        {
            throw new ArgumentException(
                $"{TryReadBufferApi}: buffer '{description.DebugName}' has {description.Memory} memory; only Readback buffers can be read.", nameof(buffer));
        }

        if (offset < 0 || offset >= description.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), string.Create(
                CultureInfo.InvariantCulture,
                $"{TryReadBufferApi}: offset must lie in buffer '{description.DebugName}' ({description.Size} bytes), but is {offset}."));
        }

        // The offset lies in the buffer, so Size - offset can't overflow.
        if (destination.IsEmpty || destination.Length > description.Size - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), string.Create(
                CultureInfo.InvariantCulture,
                $"{TryReadBufferApi}: destination.Length must be from 1 to the {description.Size - offset} bytes from offset {offset} to the end of buffer '{description.DebugName}', but is {destination.Length}."));
        }

        HandleTables.BufferMemory memory = _handles.GetMemory(handle);
        if (IsLostCore || _completedFrameNumber < memory.LastWritten)
        {
            return false;
        }

        unsafe
        {
            // Sound: a Readback buffer is HostReadback memory, mapped for its whole size until it is destroyed, and the
            // range lies inside it (checked above). The frames that wrote it have completed, so the GPU writes are visible
            // (the backend's WaitForFrame contract) and no frame in flight writes it.
            new ReadOnlySpan<byte>((byte*)memory.Mapped + offset, destination.Length).CopyTo(destination);
        }

        return true;
    }

    /// <summary>The device side of <see cref="CommandList.CopyBufferToBuffer"/>; the list's documentation is the contract.</summary>
    internal void CopyBufferToBuffer(CommandList commands, in BufferCopy copy)
    {
        CommandWriter? stream = CopyStream(commands, CopyBufferToBufferApi);
        Handle destination = copy.Destination.Value;
        ulong sourceId = _handles.GetId(BackendObjectKind.Buffer, copy.Source.Value, nameof(copy));
        ulong destinationId = _handles.GetId(BackendObjectKind.Buffer, destination, nameof(copy));
        CopyValidator.Validate(copy, _handles.GetBuffer(copy.Source.Value), _handles.GetBuffer(destination));
        if (stream is null)
        {
            return;
        }

        stream.Append(CommandKind.CopyBufferToBuffer, new CopyBufferToBufferCommand
        {
            Source = sourceId,
            SourceOffset = copy.SourceOffset,
            Destination = destinationId,
            DestinationOffset = copy.DestinationOffset,
            Size = copy.Size,
        });
        _handles.MarkWritten(destination, _frameNumber);
    }

    /// <summary>The device side of <see cref="CommandList.CopyTextureToBuffer"/>; the list's documentation is the contract.</summary>
    internal void CopyTextureToBuffer(CommandList commands, in TextureBufferCopy copy)
    {
        CommandWriter? stream = CopyStream(commands, CopyTextureToBufferApi);
        Handle texture = copy.Texture.Texture.Value;
        Handle buffer = copy.Buffer.Value;
        ulong textureId = _handles.GetId(BackendObjectKind.Texture, texture, nameof(copy));
        ulong bufferId = _handles.GetId(BackendObjectKind.Buffer, buffer, nameof(copy));
        CopyValidator.ValidateCopyToBuffer(copy, _handles.GetTexture(texture), _handles.GetBuffer(buffer));
        if (stream is null)
        {
            return;
        }

        stream.Append(CommandKind.CopyTextureToBuffer, BufferTextureCommand(copy, bufferId, textureId));
        _handles.MarkWritten(buffer, _frameNumber);
    }

    /// <summary>The device side of <see cref="CommandList.CopyBufferToTexture"/>; the list's documentation is the contract.</summary>
    internal void CopyBufferToTexture(CommandList commands, in TextureBufferCopy copy)
    {
        CommandWriter? stream = CopyStream(commands, CopyBufferToTextureApi);
        Handle texture = copy.Texture.Texture.Value;
        Handle buffer = copy.Buffer.Value;
        ulong textureId = _handles.GetId(BackendObjectKind.Texture, texture, nameof(copy));
        ulong bufferId = _handles.GetId(BackendObjectKind.Buffer, buffer, nameof(copy));
        CopyValidator.ValidateCopyToTexture(copy, _handles.GetBuffer(buffer), _handles.GetTexture(texture));
        stream?.Append(CommandKind.CopyBufferToTexture, BufferTextureCommand(copy, bufferId, textureId));
    }

    /// <summary>The device side of <see cref="CommandList.CopyTextureToTexture"/>; the list's documentation is the contract.</summary>
    internal void CopyTextureToTexture(CommandList commands, in TextureCopy copy)
    {
        CommandWriter? stream = CopyStream(commands, CopyTextureToTextureApi);
        Handle source = copy.Source.Texture.Value;
        Handle destination = copy.Destination.Texture.Value;
        ulong sourceId = _handles.GetId(BackendObjectKind.Texture, source, nameof(copy));
        ulong destinationId = _handles.GetId(BackendObjectKind.Texture, destination, nameof(copy));
        CopyValidator.Validate(copy, _handles.GetTexture(source), _handles.GetTexture(destination));
        stream?.Append(CommandKind.CopyTextureToTexture, new CopyTextureToTextureCommand
        {
            Source = sourceId,
            SourceMipLevel = copy.Source.MipLevel,
            SourceArrayLayer = copy.Source.ArrayLayer,
            SourceAspect = copy.Source.Aspect,
            Destination = destinationId,
            DestinationMipLevel = copy.Destination.MipLevel,
            DestinationArrayLayer = copy.Destination.ArrayLayer,
            DestinationAspect = copy.Destination.Aspect,
            SourceRegion = copy.SourceRegion,
            DestinationX = copy.DestinationX,
            DestinationY = copy.DestinationY,
        });
    }

    private static CopyBufferTextureCommand BufferTextureCommand(in TextureBufferCopy copy, ulong buffer, ulong texture) => new()
    {
        Buffer = buffer,
        BufferOffset = copy.BufferOffset,
        BytesPerRow = copy.BytesPerRow,
        Texture = texture,
        MipLevel = copy.Texture.MipLevel,
        ArrayLayer = copy.Texture.ArrayLayer,
        Aspect = copy.Texture.Aspect,
        Region = copy.Region,
    };

    // The open frame's command stream a copy records into, once the list is that frame's; null on a lost device, where
    // a copy is still checked but records nothing (section 12).
    private CommandWriter? CopyStream(CommandList commands, string api)
    {
        ThrowIfUnusable(api);
        ThrowIfNotOpenFrame(commands, api);
        return IsLostCore ? null : _slots[CurrentSlot()].Commands;
    }
}
