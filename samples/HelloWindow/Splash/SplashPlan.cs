using Brutal.VulkanApi;

namespace HelloWindow.Splash;

/// <summary>The result of <see cref="SplashCapabilities.Choose"/>.</summary>
/// <param name="Transfer">How the logo reaches the swapchain image, or <see cref="SplashTransfer.Unsupported"/>.</param>
/// <param name="LogoFormat">The format to upload the logo in (the swapchain's), or undefined if unsupported.</param>
/// <param name="PixelOrder">The byte order of <paramref name="LogoFormat"/>, for <see cref="LogoCompositor"/>.</param>
/// <param name="FailureMessage">Why neither transfer works, or null if one does.</param>
internal readonly record struct SplashPlan(SplashTransfer Transfer, VkFormat LogoFormat, LogoPixelOrder PixelOrder, string? FailureMessage)
{
    /// <summary>Gets a value indicating whether the splash can be shown. If not, setup fails with
    /// <see cref="FailureMessage"/>.</summary>
    public bool IsSupported => Transfer != SplashTransfer.Unsupported;
}
