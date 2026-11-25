using Brutal.VulkanApi;

namespace HelloWindow.Splash;

/// <summary>What a splash frame draws: the logo image, how it reaches the swapchain image, and where.</summary>
/// <param name="Image">The uploaded logo (<see cref="SplashImage.Image"/>), in <c>TRANSFER_SRC_OPTIMAL</c>.</param>
/// <param name="Size">The logo's size in pixels.</param>
/// <param name="Transfer">A blit or a copy (never <see cref="SplashTransfer.Unsupported"/>).</param>
/// <param name="Destination">Where the logo goes in the swapchain image (<see cref="SplashLayout.Fit"/>). A copy needs
/// it to be the logo's size.</param>
internal readonly record struct SplashDraw(VkImage Image, PixelSize Size, SplashTransfer Transfer, PixelRect Destination);
