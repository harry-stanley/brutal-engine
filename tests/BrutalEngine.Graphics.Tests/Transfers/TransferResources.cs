using System;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Tests.Transfers;

/// <summary>
/// The buffers and textures the copy tests record between, one per role, and one valid copy of each kind. Each role has
/// its own size and debug name, so a check made against the wrong resource throws, and each copy's fields have distinct
/// values, so a swapped field shows in the encoding.
/// </summary>
internal sealed record TransferResources(
    BufferHandle Source,
    BufferHandle Destination,
    BufferHandle Readback,
    TextureHandle Color,
    TextureHandle Target,
    TextureHandle Depth)
{
    /// <summary>The copy kinds a <see cref="CommandList"/> records, in the order of <see cref="CommandKind"/>.</summary>
    public static readonly CommandKind[] Kinds =
        [CommandKind.CopyBufferToBuffer, CommandKind.CopyBufferToTexture, CommandKind.CopyTextureToBuffer, CommandKind.CopyTextureToTexture];

    /// <summary>The readback buffer's size in bytes.</summary>
    public const long ReadbackSize = 512;

    /// <summary>A copy into <see cref="Destination"/> at an offset past <see cref="Source"/>'s end.</summary>
    public BufferCopy BufferToBuffer => new() { Source = Source, SourceOffset = 8, Destination = Destination, DestinationOffset = 4100, Size = 64 };

    /// <summary>A copy from mip 1, layer 1 of <see cref="Target"/>'s rows in <see cref="Source"/>.</summary>
    public TextureBufferCopy BufferToTexture => new()
    {
        Texture = new TextureLocation(Target, MipLevel: 1, ArrayLayer: 1),
        Region = new TextureRegion(5, 6, 7, 8),
        Buffer = Source,
        BufferOffset = 1024,
        BytesPerRow = 256,
    };

    /// <summary>A copy of mip 1, layer 2 of the depth texture's depth aspect into <see cref="Destination"/>.</summary>
    public TextureBufferCopy TextureToBuffer => new()
    {
        Texture = new TextureLocation(Depth, MipLevel: 1, ArrayLayer: 2, TextureAspect.Depth),
        Region = new TextureRegion(1, 2, 3, 4),
        Buffer = Destination,
        BufferOffset = 4608,
        BytesPerRow = 512,
    };

    /// <summary>A copy from layer 1 of <see cref="Color"/> to mip 1 of <see cref="Target"/>.</summary>
    public TextureCopy TextureToTexture => new()
    {
        Source = new TextureLocation(Color, MipLevel: 0, ArrayLayer: 1),
        SourceRegion = new TextureRegion(9, 10, 11, 12),
        Destination = new TextureLocation(Target, MipLevel: 1, ArrayLayer: 0),
        DestinationX = 13,
        DestinationY = 14,
    };

    /// <summary>Creates every resource on a device.</summary>
    public static TransferResources Create(GraphicsDevice device) => new(
        device.CreateBuffer(new BufferDescription { Size = 4096, Usage = BufferUsage.CopySource, DebugName = "source" }).Value,
        device.CreateBuffer(new BufferDescription { Size = 8192, Usage = BufferUsage.CopyDestination, DebugName = "destination" }).Value,
        device.CreateBuffer(new BufferDescription
        {
            Size = ReadbackSize,
            Usage = BufferUsage.CopyDestination,
            Memory = MemoryIntent.Readback,
            DebugName = "readback",
        }).Value,
        device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Rgba8Unorm,
            Width = 64,
            Height = 32,
            ArrayLayers = 2,
            Usage = TextureUsage.CopySource,
            DebugName = "color",
        }).Value,
        device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Rgba8Unorm,
            Width = 128,
            Height = 64,
            ArrayLayers = 2,
            MipLevels = 2,
            Usage = TextureUsage.CopyDestination,
            DebugName = "target",
        }).Value,
        device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Depth32Float,
            Width = 32,
            Height = 16,
            ArrayLayers = 3,
            MipLevels = 2,
            Usage = TextureUsage.CopySource,
            DebugName = "depth",
        }).Value);

    /// <summary>Records this set's copy of one kind.</summary>
    public void Record(CommandList commands, CommandKind kind)
    {
        switch (kind)
        {
            case CommandKind.CopyBufferToBuffer:
                commands.CopyBufferToBuffer(BufferToBuffer);
                break;
            case CommandKind.CopyBufferToTexture:
                commands.CopyBufferToTexture(BufferToTexture);
                break;
            case CommandKind.CopyTextureToBuffer:
                commands.CopyTextureToBuffer(TextureToBuffer);
                break;
            case CommandKind.CopyTextureToTexture:
                commands.CopyTextureToTexture(TextureToTexture);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a copy kind.");
        }
    }
}
