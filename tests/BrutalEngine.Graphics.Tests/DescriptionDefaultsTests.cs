using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The defaults of the buffer and texture descriptions (docs/design/graphics.md section 5).</summary>
public sealed class DescriptionDefaultsTests
{
    [Fact]
    public void Defaults_MatchDesign()
    {
        var buffer = new BufferDescription { Size = 256, Usage = BufferUsage.Uniform };
        var texture = new TextureDescription { Format = TextureFormat.Rgba8Unorm, Width = 64, Height = 32, Usage = TextureUsage.Sampled };

        Assert.Equal(256, buffer.Size);
        Assert.Equal(BufferUsage.Uniform, buffer.Usage);
        Assert.Equal(MemoryIntent.GpuOnly, buffer.Memory);
        Assert.Null(buffer.DebugName);

        Assert.Equal(TextureFormat.Rgba8Unorm, texture.Format);
        Assert.Equal(64, texture.Width);
        Assert.Equal(32, texture.Height);
        Assert.Equal(1, texture.ArrayLayers);
        Assert.Equal(1, texture.MipLevels);
        Assert.Equal(1, texture.SampleCount);
        Assert.Equal(TextureUsage.Sampled, texture.Usage);
        Assert.Null(texture.DebugName);
    }

    [Fact]
    public void DefaultTextureDescription_HasZeroCounts()
    {
        // default(T) skips the constructor, so the initializers don't run: the documented, invalid zero value.
        TextureDescription texture = default;

        Assert.Equal(TextureFormat.Undefined, texture.Format);
        Assert.Equal(0, texture.ArrayLayers);
        Assert.Equal(0, texture.MipLevels);
        Assert.Equal(0, texture.SampleCount);
        Assert.Equal(TextureUsage.None, texture.Usage);
    }
}
