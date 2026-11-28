using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>
/// A valid description passes the pipeline validator without allocating, so a frame bind group can be checked in the
/// frame loop. Every Validate overload in this part runs inside the measured window, each over a description that
/// reaches all of its loops. The project turns background garbage collection off, which AllocationMeasurementTests
/// guards, so the counts are exact.
/// </summary>
public sealed class PipelineValidatorAllocationTests
{
    private const int Iterations = 1_000;

    [Fact]
    public void ValidDescriptions_DoNotAllocate()
    {
        byte[] spirV = [0x03, 0x02, 0x23, 0x07, 0x00, 0x06, 0x01, 0x00];
        var module = new ShaderModuleDescription { Code = new[] { new ShaderCode(ShaderCodeFormat.SpirV, spirV) }, DebugName = "m" };
        var layout = new PipelineLayoutDescription
        {
            BindGroupLayouts = new[] { new BindGroupLayoutHandle(new Handle(1, 1)) },
            PushConstants = new PushConstantRange(ShaderStages.Vertex, 64),
        };
        var compute = new ComputePipelineDescription { Layout = default, Compute = new ShaderStage(default, "main") };
        var groupLayout = new BindGroupLayoutDescription
        {
            Entries = new[]
            {
                new BindGroupLayoutEntry(0, ShaderStages.Vertex, BindingType.UniformBuffer),
                new BindGroupLayoutEntry(1, ShaderStages.Fragment, BindingType.SampledTexture, 2),
                new BindGroupLayoutEntry(2, ShaderStages.Fragment, BindingType.Sampler),
            },
        };
        var buffer = new BufferHandle(new Handle(2, 1));
        var view = new TextureViewHandle(new Handle(3, 1));
        var sampler = new SamplerHandle(new Handle(4, 1));

        Exercise(module, layout, compute, groupLayout, buffer, view, sampler);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            Exercise(module, layout, compute, groupLayout, buffer, view, sampler);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Exercise(
        in ShaderModuleDescription module,
        in PipelineLayoutDescription layout,
        ComputePipelineDescription compute,
        in BindGroupLayoutDescription groupLayout,
        BufferHandle buffer,
        TextureViewHandle view,
        SamplerHandle sampler)
    {
        // One entry of every kind, plus a second array element, so the null and duplicate loops visit each kind.
        Span<BindGroupEntry> entries =
        [
            BindGroupEntry.FromBuffer(0, new BufferBinding(buffer, 0, 64)),
            BindGroupEntry.FromTexture(1, view, 0),
            BindGroupEntry.FromTexture(1, view, 1),
            BindGroupEntry.FromSampler(2, sampler),
        ];
        PipelineValidator.Validate(module);
        PipelineValidator.Validate(layout);
        PipelineValidator.Validate(compute);
        PipelineValidator.Validate(groupLayout);
        PipelineValidator.Validate(new BindGroupDescription { Layout = default, Entries = entries, Lifetime = BindGroupLifetime.Frame });
    }
}
