using System;
using System.Linq;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The shader module and pipeline layout descriptions (docs/design/graphics.md section 7).</summary>
public sealed class ShaderModuleDescriptionTests
{
    [Fact]
    public void ShaderCodeFormat_Values_MatchDesign()
    {
        Assert.False(typeof(ShaderCodeFormat).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(ShaderCodeFormat)));
        Assert.Equal([("SpirV", 0)], Enum.GetValues<ShaderCodeFormat>().Select(v => (v.ToString(), (int)v)));
    }

    [Fact]
    public void Defaults_MatchDesign()
    {
        var module = new ShaderModuleDescription { Code = new[] { new ShaderCode(ShaderCodeFormat.SpirV, new byte[4]) } };
        var layout = new PipelineLayoutDescription { BindGroupLayouts = ReadOnlyMemory<BindGroupLayoutHandle>.Empty };

        Assert.Null(module.DebugName);
        Assert.Equal(new PushConstantRange(ShaderStages.None, 0), layout.PushConstants);
        Assert.Null(layout.DebugName);
    }

    [Fact]
    public void ShaderCode_Equality_ComparesBytesByReference()
    {
        byte[] bytes = [3, 2, 35, 7];

        Assert.Equal(new ShaderCode(ShaderCodeFormat.SpirV, bytes), new ShaderCode(ShaderCodeFormat.SpirV, bytes));
        Assert.NotEqual(new ShaderCode(ShaderCodeFormat.SpirV, bytes), new ShaderCode(ShaderCodeFormat.SpirV, (byte[])bytes.Clone()));
    }

    [Fact]
    public void PushConstantRange_ToString_UnderAnotherCulture_IsInvariant()
    {
        string text = CultureProbe.Format(() => new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, -12).ToString());

        Assert.Equal("PushConstantRange { Stages = Vertex, Fragment, Size = -12 }", text);
    }

    [Fact]
    public void PipelineLayoutDescription_ToString_UnderAnotherCulture_IsInvariant()
    {
        var layout = new PipelineLayoutDescription
        {
            BindGroupLayouts = new[] { new BindGroupLayoutHandle(new Handle(1, 2)) },
            PushConstants = new(ShaderStages.Compute, -4),
            DebugName = "sprites",
        };

        string text = CultureProbe.Format(layout.ToString);

        Assert.Equal(
            "PipelineLayoutDescription { BindGroupLayouts = System.ReadOnlyMemory<BindGroupLayoutHandle>[1], PushConstants = PushConstantRange { Stages = Compute, Size = -4 }, DebugName = sprites }",
            text);
    }
}
