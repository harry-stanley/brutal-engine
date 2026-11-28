using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The blend presets, against the values their documentation and the issue give (WebGPU's usual ones).</summary>
public sealed class BlendStateTests
{
    [Fact]
    public void Presets_MatchDocumentedValues()
    {
        // AlphaBlending: color (SourceAlpha, OneMinusSourceAlpha), alpha (One, OneMinusSourceAlpha).
        AssertComponent(BlendState.AlphaBlending.Color, BlendFactor.SourceAlpha, BlendFactor.OneMinusSourceAlpha);
        AssertComponent(BlendState.AlphaBlending.Alpha, BlendFactor.One, BlendFactor.OneMinusSourceAlpha);

        // PremultipliedAlpha: (One, OneMinusSourceAlpha) for both.
        AssertComponent(BlendState.PremultipliedAlpha.Color, BlendFactor.One, BlendFactor.OneMinusSourceAlpha);
        AssertComponent(BlendState.PremultipliedAlpha.Alpha, BlendFactor.One, BlendFactor.OneMinusSourceAlpha);

        // Additive: color (SourceAlpha, One), alpha (One, One).
        AssertComponent(BlendState.Additive.Color, BlendFactor.SourceAlpha, BlendFactor.One);
        AssertComponent(BlendState.Additive.Alpha, BlendFactor.One, BlendFactor.One);
    }

    [Fact]
    public void BlendComponent_DefaultOperation_IsAdd()
    {
        Assert.Equal(BlendOperation.Add, new BlendComponent(BlendFactor.One, BlendFactor.Zero).Operation);
    }

    private static void AssertComponent(BlendComponent component, BlendFactor source, BlendFactor destination)
    {
        Assert.Equal(source, component.Source);
        Assert.Equal(destination, component.Destination);
        Assert.Equal(BlendOperation.Add, component.Operation);
    }
}
