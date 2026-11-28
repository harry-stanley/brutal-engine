using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The defaults of the pipeline descriptions and state types (docs/design/graphics.md section 7).</summary>
public sealed class PipelineDescriptionDefaultsTests
{
    private static readonly PipelineLayoutHandle _layout = new(new Handle(1, 1));
    private static readonly ShaderStage _stage = new(new ShaderModuleHandle(new Handle(2, 1)), "main");

    [Fact]
    public void GraphicsPipelineDescription_Defaults_MatchDesign()
    {
        var pipeline = new GraphicsPipelineDescription { Layout = _layout, Vertex = _stage, ColorTargets = new[] { new ColorTargetState(TextureFormat.Rgba8Unorm, null) } };

        Assert.Null(pipeline.Fragment);
        Assert.True(pipeline.VertexBuffers.IsEmpty);
        Assert.Equal(PrimitiveTopology.TriangleList, pipeline.Topology);
        Assert.Equal(new RasterState(CullMode.None, FrontFace.CounterClockwise), pipeline.Raster);
        Assert.Null(pipeline.Depth);
        Assert.Equal(1, pipeline.SampleCount);
        Assert.Null(pipeline.DebugName);
    }

    [Fact]
    public void ComputePipelineDescription_Defaults_MatchDesign()
    {
        var pipeline = new ComputePipelineDescription { Layout = _layout, Compute = _stage };

        Assert.Equal(_layout, pipeline.Layout);
        Assert.Equal(_stage, pipeline.Compute);
        Assert.Null(pipeline.DebugName);
    }

    [Fact]
    public void StateTypes_Defaults_MatchDesign()
    {
        Assert.Equal(ColorWriteMask.All, new ColorTargetState(TextureFormat.Bgra8Unorm, BlendState.AlphaBlending).WriteMask);
        Assert.Equal(CompareFunction.LessOrEqual, new DepthState(TextureFormat.Depth32Float, Write: true).Compare);
        Assert.Equal(CullMode.None, new RasterState().CullMode);
        Assert.Equal(FrontFace.CounterClockwise, new RasterState().FrontFace);
        Assert.Equal(default, new RasterState());
    }
}
