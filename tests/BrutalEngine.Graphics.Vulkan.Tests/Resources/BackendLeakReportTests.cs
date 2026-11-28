using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Resources;
using BrutalEngine.Graphics.Vulkan.Tests.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// The leak report on real devices (every suitable adapter of one shared validated instance), and over scripted factories
/// for the order of the native destroys.
/// </summary>
public sealed class BackendLeakReportTests(GpuResourceFixture fixture) : IClassFixture<GpuResourceFixture>
{
    // Three textures and two buffers, the middle texture unnamed and one more of each destroyed before the report:
    // textures newest first, then buffers newest first, each line in the documented format.
    private static readonly string[] _expectedLines =
    [
        "Backend leak: texture 'third texture' was still alive at disposal and is destroyed now",
        "Backend leak: texture (unnamed) was still alive at disposal and is destroyed now",
        "Backend leak: texture 'first texture' was still alive at disposal and is destroyed now",
        "Backend leak: buffer 'second buffer' was still alive at disposal and is destroyed now",
        "Backend leak: buffer 'first buffer' was still alive at disposal and is destroyed now",
    ];

    [Fact]
    [Trait("Category", "Gpu")]
    public void DestroyAll_LeftObjects_LogsNewestFirst()
    {
        long errors = fixture.Instance.ValidationErrors;
        foreach (GpuResourceFixture.Gpu gpu in fixture.Gpus)
        {
            (ValidationLog lines, Logger logger, LogCategory log) = NewLog();
            using (logger)
            {
                TextureFactory textures = new(gpu.Core, gpu.Vma, gpu.Gate, default);
                BufferFactory buffers = new(gpu.Core, gpu.Vma, gpu.Gate, default);
                Fill(textures, buffers);
                Assert.Equal(5u, BackendLeakReport.VmaAllocationCount(gpu.Vma, default));

                int destroyed = BackendLeakReport.DestroyAll(textures, buffers, log);

                Assert.Equal(5, destroyed);
                Assert.Equal(_expectedLines, lines.Entries.Select(e => e.Message));
                Assert.All(lines.Entries, e => Assert.Equal(LogLevel.Error, e.Level));
                Assert.Equal((0, 0), (textures.Table.Count, buffers.Table.Count));
                Assert.Equal(0u, BackendLeakReport.VmaAllocationCount(gpu.Vma, log));
                Assert.Equal(5, lines.Entries.Count);
            }
        }

        Assert.Equal(errors, fixture.Instance.ValidationErrors);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void VmaCount_LeakedAllocation_IsCounted()
    {
        foreach (GpuResourceFixture.Gpu gpu in fixture.Gpus)
        {
            (ValidationLog lines, Logger logger, LogCategory log) = NewLog();
            using (logger)
            {
                Assert.Equal(0u, BackendLeakReport.VmaAllocationCount(gpu.Vma, log));
                Assert.Empty(lines.Entries);

                // The seam: allocations made straight through VMA, which no table tracks.
                GpuAllocators.Buffer first = GpuAllocators.CreateBuffer(gpu.Vma, MemoryPlacement.For(BackendMemory.DeviceLocal), 256);
                GpuAllocators.Buffer second = GpuAllocators.CreateBuffer(gpu.Vma, MemoryPlacement.For(BackendMemory.HostUpload), 256);
                uint leaked = BackendLeakReport.VmaAllocationCount(gpu.Vma, log);
                gpu.Vma.DestroyBuffer(second.Handle, second.Allocation);
                uint one = BackendLeakReport.VmaAllocationCount(gpu.Vma, log);
                gpu.Vma.DestroyBuffer(first.Handle, first.Allocation);

                Assert.Equal((2u, 1u), (leaked, one));
                Assert.Equal(
                    [
                        (LogLevel.Error, "Backend bug: VMA still holds allocations that no backend table tracks (count 2)"),
                        (LogLevel.Error, "Backend bug: VMA still holds allocations that no backend table tracks (count 1)"),
                    ],
                    lines.Entries);
                Assert.Equal(0u, BackendLeakReport.VmaAllocationCount(gpu.Vma, log));
                Assert.Equal(2, lines.Entries.Count);
            }
        }
    }

    [Fact]
    public void DestroyAll_ScriptedFactories_DestroysEachHandleOnceNewestFirst()
    {
        RecordingTextures textureCalls = new();
        RecordingBuffers bufferCalls = new();
        TextureFactory textures = TextureFactoryScriptedTests.Factory(textureCalls);
        VulkanCallGate gate = new();
        BufferFactory buffers = new(bufferCalls, new DebugNames.Naming(false, gate, default), gate, new ObjectTable<BufferFactory.Entry>(), default);
        List<(VkImage, VmaAllocation)> images = [];
        List<(VkBuffer, VmaAllocation)> created = [];
        (ValidationLog lines, Logger logger, LogCategory log) = NewLog();
        using (logger)
        {
            Fill(textures, buffers, () => images.Add(textureCalls.LastCreated), () => created.Add(bufferCalls.LastCreated));

            Assert.Equal(5, BackendLeakReport.DestroyAll(textures, buffers, log));

            // Fill destroyed texture 2 and buffer 2 first; the report then destroys the rest newest first.
            Assert.Equal([images[2], images[3], images[1], images[0]], textureCalls.Destroyed);
            Assert.Equal([created[2], created[1], created[0]], bufferCalls.Destroyed);
            Assert.Equal(_expectedLines, lines.Entries.Select(e => e.Message));

            // Nothing is left, so a second report destroys and logs nothing.
            Assert.Equal(0, BackendLeakReport.DestroyAll(textures, buffers, log));
            Assert.Equal(5, lines.Entries.Count);
        }
    }

    [Fact]
    public void Arguments_NullOrNullHandle_Throw()
    {
        TextureFactory textures = TextureFactoryScriptedTests.Factory(new RecordingTextures());
        VulkanCallGate gate = new();
        BufferFactory buffers = new(new RecordingBuffers(), new DebugNames.Naming(false, gate, default), gate, new ObjectTable<BufferFactory.Entry>(), default);

        Assert.Throws<ArgumentNullException>("textures", () => BackendLeakReport.DestroyAll(null!, buffers, default));
        Assert.Throws<ArgumentNullException>("buffers", () => BackendLeakReport.DestroyAll(textures, null!, default));
        Assert.Throws<ArgumentException>("vma", () => BackendLeakReport.VmaAllocationCount(default, default));
    }

    private static (ValidationLog Lines, Logger Logger, LogCategory Log) NewLog()
    {
        ValidationLog lines = new();
        (Logger logger, _) = ValidationLog.Create(lines);
        return (lines, logger, logger.CreateCategory("Graphics.Vulkan"));
    }

    // Creates four textures (the second unnamed) and three buffers, then destroys the second-newest texture and the
    // newest buffer, so the report must skip removed entries in the middle and at the head of each table.
    private static void Fill(TextureFactory textures, BufferFactory buffers, Action? onTexture = null, Action? onBuffer = null)
    {
        string?[] textureNames = ["first texture", null, "gone texture", "third texture"];
        ulong[] textureIds = new ulong[textureNames.Length];
        for (int i = 0; i < textureNames.Length; i++)
        {
            Result<ulong> texture = textures.Create(new TextureDescription { Format = TextureFormat.Rgba8Unorm, Width = 16, Height = 16, Usage = TextureUsage.Sampled, DebugName = textureNames[i] });
            Assert.True(texture.IsSuccess, texture.ToString());
            textureIds[i] = texture.Value;
            onTexture?.Invoke();
        }

        string[] bufferNames = ["first buffer", "second buffer", "gone buffer"];
        ulong[] bufferIds = new ulong[bufferNames.Length];
        for (int i = 0; i < bufferNames.Length; i++)
        {
            Result<BackendBuffer> buffer = buffers.Create(new BufferDescription { Size = 256, Usage = BufferUsage.Vertex, DebugName = bufferNames[i] }, BackendMemory.DeviceLocal);
            Assert.True(buffer.IsSuccess, buffer.ToString());
            bufferIds[i] = buffer.Value.Id;
            onBuffer?.Invoke();
        }

        Assert.True(textures.Destroy(textureIds[2]));
        Assert.True(buffers.Destroy(bufferIds[2]));
    }
}
