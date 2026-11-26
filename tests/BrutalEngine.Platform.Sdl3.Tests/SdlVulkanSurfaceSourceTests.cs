using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using Brutal.SdlApi;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlVulkanSurfaceSourceTests
{
    private const int WorkerTimeoutSeconds = 30;

    [Fact]
    [Trait("Category", "Gpu")]
    public void Extensions_MatchSdlList()
    {
        using VulkanWindowHarness harness = new();

        // BRUTAL's convenience wrapper (Brutal.Sdl/Manual/Libraries/Sdl.Vulkan.cs:47-59) reads the same SDL list into
        // strings, independently of the copy under test.
        string[] expected = Sdl.Vulkan.GetInstanceExtensions();

        Assert.NotEmpty(expected);
        Assert.Contains("VK_KHR_surface", expected);
        Assert.Equal(expected, ReadAll(harness.Source));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(Encoding.ASCII.GetBytes(expected[i]), harness.Source.GetRequiredInstanceExtension(i).ToArray());
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void ExtensionGetters_DoNotAllocate()
    {
        using VulkanWindowHarness harness = new();
        SdlVulkanSurfaceSource source = harness.Source;
        _ = ReadLengths(source);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = ReadLengths(source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(total > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void ExtensionGetters_FromOtherThread_Work()
    {
        using VulkanWindowHarness harness = new();
        string[] expected = ReadAll(harness.Source);
        string[]? actual = null;
        ConcurrentQueue<Exception> failures = new();
        using ManualResetEventSlim done = new();
        Thread worker = new(() =>
        {
            try
            {
                actual = ReadAll(harness.Source);
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(WorkerTimeoutSeconds), TestContext.Current.CancellationToken), "The worker thread didn't finish within 30 s.");
        Assert.True(worker.Join(TimeSpan.FromSeconds(WorkerTimeoutSeconds)));
        Assert.Empty(failures);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [Trait("Category", "Gpu")]
    [InlineData("negative index", typeof(ArgumentOutOfRangeException))]
    [InlineData("minimum index", typeof(ArgumentOutOfRangeException))]
    [InlineData("index equal to count", typeof(ArgumentOutOfRangeException))]
    [InlineData("disposed count", typeof(ObjectDisposedException))]
    [InlineData("disposed get", typeof(ObjectDisposedException))]
    public void Misuse_Throws(string misuse, Type expected)
    {
        using VulkanWindowHarness harness = new();
        SdlVulkanSurfaceSource source = harness.Source;
        int count = source.RequiredInstanceExtensionCount;
        harness.IsDisposed = misuse.StartsWith("disposed", StringComparison.Ordinal);

        Exception? thrown = Record.Exception(() =>
        {
            switch (misuse)
            {
                case "negative index":
                    _ = source.GetRequiredInstanceExtension(-1).Length;
                    break;
                case "minimum index":
                    _ = source.GetRequiredInstanceExtension(int.MinValue).Length;
                    break;
                case "index equal to count":
                    _ = source.GetRequiredInstanceExtension(count).Length;
                    break;
                case "disposed count":
                    _ = source.RequiredInstanceExtensionCount;
                    break;
                case "disposed get":
                    _ = source.GetRequiredInstanceExtension(0).Length;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(misuse), misuse, "Unknown case.");
            }
        });

        Assert.NotNull(thrown);
        Assert.IsType(expected, thrown, exactMatch: true);
        if (thrown is ArgumentException argument)
        {
            Assert.Equal("index", argument.ParamName);
        }
    }

    [Fact]
    public void Create_NotAVulkanWindow_Throws()
    {
        // On the dummy driver, which has no Vulkan, SDL_Vulkan_GetInstanceExtensions would call a null function pointer.
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = HeadlessMode.NoSurface };
        using SdlVideoSession session = SdlVideoSession.Start(options, null).Value;
        using SdlWindow window = Sdl.PInvoke.CreateWindow("Platform.Sdl3.Tests", 64, 64, SdlWindowFlags.Hidden);
        Assert.False(window.IsNull(), Sdl.PInvoke.GetError());

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => SdlVulkanSurfaceSource.Create(window, new Owner()));

        Assert.Equal("window", thrown.ParamName);
        Assert.Contains("SDL_WINDOW_VULKAN", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_InvalidArguments_Throw()
    {
        ArgumentException window = Assert.Throws<ArgumentException>(() => SdlVulkanSurfaceSource.Create(default, new Owner()));
        ArgumentNullException owner = Assert.Throws<ArgumentNullException>(() => SdlVulkanSurfaceSource.Create(default, null!));

        Assert.Equal("window", window.ParamName);
        Assert.Equal("owner", owner.ParamName);
    }

    private static string[] ReadAll(SdlVulkanSurfaceSource source)
    {
        string[] names = new string[source.RequiredInstanceExtensionCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = Encoding.ASCII.GetString(source.GetRequiredInstanceExtension(i));
        }

        return names;
    }

    // Every getter, with no allocation of its own: the count, then each name's length and first byte.
    private static int ReadLengths(SdlVulkanSurfaceSource source)
    {
        int total = 0;
        for (int i = 0; i < source.RequiredInstanceExtensionCount; i++)
        {
            ReadOnlySpan<byte> name = source.GetRequiredInstanceExtension(i);
            total += name.Length + (name[0] == (byte)'V' ? 0 : 1_000_000);
        }

        return total;
    }

    private sealed class Owner : SdlVulkanSurfaceSource.IOwner
    {
        public bool IsDisposed => false;
    }
}
