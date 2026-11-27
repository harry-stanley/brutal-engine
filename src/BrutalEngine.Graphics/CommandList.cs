using System;

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
}
