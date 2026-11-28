using System;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The pipeline types whose fields are numbers format them with the invariant culture.</summary>
public sealed class PipelineStateToStringTests
{
    [Fact]
    public void VertexAttribute_ToString_UnderAnotherCulture_IsInvariant()
    {
        string text = CultureProbe.Format(() => new VertexAttribute(-1, VertexFormat.Float32x2, -8).ToString());

        Assert.Equal("VertexAttribute { Location = -1, Format = Float32x2, Offset = -8 }", text);
    }

    [Fact]
    public void VertexBufferLayout_ToString_UnderAnotherCulture_IsInvariant()
    {
        var layout = new VertexBufferLayout(-20, VertexStepMode.Instance, new VertexAttribute[2]);

        string text = CultureProbe.Format(layout.ToString);

        Assert.Equal("VertexBufferLayout { Stride = -20, StepMode = Instance, Attributes = System.ReadOnlyMemory<VertexAttribute>[2] }", text);
    }

    [Fact]
    public void GraphicsPipelineDescription_ToString_UnderAnotherCulture_IsInvariant()
    {
        var pipeline = new GraphicsPipelineDescription
        {
            Layout = new PipelineLayoutHandle(new Handle(1, 2)),
            Vertex = new ShaderStage(new ShaderModuleHandle(new Handle(3, 1)), "vs"),
            Fragment = new ShaderStage(new ShaderModuleHandle(new Handle(3, 1)), "fs"),
            VertexBuffers = new VertexBufferLayout[1],
            Topology = PrimitiveTopology.LineStrip,
            Raster = new RasterState(CullMode.Back, FrontFace.Clockwise),
            Depth = new DepthState(TextureFormat.Depth32Float, Write: false),
            ColorTargets = ReadOnlyMemory<ColorTargetState>.Empty,
            SampleCount = -4,
            DebugName = "sprites",
        };

        string text = CultureProbe.Format(pipeline.ToString);

        Assert.Equal(
            "GraphicsPipelineDescription { Layout = PipelineLayout(1:2), Vertex = ShaderStage { Module = ShaderModule(3:1), EntryPoint = vs }, "
            + "Fragment = ShaderStage { Module = ShaderModule(3:1), EntryPoint = fs }, VertexBuffers = System.ReadOnlyMemory<VertexBufferLayout>[1], "
            + "Topology = LineStrip, Raster = RasterState { CullMode = Back, FrontFace = Clockwise }, "
            + "Depth = DepthState { Format = Depth32Float, Write = False, Compare = LessOrEqual }, "
            + "ColorTargets = System.ReadOnlyMemory<ColorTargetState>[0], SampleCount = -4, DebugName = sprites }",
            text);
    }
}
