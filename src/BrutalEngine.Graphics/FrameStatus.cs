namespace BrutalEngine.Graphics;

/// <summary>The outcome of beginning or submitting a frame.</summary>
/// <remarks>
/// Device loss is a value, not an exception (docs/design/graphics.md sections 4 and 12): after <see cref="DeviceLost"/>,
/// recording calls in the frame do nothing and the device recreates itself. The numeric values are fixed.
/// </remarks>
public enum FrameStatus
{
    /// <summary>The call succeeded: the frame is open (begin) or was submitted (submit).</summary>
    Ready = 0,

    /// <summary>The device was lost (docs/design/graphics.md section 12).</summary>
    DeviceLost = 1,
}
