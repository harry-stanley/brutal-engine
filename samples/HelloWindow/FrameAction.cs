namespace HelloWindow;

/// <summary>What the frame loop does after a swapchain acquire or present call.</summary>
internal enum FrameAction
{
    /// <summary>Unrecoverable: stop the run and report the result.</summary>
    Fatal = 0,

    /// <summary>Success: carry on (render the acquired image, or count the present).</summary>
    Render,

    /// <summary>SUBOPTIMAL: the call succeeded (the image is usable or was presented); recreate the swapchain after.</summary>
    RenderThenRecreate,

    /// <summary>OUT_OF_DATE: nothing was acquired or presented; recreate the swapchain before the next frame.</summary>
    Recreate,
}
