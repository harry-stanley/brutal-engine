using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Instances;

/// <summary>
/// The process-wide gate around Vulkan's global commands: overlapping them crashes the process with the NVIDIA
/// proprietary driver on Linux, so every one the backend makes runs inside <see cref="GlobalCommands.Gate"/>. The
/// sources check the gate from inside each call, so a call made outside it fails deterministically, whatever the timing.
/// </summary>
public sealed class GlobalCommandsTests
{
    private const string Validation = "VK_LAYER_KHRONOS_validation";

    // The one file allowed to make Vulkan's global commands, relative to the backend's project folder.
    private const string GateFile = "Instances/GlobalCommands.cs";

    // What InstanceFactory returns when the fake sources' vkCreateInstance fails as BRUTAL's wrapper does.
    private static readonly EngineError _createFailed = new(
        ErrorKind.Native,
        "vkCreateInstance failed: Vulkan call to vkCreateInstance failed with error: VK_ERROR_INITIALIZATION_FAILED",
        (int)VkResult.ErrorInitializationFailed);

    [Fact]
    public void Select_EveryEnumeration_RunsInsideTheGate()
    {
        FakeEnumeration loader = ValidationLoader();

        Result<InstanceExtensions.Selection> result = InstanceExtensions.Select(loader, new InstanceFactory.Options { Validation = true }, new VulkanCallGate());

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(
            ["extensions() count", "extensions() fill", "layers count", "layers fill", $"extensions({Validation}) count", $"extensions({Validation}) fill"],
            loader.Calls);
        Assert.Empty(loader.CallsOutsideGate);
    }

    [Fact]
    public void Create_CreateInstance_RunsInsideTheGate()
    {
        FakeEnumeration loader = new();

        Result<VulkanInstance> result = InstanceFactory.Create(new InstanceFactory.Options(), new VulkanCallGate(), loader);

        Assert.Equal(_createFailed, result.Error);
        Assert.Equal(["extensions() count", "create"], loader.Calls);
        Assert.Empty(loader.CallsOutsideGate);
    }

    [Fact]
    public void Select_Returns_ReleasesTheGate()
    {
        FakeEnumeration loader = ValidationLoader();

        Result<InstanceExtensions.Selection> result = InstanceExtensions.Select(loader, new InstanceFactory.Options { Validation = true }, new VulkanCallGate());

        Assert.True(result.IsSuccess, result.ToString());
        Assert.False(GlobalCommands.Gate.IsHeldByCurrentThread);
        Assert.True(OtherThreadEntersTheGate());
    }

    [Fact]
    public void Create_SourceThrows_ReleasesTheGate()
    {
        FakeEnumeration loader = new();

        Result<VulkanInstance> result = InstanceFactory.Create(new InstanceFactory.Options(), new VulkanCallGate(), loader);

        // The source threw VkResultError inside the gate.
        Assert.Equal(_createFailed, result.Error);
        Assert.False(GlobalCommands.Gate.IsHeldByCurrentThread);
        Assert.True(OtherThreadEntersTheGate());
    }

    [Fact]
    public void Create_SeveralThreads_GlobalCommandsNeverOverlap()
    {
        const int Threads = 8;
        const int PerThread = 25;
        OverlapRecorder source = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        using Barrier start = new(Threads);
        ConcurrentQueue<Exception> failures = new();
        var threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            threads[t] = new Thread(() =>
            {
                try
                {
                    // Every thread is released at once, each with its own device gate, as devices on several threads are.
                    Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30), token));
                    for (int i = 0; i < PerThread; i++)
                    {
                        Result<VulkanInstance> result = InstanceFactory.Create(new InstanceFactory.Options(), new VulkanCallGate(), source);
                        Assert.Equal(_createFailed, result.Error);
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        }

        Assert.Empty(failures);

        // Each creation: the loader's extensions (count, fill), then vkCreateInstance.
        Assert.Equal(Threads * PerThread * 3, source.Calls);
        Assert.Equal(0, source.CallsOutsideGate);
        Assert.Equal(1, source.MostInsideAtOnce);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_RealLoader_GlobalCommandsRunInsideTheGate()
    {
        GpuValidation.BindLoader();
        GateCheckingLoader loader = new();

        Result<VulkanInstance> result = InstanceFactory.Create(
            new InstanceFactory.Options { ApplicationName = nameof(GlobalCommandsTests), Validation = true }, new VulkanCallGate(), loader);

        Assert.True(result.IsSuccess, result.ToString());
        using VulkanInstance instance = result.Value;
        Assert.NotEmpty(instance.Brutal.PhysicalDevices);
        Assert.Equal(["extensions", "extensions", "layers", "layers", "extensions", "extensions", "create"], loader.Calls);
        Assert.Empty(loader.CallsOutsideGate);
    }

    [Fact]
    public void Backend_GlobalCommands_AreCalledOnlyFromTheGateFile()
    {
        string project = Path.Combine(RepositoryRoot(), "src", "BrutalEngine.Graphics.Vulkan");
        List<string> bypasses = [];
        foreach (string file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(project, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal) || relative == GateFile)
            {
                continue;
            }

            foreach (string found in GlobalCommandScan.FindBypasses(File.ReadAllText(file)))
            {
                bypasses.Add(relative + ":" + found);
            }
        }

        // A global command made anywhere else skips the process-wide gate, and concurrent instance creation can crash
        // the process again on drivers that race (the NVIDIA proprietary driver on Linux): call it through GlobalCommands.
        // What the scan catches, and what it can't see, is on GlobalCommandScan.
        Assert.True(bypasses.Count == 0, "Global Vulkan commands outside " + GateFile + ":\n" + string.Join('\n', bypasses));
    }

    // A loader that offers what validation needs, so a validated Select makes every enumeration call.
    private static FakeEnumeration ValidationLoader()
    {
        FakeEnumeration loader = new();
        loader.Extensions[string.Empty].Add("VK_EXT_debug_utils");
        loader.Layers.Add(Validation);
        loader.Extensions[Validation] = ["VK_EXT_debug_utils", "VK_EXT_validation_features"];
        return loader;
    }

    // Whether another thread can take the gate: it waits for any other test's global command to finish, so only a gate
    // this thread still holds (or leaked) makes it give up.
    private static bool OtherThreadEntersTheGate()
    {
        bool entered = false;
        Exception? failure = null;
        Thread other = new(() =>
        {
            try
            {
                entered = GlobalCommands.Gate.TryEnter(TimeSpan.FromSeconds(30));
                if (entered)
                {
                    GlobalCommands.Gate.Exit();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(failure);
        return entered;
    }

    // The repository root: the nearest folder above the test binaries that holds the solution file.
    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BrutalEngine.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("BrutalEngine.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// A thread-safe loader that offers one extension and fails <c>vkCreateInstance</c> as BRUTAL's wrapper does. It
    /// counts its calls, the calls made outside the gate, and the most calls in progress at once.
    /// </summary>
    private sealed class OverlapRecorder : GlobalCommands.ISource
    {
        private int _calls;
        private int _outsideGate;
        private int _inside;
        private int _mostInside;

        public int Calls => Volatile.Read(ref _calls);

        public int CallsOutsideGate => Volatile.Read(ref _outsideGate);

        public int MostInsideAtOnce => Volatile.Read(ref _mostInside);

        public unsafe VkResult EnumerateExtensions(string? layerName, ref int count, Span<VkExtensionProperties> properties)
        {
            Enter();
            try
            {
                if (!properties.IsEmpty)
                {
                    // Sound: the entry is pinned, and its name is a 256-byte buffer.
                    fixed (VkExtensionProperties* entry = properties)
                    {
                        Span<byte> name = new(entry->ExtensionName, VK.MAX_EXTENSION_NAME_SIZE);
                        name[Encoding.UTF8.GetBytes("VK_EXT_debug_utils", name)] = 0;
                    }
                }

                count = 1;
                return VkResult.Success;
            }
            finally
            {
                Exit();
            }
        }

        public VkResult EnumerateLayers(ref int count, Span<VkLayerProperties> properties)
        {
            Enter();
            count = 0;
            Exit();
            return VkResult.Success;
        }

        public Instance CreateInstance(Instance.CreateInfo createInfo)
        {
            Enter();
            try
            {
                throw new VkResultError(VkResult.ErrorInitializationFailed, "Vulkan call to vkCreateInstance failed with error: VK_ERROR_INITIALIZATION_FAILED");
            }
            finally
            {
                Exit();
            }
        }

        private void Enter()
        {
            _ = Interlocked.Increment(ref _calls);
            if (!GlobalCommands.Gate.IsHeldByCurrentThread)
            {
                _ = Interlocked.Increment(ref _outsideGate);
            }

            int inside = Interlocked.Increment(ref _inside);
            int most = Volatile.Read(ref _mostInside);
            while (inside > most)
            {
                int seen = Interlocked.CompareExchange(ref _mostInside, inside, most);
                if (seen == most)
                {
                    break;
                }

                most = seen;
            }

            // Lets another thread run while this call is in progress; without the gate, its call would overlap this one.
            _ = Thread.Yield();
        }

        private void Exit() => _ = Interlocked.Decrement(ref _inside);
    }

    /// <summary>The real loader, recording each call and whether it was made inside the gate.</summary>
    private sealed class GateCheckingLoader : GlobalCommands.ISource
    {
        public List<string> Calls { get; } = [];

        public List<string> CallsOutsideGate { get; } = [];

        public VkResult EnumerateExtensions(string? layerName, ref int count, Span<VkExtensionProperties> properties)
        {
            Record("extensions");
            return default(GlobalCommands.LoaderSource).EnumerateExtensions(layerName, ref count, properties);
        }

        public VkResult EnumerateLayers(ref int count, Span<VkLayerProperties> properties)
        {
            Record("layers");
            return default(GlobalCommands.LoaderSource).EnumerateLayers(ref count, properties);
        }

        public Instance CreateInstance(Instance.CreateInfo createInfo)
        {
            Record("create");
            return default(GlobalCommands.LoaderSource).CreateInstance(createInfo);
        }

        private void Record(string call)
        {
            Calls.Add(call);
            if (!GlobalCommands.Gate.IsHeldByCurrentThread)
            {
                CallsOutsideGate.Add(call);
            }
        }
    }
}
