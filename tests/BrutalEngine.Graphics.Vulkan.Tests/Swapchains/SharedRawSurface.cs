using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

/// <summary>
/// The swapchain test classes, which share one raw surface (<see cref="VulkanSwapchainTests.SurfaceFixture"/>) and run
/// one at a time. Run in parallel, two classes presenting to two windows on SDL's <c>wayland</c> driver crashed the test
/// host inside the NVIDIA driver (SIGSEGV), with or without present fences; one surface also means one instance fewer
/// on the NVIDIA host (#434).
/// </summary>
[CollectionDefinition(nameof(SharedRawSurface))]
public sealed class SharedRawSurface : ICollectionFixture<VulkanSwapchainTests.SurfaceFixture>;
