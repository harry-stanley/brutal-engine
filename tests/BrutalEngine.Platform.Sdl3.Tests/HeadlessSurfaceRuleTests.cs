using System;
using System.Text;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

public sealed class HeadlessSurfaceRuleTests
{
    // The extension lists SDL reports (docs/design/platform.md section 3, observed): offscreen on Windows, offscreen
    // on Linux, and a windowed Windows window.
    private const string WindowsOffscreen = "VK_KHR_surface";
    private const string LinuxOffscreen = "VK_KHR_surface VK_EXT_headless_surface";
    private const string Windowed = "VK_KHR_surface VK_KHR_win32_surface";

    [Theory]
    [InlineData("offscreen", WindowsOffscreen, true)]
    [InlineData("OffScreen", WindowsOffscreen, true)]
    [InlineData("offscreen", "", true)]
    [InlineData("offscreen", "VK_EXT_headless_surfac", true)]
    [InlineData("offscreen", "VK_EXT_headless_surfaces", true)]
    [InlineData("offscreen", "vk_ext_headless_surface", true)]
    [InlineData("offscreen", LinuxOffscreen, false)]
    [InlineData("offscreen", "VK_EXT_headless_surface", false)]
    [InlineData("windows", WindowsOffscreen, false)]
    [InlineData("windows", Windowed, false)]
    [InlineData("x11", "VK_KHR_surface VK_KHR_xlib_surface", false)]
    [InlineData("dummy", "", false)]
    [InlineData("offscreen2", WindowsOffscreen, false)]
    [InlineData("", WindowsOffscreen, false)]
    public void Table(string driver, string extensions, bool expected)
    {
        FakeSource source = new(extensions);

        Assert.Equal(expected, HeadlessSurfaceRule.RequiresHeadlessExtension(source, driver));
    }

    [Theory]
    [InlineData(LinuxOffscreen, "VK_KHR_surface", true)]
    [InlineData(LinuxOffscreen, "VK_EXT_headless_surface", true)]
    [InlineData(LinuxOffscreen, "VK_KHR", false)]
    [InlineData(LinuxOffscreen, "", false)]
    [InlineData("", "VK_KHR_surface", false)]
    public void ContainsExtension_ComparesWholeNames(string extensions, string name, bool expected)
    {
        Assert.Equal(expected, HeadlessSurfaceRule.ContainsExtension(new FakeSource(extensions), Encoding.ASCII.GetBytes(name)));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        FakeSource source = new(LinuxOffscreen);

        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() => HeadlessSurfaceRule.RequiresHeadlessExtension(null!, "offscreen")).ParamName);
        Assert.Equal("videoDriver", Assert.Throws<ArgumentNullException>(() => HeadlessSurfaceRule.RequiresHeadlessExtension(source, null!)).ParamName);
        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() => HeadlessSurfaceRule.ContainsExtension(null!, "VK_KHR_surface"u8)).ParamName);
    }

    [Fact]
    public void Message_IsTheNotesText()
    {
        Assert.Equal(
            "Headless Vulkan windows need VK_EXT_headless_surface, which no installed Vulkan driver offers. Windows drivers don't; run windowed.",
            HeadlessSurfaceRule.Message);
    }

    // The rule reads only the extension getters; surface creation is never called.
    private sealed class FakeSource(string extensions) : IVulkanSurfaceSource
    {
        private readonly byte[][] _names = Array.ConvertAll(
            extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries), Encoding.ASCII.GetBytes);

        public int RequiredInstanceExtensionCount => _names.Length;

        public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index) => _names[index];

        public Result<nint> CreateVulkanSurface(nint instance) => throw new NotSupportedException();
    }
}
