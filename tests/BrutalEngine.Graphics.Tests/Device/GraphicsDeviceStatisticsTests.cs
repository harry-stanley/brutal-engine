using System;
using System.Globalization;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>The device's statistics snapshot (docs/design/graphics.md section 13).</summary>
public sealed class GraphicsDeviceStatisticsTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void LiveObjects_CountsEveryKind()
    {
        GraphicsDevice device = _fixture.Create();
        Assert.Equal(new GraphicsStatistics(0, 0, 0, 0, 0, 0), device.Statistics);

        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        Assert.Equal(1, device.Statistics.LiveObjects);
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        Assert.Equal(2, device.Statistics.LiveObjects);
        // A texture brings its default view.
        TextureHandle texture = device.CreateTexture(GraphicsDeviceDisposeTests.Prepared.Texture).Value;
        Assert.Equal(4, device.Statistics.LiveObjects);
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value;
        Assert.Equal(5, device.Statistics.LiveObjects);
        // Labels aren't objects.
        _ = device.CreateDebugLabel("Not an object");
        Assert.Equal(5, device.Statistics.LiveObjects);

        device.Destroy(view);
        Assert.Equal(4, device.Statistics.LiveObjects);
        device.Destroy(texture);
        Assert.Equal(2, device.Statistics.LiveObjects);
        device.Destroy(sampler);
        device.Destroy(buffer);
        Assert.Equal(0, device.Statistics.LiveObjects);
        Assert.Equal(_fixture.BackendDevice.LiveObjectCount, device.Statistics.LiveObjects);
    }

    [Fact]
    public void Statistics_ReadsTheBackendsValidationCounts()
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Validation = true });

        _fixture.BackendDevice.AddValidationMessages(errors: 3, warnings: 5);

        Assert.Equal(new GraphicsStatistics(0, 0, 0, 0, 3, 5), device.Statistics);
    }

    [Fact]
    public void Statistics_DoesNotAllocate()
    {
        GraphicsDevice device = _fixture.Create();
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        _ = device.Statistics;

        long before = GC.GetAllocatedBytesForCurrentThread();
        GraphicsStatistics statistics = device.Statistics;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1, statistics.LiveObjects);
        device.Destroy(buffer);
        device.ThrowIfLeaks();
    }

    [Fact]
    public void ToString_UsesInvariantCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        try
        {
            CultureInfo.CurrentCulture = culture;

            string text = new GraphicsStatistics(-1, 2, 3, -4, 5, 6).ToString();

            Assert.Equal(
                "GraphicsStatistics { FramesSubmitted = -1, UploadBytesLastFrame = 2, UploadOverflowBytes = 3, LiveObjects = -4, ValidationErrors = 5, ValidationWarnings = 6 }",
                text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
