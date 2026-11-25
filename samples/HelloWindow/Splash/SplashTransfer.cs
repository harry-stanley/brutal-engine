namespace HelloWindow.Splash;

/// <summary>How the logo image reaches the swapchain image.</summary>
internal enum SplashTransfer
{
    /// <summary>Neither way works on this surface: a setup failure. The splash is never skipped instead.</summary>
    Unsupported = 0,

    /// <summary><c>vkCmdBlitImage</c> with linear filtering, which can scale the logo down.</summary>
    Blit,

    /// <summary><c>vkCmdCopyImage</c> at 1:1, only when the logo fits unscaled.</summary>
    Copy,
}
