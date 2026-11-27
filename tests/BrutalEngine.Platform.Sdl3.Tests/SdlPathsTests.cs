using System;
using System.IO;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlPathsTests
{
    [Fact]
    public void BaseDirectory_IsAbsolute()
    {
        // No SDL_Init: the platform reads it before SDL video starts. The test binary's folder holds the application.
        Result<string> read = SdlPaths.ReadBaseDirectory();

        Assert.True(read.TryGetValue(out string? directory), read.ToString());
        Assert.True(Path.IsPathFullyQualified(directory), directory);
        Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.TrimEndingDirectorySeparator(directory));
    }

    [Fact]
    public void UserDataDirectory_OverrideWinsAndIsCached()
    {
        using var root = new DataRoot();
        string overrideDirectory = Path.Join(root.Path, "override");
        var paths = new SdlPaths(root.Options with { UserDataDirectory = overrideDirectory });

        Result<string> first = paths.GetUserDataDirectory();
        Result<string> second = paths.GetUserDataDirectory();

        Assert.Equal(overrideDirectory, first.Value);
        Assert.Equal(overrideDirectory, second.Value);

        // SDL wasn't asked: it would have created the organisation's folder.
        Assert.False(Directory.Exists(root.OrganizationDirectory), root.OrganizationDirectory);
        Assert.False(File.Exists(root.OrganizationDirectory), root.OrganizationDirectory);
    }

    [Fact]
    public void UserDataDirectory_FromSdl_IsCreatedOnceThenCached()
    {
        using var root = new DataRoot();
        var paths = new SdlPaths(root.Options);

        // Constructing asks SDL nothing.
        Assert.False(Directory.Exists(root.OrganizationDirectory), root.OrganizationDirectory);

        Result<string> first = paths.GetUserDataDirectory();

        Assert.True(first.TryGetValue(out string? directory), first.ToString());
        Assert.Equal(
            Path.Join(root.OrganizationDirectory, root.Options.Application),
            Path.TrimEndingDirectorySeparator(directory));
        Assert.True(Directory.Exists(directory), directory);

        // A second call returns the kept string without asking SDL, which would create the folder again.
        Directory.Delete(root.OrganizationDirectory, recursive: true);
        Result<string> second = paths.GetUserDataDirectory();

        Assert.Same(directory, second.Value);
        Assert.False(Directory.Exists(root.OrganizationDirectory), root.OrganizationDirectory);
    }

    [Fact]
    public void UserDataDirectory_CantCreate_IsIoAndNotCached()
    {
        // A file where the organisation's folder goes: SDL can't create the application's folder inside it.
        using var root = new DataRoot();
        File.WriteAllBytes(root.OrganizationDirectory, []);
        var paths = new SdlPaths(root.Options);

        Result<string> failed = paths.GetUserDataDirectory();

        Assert.True(failed.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Io, error.Kind);
        Assert.StartsWith("SDL_GetPrefPath failed: ", error.Message, StringComparison.Ordinal);

        // The failure isn't kept: once the file is gone, the next call resolves and creates the folder.
        File.Delete(root.OrganizationDirectory);
        Result<string> retried = paths.GetUserDataDirectory();

        Assert.True(retried.TryGetValue(out string? directory), retried.ToString());
        Assert.True(Directory.Exists(directory), directory);
    }

    [Fact]
    public unsafe void TakePrefPath_Null_IsIoWithSdlText()
    {
        // A NULL environment is an invalid parameter: SDL sets its error text, with no SDL_Init.
        _ = Brutal.SdlApi.Sdl.PInvoke.SetEnvironmentVariable(0, "BRUTALENGINE_TEST", "1", 1);
        string? text = Brutal.SdlApi.Sdl.PInvoke.GetError();

        Result<string> taken = SdlPaths.TakePrefPath(null);

        Assert.False(string.IsNullOrEmpty(text));
        Assert.True(taken.TryGetError(out EngineError error));
        Assert.Equal(new EngineError(ErrorKind.Io, "SDL_GetPrefPath failed: " + text), error);
    }

    [Fact]
    public void Constructor_NullOptions_Throws() => Assert.Throws<ArgumentNullException>(() => new SdlPaths(null!));

    /// <summary>
    /// A unique organisation name, with SDL's per-user data root pointed at a temporary directory on Linux
    /// (<c>XDG_DATA_HOME</c>, set in SDL's own environment). Windows has no such variable: there the organisation's
    /// folder is made under the real roaming application data folder, and removed on Dispose.
    /// </summary>
    private sealed class DataRoot : IDisposable
    {
        private readonly NativeEnvironmentVariable? _xdgDataHome;

        public DataRoot()
        {
            Path = Directory.CreateTempSubdirectory("brutalengine-paths-").FullName;
            string organization = "BrutalEngine Tests " + Guid.NewGuid().ToString("N");
            Options = new PlatformOptions { Organization = organization, Application = "Platform.Sdl3.Tests" };
            string dataRoot;
            if (OperatingSystem.IsWindows())
            {
                dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            else
            {
                dataRoot = Directory.CreateDirectory(System.IO.Path.Join(Path, "data")).FullName;
                _xdgDataHome = NativeEnvironmentVariable.Set("XDG_DATA_HOME", dataRoot);
            }

            OrganizationDirectory = System.IO.Path.Join(dataRoot, organization);
        }

        public string Path { get; }

        public PlatformOptions Options { get; }

        public string OrganizationDirectory { get; }

        public void Dispose()
        {
            _xdgDataHome?.Dispose();
            if (File.Exists(OrganizationDirectory))
            {
                File.Delete(OrganizationDirectory);
            }

            if (Directory.Exists(OrganizationDirectory))
            {
                Directory.Delete(OrganizationDirectory, recursive: true);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
