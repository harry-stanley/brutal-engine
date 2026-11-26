using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformInterfaceTests
{
    [Fact]
    public void TestDouble_ImplementsEveryMember()
    {
        // Through a generic constraint, so every call below goes through IPlatform, not the double's own members.
        Exercise(new TestPlatform());
    }

    [Fact]
    public void Interface_DeclaresTheNoteMembers()
    {
        // docs/design/platform.md section 1, "The platform root", written out by hand: every member (events and
        // nested types included), with its parameter names and the nullability of every reference type ('!' not
        // null, '?' nullable), as the PublicAPI files write them.
        string[] expected =
        [
            "System.String! BackendName { get; }",
            "System.String! VideoDriver { get; }",
            "BrutalEngine.Platform.HeadlessMode Headless { get; }",
            "System.TimeProvider! Clock { get; }",
            "System.String! BaseDirectory { get; }",
            "BrutalEngine.Core.Result<System.String!> GetUserDataDirectory()",
            "BrutalEngine.Core.Result<BrutalEngine.Platform.IWindow!> CreateWindow(BrutalEngine.Platform.WindowOptions! options)",
            "BrutalEngine.Platform.PlatformEventBatch PumpEvents()",
            "BrutalEngine.Platform.PlatformEventBatch Events { get; }",
            "System.Boolean WaitForEvents(System.TimeSpan timeout)",
            "System.Collections.Generic.IReadOnlyList<BrutalEngine.Platform.DisplayInfo!>! Displays { get; }",
            "BrutalEngine.Core.Result<System.Collections.Generic.IReadOnlyList<BrutalEngine.Platform.DisplayMode>!> GetFullscreenModes(BrutalEngine.Platform.DisplayId display)",
            "BrutalEngine.Core.Result<System.String!> GetClipboardText()",
            "BrutalEngine.Core.Result SetClipboardText(System.String! text)",
            "BrutalEngine.Core.Result SetCursor(BrutalEngine.Platform.SystemCursor cursor)",
            "BrutalEngine.Core.Result SetCursorVisible(System.Boolean visible)",
            "BrutalEngine.Core.Result ShowMessageBox(BrutalEngine.Platform.MessageBoxRequest! request)",
        ];

        Type type = typeof(IPlatform);
        NullabilityInfoContext nullability = new();
        IEnumerable<string> properties = type.GetProperties().Select(p =>
            $"{Describe(nullability.Create(p))} {p.Name} {{ {(p.CanWrite ? "get; set;" : "get;")} }}");
        IEnumerable<string> methods = type.GetMethods().Where(m => !m.IsSpecialName).Select(m =>
            $"{Describe(nullability.Create(m.ReturnParameter))} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{Describe(nullability.Create(p))} {p.Name}"))})");
        IEnumerable<string> events = type.GetEvents().Select(e => $"event {Describe(nullability.Create(e))} {e.Name}");
        IEnumerable<string> nestedTypes = type.GetNestedTypes().Select(t => $"nested {t}");

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            properties.Concat(methods).Concat(events).Concat(nestedTypes).Order(StringComparer.Ordinal));
        Assert.Equal([typeof(IDisposable)], type.GetInterfaces());
        Assert.True(type.IsPublic);
    }

    // A type as C# writes it, with '!' (not null), '?' (nullable) or '~' (oblivious) after each reference type.
    private static string Describe(NullabilityInfo info)
    {
        Type type = info.Type;
        string name = type.IsGenericType
            ? $"{type.Namespace}.{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", info.GenericTypeArguments.Select(Describe))}>"
            : type.FullName!;
        string state = info.ReadState switch
        {
            NullabilityState.NotNull => "!",
            NullabilityState.Nullable => "?",
            _ => "~",
        };
        return type.IsValueType ? name : name + state;
    }

    private static void Exercise<T>(T platform)
        where T : IPlatform
    {
        Assert.Equal("Test", platform.BackendName);
        Assert.Equal("dummy", platform.VideoDriver);
        Assert.Equal(HeadlessMode.NoSurface, platform.Headless);
        Assert.Same(TimeProvider.System, platform.Clock);
        Assert.Equal(AppContext.BaseDirectory, platform.BaseDirectory);
        Assert.Equal(TestPlatform.UserData, platform.GetUserDataDirectory().Value);

        Result<IWindow> window = platform.CreateWindow(new WindowOptions { Surface = WindowSurface.None });
        Assert.Equal(ErrorKind.Unsupported, window.Error.Kind);

        Assert.True(platform.Events.Events.IsEmpty);
        PlatformEventBatch batch = platform.PumpEvents();
        Assert.Equal(PlatformEventKind.QuitRequested, Assert.Single(batch.Events.ToArray()).Kind);
        Assert.Equal(1, platform.Events.Events.Length);
        Assert.False(platform.WaitForEvents(TimeSpan.Zero));

        DisplayInfo display = Assert.Single(platform.Displays);
        Assert.Equal(display.CurrentMode, Assert.Single(platform.GetFullscreenModes(display.Id).Value));

        Assert.True(platform.SetClipboardText("copied").IsSuccess);
        Assert.Equal("copied", platform.GetClipboardText().Value);
        Assert.True(platform.SetCursor(SystemCursor.Text).IsSuccess);
        Assert.True(platform.SetCursorVisible(false).IsSuccess);
        Assert.Equal(
            ErrorKind.Unavailable,
            platform.ShowMessageBox(new MessageBoxRequest(MessageBoxKind.Error, "Title", "Message")).Error.Kind);

        platform.Dispose();
        platform.Dispose();
        Assert.Throws<ObjectDisposedException>(() => platform.PumpEvents().Events.Length);
    }

    // A minimal, headless platform: one display, a clipboard and an event buffer, and no windows.
    private sealed class TestPlatform : IPlatform
    {
        public const string UserData = "/user/data";

        private static readonly DisplayMode _mode = new() { Display = new DisplayId(1), Width = 1920, Height = 1080, PixelDensity = 1f, RefreshRate = 60f };

        private readonly PlatformEventBuffer _buffer = new(64, 2048);
        private readonly DisplayInfo[] _displays =
        [
            new() { Id = new DisplayId(1), Name = "Test", Bounds = new(0, 0, 1920, 1080), UsableBounds = new(0, 0, 1920, 1040), ContentScale = 1f, CurrentMode = _mode, IsPrimary = true },
        ];

        private string _clipboard = string.Empty;
        private bool _disposed;

        public string BackendName => "Test";

        public string VideoDriver => "dummy";

        public HeadlessMode Headless => HeadlessMode.NoSurface;

        public TimeProvider Clock => TimeProvider.System;

        public string BaseDirectory => AppContext.BaseDirectory;

        public PlatformEventBatch Events => _buffer.AsBatch();

        public IReadOnlyList<DisplayInfo> Displays => _displays;

        public Result<string> GetUserDataDirectory() => UserData;

        public Result<IWindow> CreateWindow(WindowOptions options) =>
            new EngineError(ErrorKind.Unsupported, "The test platform has no windows.");

        public PlatformEventBatch PumpEvents()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _buffer.Clear();
            _buffer.Add(PlatformEvent.Create(new PlatformEventHeader(PlatformEventKind.QuitRequested, default, Clock.GetTimestamp())));
            return _buffer.AsBatch();
        }

        public bool WaitForEvents(TimeSpan timeout) => false;

        public Result<IReadOnlyList<DisplayMode>> GetFullscreenModes(DisplayId display) =>
            display == _mode.Display ? new DisplayMode[] { _mode } : new EngineError(ErrorKind.NotFound, "No such display.");

        public Result<string> GetClipboardText() => _clipboard;

        public Result SetClipboardText(string text)
        {
            _clipboard = text;
            return Result.Success;
        }

        public Result SetCursor(SystemCursor cursor) => Result.Success;

        public Result SetCursorVisible(bool visible) => Result.Success;

        public Result ShowMessageBox(MessageBoxRequest request) =>
            new EngineError(ErrorKind.Unavailable, "Headless platforms show no message boxes.");

        public void Dispose() => _disposed = true;
    }
}
