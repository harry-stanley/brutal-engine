using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Swapchains;

/// <summary>
/// What a surface reported, as plain values: its capabilities (<c>VkSurfaceCapabilitiesKHR</c>), its formats and its
/// present modes. The swapchain component fills it from the surface queries; tests build it by hand.
/// </summary>
/// <remarks>
/// Holds the arrays without copying them; the planner only reads them. Immutable apart from the arrays' contents.
/// </remarks>
internal readonly struct SurfaceFacts
{
    /// <summary>
    /// Gets the surface's <c>currentExtent</c>, or <see cref="SwapchainPlanner.UndefinedExtent"/> when the swapchain
    /// decides the size (headless and Wayland surfaces).
    /// </summary>
    public VkExtent2D CurrentExtent { get; init; }

    /// <summary>Gets the surface's <c>minImageExtent</c>.</summary>
    public VkExtent2D MinExtent { get; init; }

    /// <summary>Gets the surface's <c>maxImageExtent</c>.</summary>
    public VkExtent2D MaxExtent { get; init; }

    /// <summary>Gets the surface's <c>minImageCount</c>.</summary>
    public int MinImageCount { get; init; }

    /// <summary>Gets the surface's <c>maxImageCount</c>; 0 means no maximum.</summary>
    public int MaxImageCount { get; init; }

    /// <summary>Gets the surface's <c>supportedCompositeAlpha</c>.</summary>
    public VkCompositeAlphaFlagsKHR SupportedCompositeAlpha { get; init; }

    /// <summary>Gets the surface's <c>supportedUsageFlags</c>.</summary>
    public VkImageUsageFlags SupportedUsage { get; init; }

    /// <summary>Gets the surface formats, in the order the driver reported them; <see langword="null"/> reads as none.</summary>
    public VkSurfaceFormatKHR[]? Formats { get; init; }

    /// <summary>Gets the present modes the surface offers; <see langword="null"/> reads as none.</summary>
    public VkPresentModeKHR[]? PresentModes { get; init; }
}

/// <summary>
/// The pure swapchain decisions of docs/design/graphics-vulkan.md section 4: extent, image count, composite alpha,
/// usage, format and present mode, from a surface's facts and the requested options.
/// </summary>
/// <remarks>Stateless and thread-safe. Creation-time only.</remarks>
internal static class SwapchainPlanner
{
    /// <summary>The <c>currentExtent</c> value (<c>0xFFFFFFFF</c> in both fields) that leaves the size to the swapchain.</summary>
    public static VkExtent2D UndefinedExtent => new(-1, -1);

    /// <summary>Plans a swapchain.</summary>
    /// <param name="facts">What the surface reported.</param>
    /// <param name="sourcePixelSize">
    /// The window's size in pixels (<c>WindowSurfaceSource.PixelWidth</c> and <c>PixelHeight</c>), used only when the
    /// current extent is undefined.
    /// </param>
    /// <param name="options">The requested present mode and format kind.</param>
    /// <returns>
    /// The plan; <see cref="ErrorKind.Cancelled"/> ("surface extent is zero") when the extent is zero, which the front
    /// end reports as a minimised window; or <see cref="ErrorKind.Unsupported"/> when the surface offers no
    /// colour-attachment usage, no composite alpha mode, or none of the four formats. Failure messages are constants.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The options' present mode or format kind isn't a member of its enum (the front end validates both).
    /// </exception>
    public static Result<SwapchainPlan> Plan(in SurfaceFacts facts, VkExtent2D sourcePixelSize, SwapchainOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VkPresentModeKHR requestedMode = VkPresentModeOf(options.PresentMode);
        ReadOnlySpan<VkFormat> order = FormatOrderOf(options.Format);

        if (!TryChooseExtent(facts, sourcePixelSize, out int width, out int height))
        {
            return new EngineError(ErrorKind.Cancelled, "surface extent is zero");
        }

        if ((facts.SupportedUsage & VkImageUsageFlags.ColorAttachmentBit) == 0)
        {
            return new EngineError(ErrorKind.Unsupported, "surface doesn't support colour-attachment images");
        }

        if (facts.SupportedCompositeAlpha == VkCompositeAlphaFlagsKHR.None)
        {
            return new EngineError(ErrorKind.Unsupported, "surface reports no composite alpha mode");
        }

        if (!TryChooseFormat(facts.Formats, order, out VkFormat format))
        {
            return new EngineError(
                ErrorKind.Unsupported, "surface offers none of B8G8R8A8 or R8G8B8A8, SRGB or UNORM, with SRGB_NONLINEAR");
        }

        bool listed = Array.IndexOf(facts.PresentModes ?? [], requestedMode) >= 0;
        return new SwapchainPlan(
            width,
            height,
            ChooseImageCount(facts.MinImageCount, facts.MaxImageCount),
            LowestBit(facts.SupportedCompositeAlpha),
            VkImageUsageFlags.ColorAttachmentBit | (facts.SupportedUsage & VkImageUsageFlags.TransferDstBit),
            format,
            VkColorSpaceKHR.SRGBNonlinearKHR,
            TextureFormatOf(format),
            listed ? options.PresentMode : PresentMode.Fifo,
            listed ? requestedMode : VkPresentModeKHR.FifoKHR,
            PresentModeFellBack: !listed);
    }

    /// <summary>The source's size clamped to the limits when the current extent is undefined; false when it is zero.</summary>
    private static bool TryChooseExtent(in SurfaceFacts facts, VkExtent2D source, out int width, out int height)
    {
        VkExtent2D current = facts.CurrentExtent;
        if (current.Width != -1 || current.Height != -1)
        {
            (width, height) = (current.Width, current.Height);
        }
        else if (source.Width <= 0 || source.Height <= 0)
        {
            // Checked before clamping: clamping to minImageExtent would turn a minimised window into a 1x1 swapchain.
            (width, height) = (0, 0);
        }
        else
        {
            width = Math.Min(Math.Max(source.Width, facts.MinExtent.Width), facts.MaxExtent.Width);
            height = Math.Min(Math.Max(source.Height, facts.MinExtent.Height), facts.MaxExtent.Height);
        }

        return width > 0 && height > 0;
    }

    /// <summary>One more than the minimum, so acquiring never waits for the only free image; within a non-zero maximum.</summary>
    private static int ChooseImageCount(int min, int max) => max > 0 ? Math.Min(min + 1, max) : min + 1;

    /// <summary>The lowest set bit: opaque is bit 0, so it wins whenever it is supported.</summary>
    private static VkCompositeAlphaFlagsKHR LowestBit(VkCompositeAlphaFlagsKHR supported)
    {
        uint bits = (uint)supported;
        return (VkCompositeAlphaFlagsKHR)(bits & (0u - bits));
    }

    /// <summary>The first format of <paramref name="order"/> the surface offers with <c>SRGB_NONLINEAR</c>.</summary>
    private static bool TryChooseFormat(VkSurfaceFormatKHR[]? offered, ReadOnlySpan<VkFormat> order, out VkFormat format)
    {
        foreach (VkFormat candidate in order)
        {
            foreach (VkSurfaceFormatKHR surfaceFormat in offered ?? [])
            {
                if (surfaceFormat.Format == candidate && surfaceFormat.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
                {
                    format = candidate;
                    return true;
                }
            }
        }

        format = VkFormat.Undefined;
        return false;
    }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> isn't a <see cref="SwapchainFormat"/> member.</exception>
    private static ReadOnlySpan<VkFormat> FormatOrderOf(SwapchainFormat kind) => kind switch
    {
        SwapchainFormat.Srgb => [VkFormat.B8G8R8A8SRGB, VkFormat.R8G8B8A8SRGB, VkFormat.B8G8R8A8UNorm, VkFormat.R8G8B8A8UNorm],
        SwapchainFormat.Unorm => [VkFormat.B8G8R8A8UNorm, VkFormat.R8G8B8A8UNorm, VkFormat.B8G8R8A8SRGB, VkFormat.R8G8B8A8SRGB],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), (int)kind, "Not a SwapchainFormat member."),
    };

    /// <summary>The four-row table between the swapchain's Vulkan formats and the engine's.</summary>
    private static TextureFormat TextureFormatOf(VkFormat format) => format switch
    {
        VkFormat.B8G8R8A8SRGB => TextureFormat.Bgra8UnormSrgb,
        VkFormat.R8G8B8A8SRGB => TextureFormat.Rgba8UnormSrgb,
        VkFormat.B8G8R8A8UNorm => TextureFormat.Bgra8Unorm,
        _ => TextureFormat.Rgba8Unorm, // R8G8B8A8_UNORM: TryChooseFormat returns only the four
    };

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> isn't a <see cref="PresentMode"/> member.</exception>
    private static VkPresentModeKHR VkPresentModeOf(PresentMode mode) => mode switch
    {
        PresentMode.Fifo => VkPresentModeKHR.FifoKHR,
        PresentMode.FifoRelaxed => VkPresentModeKHR.FifoRelaxedKHR,
        PresentMode.Mailbox => VkPresentModeKHR.MailboxKHR,
        PresentMode.Immediate => VkPresentModeKHR.ImmediateKHR,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), (int)mode, "Not a PresentMode member."),
    };
}
