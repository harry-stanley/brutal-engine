using System;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class MessageBoxRequestTests
{
    [Fact]
    public void Parent_DefaultsToNull()
    {
        MessageBoxRequest request = new(MessageBoxKind.Warning, "Title", "Message");

        Assert.Null(request.Parent);
        Assert.Equal(MessageBoxKind.Warning, request.Kind);
        Assert.Equal("Title", request.Title);
        Assert.Equal("Message", request.Message);
    }

    [Fact]
    public void Equality_ComparesEveryProperty()
    {
        FakeWindow window = new();
        MessageBoxRequest request = new(MessageBoxKind.Error, "Title", "Message") { Parent = window };
        MessageBoxRequest same = new(MessageBoxKind.Error, new string("Title".AsSpan()), "Message") { Parent = window };

        Assert.Same(window, request.Parent);
        Assert.Equal(request, same);
        Assert.Equal(request.GetHashCode(), same.GetHashCode());

        Assert.NotEqual(request, request with { Kind = MessageBoxKind.Warning });
        Assert.NotEqual(request, request with { Title = "Other" });
        Assert.NotEqual(request, request with { Message = "Other" });
        Assert.NotEqual(request, request with { Parent = null });
        Assert.NotEqual(request, request with { Parent = new FakeWindow() });
    }

    /// <summary>A window with no behaviour, only an identity, for the <see cref="MessageBoxRequest.Parent"/> tests.</summary>
    private sealed class FakeWindow : IWindow
    {
        public WindowId Id => throw new NotSupportedException();
        public WindowSize Size => throw new NotSupportedException();
        public WindowSize PixelSize => throw new NotSupportedException();
        public WindowSize ContentSize => throw new NotSupportedException();
        public float PixelDensity => throw new NotSupportedException();
        public float DisplayScale => throw new NotSupportedException();
        public WindowState State => throw new NotSupportedException();
        public WindowMode Mode => throw new NotSupportedException();
        public DisplayId Display => throw new NotSupportedException();
        public bool IsTextInputActive => throw new NotSupportedException();
        public bool IsRelativeMouseMode => throw new NotSupportedException();
        public IVulkanSurfaceSource? VulkanSurface => throw new NotSupportedException();

        public Result SetTitle(string title) => throw new NotSupportedException();
        public Result SetContentSize(WindowSize contentSize) => throw new NotSupportedException();
        public Result SetMode(WindowMode mode) => throw new NotSupportedException();
        public Result SetExclusiveFullscreen(DisplayMode mode) => throw new NotSupportedException();
        public Result StartTextInput() => throw new NotSupportedException();
        public Result StopTextInput() => throw new NotSupportedException();
        public Result SetRelativeMouseMode(bool enabled) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
