using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Recording;
using BrutalEngine.Graphics.Validation;

namespace BrutalEngine.Graphics;

/// <content>Recorded copies (docs/design/graphics.md section 6).</content>
public sealed partial class GraphicsDevice
{
    private const string CopyBufferToBufferApi = nameof(CommandList) + "." + nameof(CommandList.CopyBufferToBuffer);
    private const string CopyTextureToBufferApi = nameof(CommandList) + "." + nameof(CommandList.CopyTextureToBuffer);
    private const string CopyBufferToTextureApi = nameof(CommandList) + "." + nameof(CommandList.CopyBufferToTexture);
    private const string CopyTextureToTextureApi = nameof(CommandList) + "." + nameof(CommandList.CopyTextureToTexture);

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
