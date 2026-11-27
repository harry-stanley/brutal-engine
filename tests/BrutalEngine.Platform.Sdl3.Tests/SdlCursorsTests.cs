using System;
using System.Collections.Generic;
using Brutal.SdlApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlCursorsTests
{
    // Written from Brutal.Sdl/Generated/Enums/SdlSystemCursor.gen.cs, not from the code under test.
    [Theory]
    [InlineData(SystemCursor.Default, SdlSystemCursor.Default, 0u)]
    [InlineData(SystemCursor.Text, SdlSystemCursor.Text, 1u)]
    [InlineData(SystemCursor.Wait, SdlSystemCursor.Wait, 2u)]
    [InlineData(SystemCursor.Crosshair, SdlSystemCursor.Crosshair, 3u)]
    [InlineData(SystemCursor.Progress, SdlSystemCursor.Progress, 4u)]
    [InlineData(SystemCursor.ResizeNwse, SdlSystemCursor.NwseResize, 5u)]
    [InlineData(SystemCursor.ResizeNesw, SdlSystemCursor.NeswResize, 6u)]
    [InlineData(SystemCursor.ResizeEw, SdlSystemCursor.EwResize, 7u)]
    [InlineData(SystemCursor.ResizeNs, SdlSystemCursor.NsResize, 8u)]
    [InlineData(SystemCursor.Move, SdlSystemCursor.Move, 9u)]
    [InlineData(SystemCursor.NotAllowed, SdlSystemCursor.NotAllowed, 10u)]
    [InlineData(SystemCursor.Pointer, SdlSystemCursor.Pointer, 11u)]
    public void EveryShape_Maps(SystemCursor cursor, SdlSystemCursor expected, uint sdlValue)
    {
        var native = new FakeCursors();
        using var cursors = new SdlCursors(native);

        Result set = cursors.Set(cursor);

        Assert.Equal(sdlValue, (uint)expected);
        Assert.Equal(expected, SdlCursors.ToSdl(cursor));
        Assert.True(set.IsSuccess, set.ToString());
        Assert.Equal([$"create {expected}", $"set {expected}"], native.Calls);
    }

    [Fact]
    public void EveryShape_HasARow() => Assert.Equal(12, Enum.GetValues<SystemCursor>().Length);

    [Theory]
    [InlineData(-1)]
    [InlineData(12)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UndefinedShape_Throws(int value)
    {
        var native = new FakeCursors();
        using var cursors = new SdlCursors(native);

        Assert.Throws<ArgumentOutOfRangeException>(() => SdlCursors.ToSdl((SystemCursor)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => cursors.Set((SystemCursor)value));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void SameShapeTwice_CreatesOnce()
    {
        var native = new FakeCursors();
        using var cursors = new SdlCursors(native);

        Assert.True(cursors.Set(SystemCursor.Text).IsSuccess);
        Assert.True(cursors.Set(SystemCursor.Move).IsSuccess);
        Assert.True(cursors.Set(SystemCursor.Text).IsSuccess);

        Assert.Equal(["create Text", "set Text", "create Move", "set Move", "set Text"], native.Calls);
    }

    [Fact]
    public void Dispose_FreesEveryCreatedShapeOnce()
    {
        var native = new FakeCursors();
        var cursors = new SdlCursors(native);
        Assert.True(cursors.Set(SystemCursor.Wait).IsSuccess);
        Assert.True(cursors.Set(SystemCursor.Pointer).IsSuccess);
        native.Calls.Clear();

        cursors.Dispose();
        cursors.Dispose();

        Assert.Equal(["destroy Wait", "destroy Pointer"], native.Calls);
        Assert.Throws<ObjectDisposedException>(() => cursors.Set(SystemCursor.Default));
        Assert.Throws<ObjectDisposedException>(() => cursors.SetVisible(true));
        Assert.Equal(2, native.Calls.Count);
    }

    [Fact]
    public void CreateFails_IsNativeAndRetriedNextCall()
    {
        var native = new FakeCursors { FailCreate = true };
        var cursors = new SdlCursors(native);

        Result failed = cursors.Set(SystemCursor.Crosshair);
        native.FailCreate = false;
        Result retried = cursors.Set(SystemCursor.Crosshair);
        cursors.Dispose();

        Assert.True(failed.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.StartsWith("SDL_CreateSystemCursor failed", error.Message, StringComparison.Ordinal);
        Assert.True(retried.IsSuccess, retried.ToString());
        Assert.Equal(["create Crosshair", "create Crosshair", "set Crosshair", "destroy Crosshair"], native.Calls);
    }

    [Fact]
    public void SetRefused_IsNativeAndKeepsTheCursor()
    {
        var native = new FakeCursors { FailSet = true };
        var cursors = new SdlCursors(native);

        Result failed = cursors.Set(SystemCursor.ResizeEw);
        native.FailSet = false;
        Result retried = cursors.Set(SystemCursor.ResizeEw);
        cursors.Dispose();

        Assert.True(failed.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.StartsWith("SDL_SetCursor failed", error.Message, StringComparison.Ordinal);
        Assert.True(retried.IsSuccess, retried.ToString());
        Assert.Equal(["create EwResize", "set EwResize", "set EwResize", "destroy EwResize"], native.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void SetVisible_CallsShowOrHide(bool visible, bool refused)
    {
        var native = new FakeCursors { FailVisibility = refused };
        using var cursors = new SdlCursors(native);

        Result result = cursors.SetVisible(visible);

        Assert.Equal([visible ? "show" : "hide"], native.Calls);
        Assert.Equal(!refused, result.IsSuccess);
        if (refused)
        {
            Assert.Equal(ErrorKind.Native, result.Error.Kind);
            Assert.StartsWith(visible ? "SDL_ShowCursor failed" : "SDL_HideCursor failed", result.Error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Constructor_NullNative_Throws() => Assert.Throws<ArgumentNullException>(() => new SdlCursors(null!));

    [Fact]
    public void Set_OnDummy_IsNative()
    {
        // SDL's dummy driver creates no cursors (observed): the refusal is a Result, and the next call asks again.
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start();
        using var cursors = new SdlCursors();

        Result first = cursors.Set(SystemCursor.Text);
        Result second = cursors.Set(SystemCursor.Text);

        Assert.Equal(new EngineError(ErrorKind.Native, "SDL_CreateSystemCursor failed: CreateSystemCursor is not currently supported"), first.Error);
        Assert.Equal(first.Error, second.Error);
    }

    [Fact]
    public void SetVisible_OnDummy_ChangesSdlsVisibility()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start();
        using var cursors = new SdlCursors();

        Result hidden = cursors.SetVisible(false);
        byte visibleAfterHide = Sdl.PInvoke.CursorVisible();
        Result shown = cursors.SetVisible(true);
        byte visibleAfterShow = Sdl.PInvoke.CursorVisible();

        Assert.True(hidden.IsSuccess, hidden.ToString());
        Assert.Equal(0, visibleAfterHide);
        Assert.True(shown.IsSuccess, shown.ToString());
        Assert.NotEqual(0, visibleAfterShow);
    }

    private static SdlVideoSession Start()
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = HeadlessMode.NoSurface };
        return SdlVideoSession.Start(options, null).Value;
    }

    /// <summary>
    /// Logs every call by the shape it concerns and never touches SDL. Each shape's fake handle is 0x1000 times its
    /// value plus one: never null, never dereferenced, and mapped back to the shape's name in the log.
    /// </summary>
    private sealed class FakeCursors : SdlCursors.INative
    {
        public List<string> Calls { get; } = [];

        public bool FailCreate { get; set; }

        public bool FailSet { get; set; }

        public bool FailVisibility { get; set; }

        public unsafe SdlCursor CreateSystemCursor(SdlSystemCursor id)
        {
            Calls.Add($"create {id}");
            return FailCreate ? default : (SdlCursor)(void*)(0x1000 * ((nint)id + 1));
        }

        public byte SetCursor(SdlCursor cursor) => Log($"set {Shape(cursor)}", FailSet);

        public void DestroyCursor(SdlCursor cursor) => Calls.Add($"destroy {Shape(cursor)}");

        public byte ShowCursor() => Log("show", FailVisibility);

        public byte HideCursor() => Log("hide", FailVisibility);

        private static SdlSystemCursor Shape(SdlCursor cursor) => (SdlSystemCursor)((cursor.Pointer.Value / 0x1000) - 1);

        private byte Log(string call, bool fail)
        {
            Calls.Add(call);
            return fail ? (byte)0 : (byte)1;
        }
    }
}
