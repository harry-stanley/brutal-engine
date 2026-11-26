using System;
using Brutal.SdlApi;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

/// <summary>
/// Sets or unsets a variable in SDL's process environment for one test and restores it on Dispose. .NET's
/// Environment.SetEnvironmentVariable doesn't reach native code on Linux, so this goes through SDL_GetEnvironment.
/// </summary>
internal sealed class NativeEnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    private NativeEnvironmentVariable(string name, string? value)
    {
        _name = name;
        _previous = Sdl.PInvoke.GetEnvironmentVariable(Environment(), name);
        Apply(name, value);
    }

    /// <summary>Sets <paramref name="name"/> to <paramref name="value"/>, or unsets it when null.</summary>
    public static NativeEnvironmentVariable Set(string name, string? value) => new(name, value);

    public void Dispose() => Apply(_name, _previous);

    // Read each time: SDL_Quit drops SDL's copy of the environment (observed), and the next call rebuilds it from the
    // process's environment. So a variable set here lasts until the next SDL_Quit.
    private static nint Environment()
    {
        nint environment = Sdl.PInvoke.GetEnvironment();
        Assert.NotEqual(0, environment);
        return environment;
    }

    private static void Apply(string name, string? value)
    {
        byte status = value is null
            ? Sdl.PInvoke.UnsetEnvironmentVariable(Environment(), name)
            : Sdl.PInvoke.SetEnvironmentVariable(Environment(), name, value, 1);
        Assert.NotEqual(0, status);
    }
}
