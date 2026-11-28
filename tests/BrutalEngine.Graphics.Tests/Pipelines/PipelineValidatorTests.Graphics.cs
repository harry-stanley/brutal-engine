using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>The pipeline validator's graphics pipeline rules (docs/design/graphics.md section 7, and the rules settled on #117).</summary>
public sealed partial class PipelineValidatorTests
{
    private static readonly ColorTargetState _target = new(TextureFormat.Rgba8Unorm, BlendState.AlphaBlending);

    // A valid pipeline: one 20-byte vertex buffer (a Float32x3 position at 0, a Float32x2 UV at 12) and one blended target.
    private static readonly GraphicsPipelineDescription _pipeline = new()
    {
        Layout = new PipelineLayoutHandle(new Handle(2, 1)),
        Vertex = _stage,
        Fragment = _stage,
        VertexBuffers = new[] { new VertexBufferLayout(20, VertexStepMode.Vertex, new VertexAttribute[] { new(0, VertexFormat.Float32x3, 0), new(1, VertexFormat.Float32x2, 12) }) },
        ColorTargets = new[] { _target },
        DebugName = DebugName,
    };

    public static TheoryData<string> InvalidGraphicsPipelines() =>
    [
        "VertexEntryNull", "VertexEntryEmpty", "FragmentEntryEmpty", "TopologyNegative", "TopologyAboveLast", "CullModeNegative",
        "CullModeAboveLast", "FrontFaceNegative", "FrontFaceAboveLast", "SamplesZero", "SamplesThree", "SamplesSix", "SamplesThirtyTwo",
        "SamplesNegative", "SamplesMinValue", "NoTargetsNoDepth", "DepthFormatUndefined", "DepthFormatColor", "DepthFormatNegative",
        "DepthFormatAboveLast", "DepthCompareNegative", "DepthCompareAboveLast", "TargetFormatUndefined", "TargetFormatDepth16",
        "TargetFormatDepth32", "TargetFormatNegative", "TargetFormatAboveLast", "WriteMaskUndefined", "WriteMaskSignBit",
        "ColorSourceNegative", "ColorSourceAboveLast", "ColorDestinationNegative", "ColorDestinationAboveLast", "ColorOperationNegative",
        "ColorOperationAboveLast", "AlphaSourceNegative", "AlphaSourceAboveLast", "AlphaDestinationNegative", "AlphaDestinationAboveLast",
        "AlphaOperationNegative", "AlphaOperationAboveLast", "StrideNegative", "StepModeNegative", "StepModeAboveLast", "LocationNegative",
        "OffsetNegative", "OffsetMinValue", "FormatNegative", "FormatAboveLast", "EndsPastStride", "EndsPastZeroStride", "EndsPastHugeOffset",
        "LocationRepeatedInBuffer", "LocationRepeatedAcrossBuffers",
    ];

    [Theory]
    [MemberData(nameof(InvalidGraphicsPipelines))]
    public void GraphicsPipeline_Invalid_Throws(string row)
    {
        Type outOfRange = typeof(ArgumentOutOfRangeException);
        Type invalid = typeof(ArgumentException);
        BlendComponent add = new(BlendFactor.One, BlendFactor.Zero);
        (GraphicsPipelineDescription description, Type exception, string error) = row switch
        {
            "VertexEntryNull" => (_pipeline with { Vertex = _stage with { EntryPoint = null! } }, invalid, "Vertex.EntryPoint must not be null or empty"),
            "VertexEntryEmpty" => (_pipeline with { Vertex = _stage with { EntryPoint = "" } }, invalid, "Vertex.EntryPoint must not be null or empty"),
            "FragmentEntryEmpty" => (_pipeline with { Fragment = _stage with { EntryPoint = "" } }, invalid, "Fragment.EntryPoint must not be null or empty"),
            "TopologyNegative" => (_pipeline with { Topology = (PrimitiveTopology)(-1) }, outOfRange, "Topology isn't a PrimitiveTopology member, but is -1"),
            "TopologyAboveLast" => (_pipeline with { Topology = (PrimitiveTopology)4 }, outOfRange, "Topology isn't a PrimitiveTopology member, but is 4"),
            "CullModeNegative" => (_pipeline with { Raster = new((CullMode)(-1)) }, outOfRange, "Raster.CullMode isn't a CullMode member, but is -1"),
            "CullModeAboveLast" => (_pipeline with { Raster = new((CullMode)3) }, outOfRange, "Raster.CullMode isn't a CullMode member, but is 3"),
            "FrontFaceNegative" => (_pipeline with { Raster = new(CullMode.Back, (FrontFace)(-1)) }, outOfRange, "Raster.FrontFace isn't a FrontFace member, but is -1"),
            "FrontFaceAboveLast" => (_pipeline with { Raster = new(CullMode.Back, (FrontFace)2) }, outOfRange, "Raster.FrontFace isn't a FrontFace member, but is 2"),
            "SamplesZero" => (_pipeline with { SampleCount = 0 }, outOfRange, "SampleCount must be 1, 2, 4, 8 or 16, but is 0"),
            "SamplesThree" => (_pipeline with { SampleCount = 3 }, outOfRange, "SampleCount must be 1, 2, 4, 8 or 16, but is 3"),
            "SamplesSix" => (_pipeline with { SampleCount = 6 }, outOfRange, "SampleCount must be 1, 2, 4, 8 or 16, but is 6"),
            "SamplesThirtyTwo" => (_pipeline with { SampleCount = 32 }, outOfRange, "SampleCount must be 1, 2, 4, 8 or 16, but is 32"),
            "SamplesNegative" => (_pipeline with { SampleCount = -1 }, outOfRange, "SampleCount must be 1, 2, 4, 8 or 16, but is -1"),
            "SamplesMinValue" => (_pipeline with { SampleCount = int.MinValue }, outOfRange, "SampleCount must be 1, 2, 4, 8 or 16, but is -2147483648"),
            "NoTargetsNoDepth" => (_pipeline with { ColorTargets = ReadOnlyMemory<ColorTargetState>.Empty }, invalid, "ColorTargets must not be empty when there is no Depth state"),
            "DepthFormatUndefined" => (WithDepth(TextureFormat.Undefined), invalid, "Depth.Format must be a depth format, but is Undefined"),
            "DepthFormatColor" => (WithDepth(TextureFormat.Rgba8Unorm), invalid, "Depth.Format must be a depth format, but is Rgba8Unorm"),
            "DepthFormatNegative" => (WithDepth((TextureFormat)(-1)), outOfRange, "Depth.Format isn't a TextureFormat member, but is -1"),
            "DepthFormatAboveLast" => (WithDepth((TextureFormat)17), outOfRange, "Depth.Format isn't a TextureFormat member, but is 17"),
            "DepthCompareNegative" => (WithDepth(TextureFormat.Depth32Float, (CompareFunction)(-1)), outOfRange, "Depth.Compare isn't a CompareFunction member, but is -1"),
            "DepthCompareAboveLast" => (WithDepth(TextureFormat.Depth32Float, (CompareFunction)8), outOfRange, "Depth.Compare isn't a CompareFunction member, but is 8"),
            "TargetFormatUndefined" => (WithTarget(_target with { Format = TextureFormat.Undefined }), invalid, "ColorTargets[1].Format must be a color format, but is Undefined"),
            "TargetFormatDepth16" => (WithTarget(_target with { Format = TextureFormat.Depth16Unorm }), invalid, "ColorTargets[1].Format must be a color format, but is Depth16Unorm"),
            "TargetFormatDepth32" => (WithTarget(_target with { Format = TextureFormat.Depth32Float }), invalid, "ColorTargets[1].Format must be a color format, but is Depth32Float"),
            "TargetFormatNegative" => (WithTarget(_target with { Format = (TextureFormat)(-1) }), outOfRange, "ColorTargets[1].Format isn't a TextureFormat member, but is -1"),
            "TargetFormatAboveLast" => (WithTarget(_target with { Format = (TextureFormat)17 }), outOfRange, "ColorTargets[1].Format isn't a TextureFormat member, but is 17"),
            "WriteMaskUndefined" => (WithTarget(_target with { WriteMask = ColorWriteMask.Red | (ColorWriteMask)16 }), outOfRange, "ColorTargets[1].WriteMask has flags that aren't ColorWriteMask members, but is 17"),
            "WriteMaskSignBit" => (WithTarget(_target with { WriteMask = (ColorWriteMask)int.MinValue }), outOfRange, "ColorTargets[1].WriteMask has flags that aren't ColorWriteMask members, but is -2147483648"),
            "ColorSourceNegative" => (WithBlend(add with { Source = (BlendFactor)(-1) }, add), outOfRange, "ColorTargets[1].Blend.Color.Source isn't a BlendFactor member, but is -1"),
            "ColorSourceAboveLast" => (WithBlend(add with { Source = (BlendFactor)10 }, add), outOfRange, "ColorTargets[1].Blend.Color.Source isn't a BlendFactor member, but is 10"),
            "ColorDestinationNegative" => (WithBlend(add with { Destination = (BlendFactor)(-1) }, add), outOfRange, "ColorTargets[1].Blend.Color.Destination isn't a BlendFactor member, but is -1"),
            "ColorDestinationAboveLast" => (WithBlend(add with { Destination = (BlendFactor)10 }, add), outOfRange, "ColorTargets[1].Blend.Color.Destination isn't a BlendFactor member, but is 10"),
            "ColorOperationNegative" => (WithBlend(add with { Operation = (BlendOperation)(-1) }, add), outOfRange, "ColorTargets[1].Blend.Color.Operation isn't a BlendOperation member, but is -1"),
            "ColorOperationAboveLast" => (WithBlend(add with { Operation = (BlendOperation)5 }, add), outOfRange, "ColorTargets[1].Blend.Color.Operation isn't a BlendOperation member, but is 5"),
            "AlphaSourceNegative" => (WithBlend(add, add with { Source = (BlendFactor)(-1) }), outOfRange, "ColorTargets[1].Blend.Alpha.Source isn't a BlendFactor member, but is -1"),
            "AlphaSourceAboveLast" => (WithBlend(add, add with { Source = (BlendFactor)10 }), outOfRange, "ColorTargets[1].Blend.Alpha.Source isn't a BlendFactor member, but is 10"),
            "AlphaDestinationNegative" => (WithBlend(add, add with { Destination = (BlendFactor)(-1) }), outOfRange, "ColorTargets[1].Blend.Alpha.Destination isn't a BlendFactor member, but is -1"),
            "AlphaDestinationAboveLast" => (WithBlend(add, add with { Destination = (BlendFactor)10 }), outOfRange, "ColorTargets[1].Blend.Alpha.Destination isn't a BlendFactor member, but is 10"),
            "AlphaOperationNegative" => (WithBlend(add, add with { Operation = (BlendOperation)(-1) }), outOfRange, "ColorTargets[1].Blend.Alpha.Operation isn't a BlendOperation member, but is -1"),
            "AlphaOperationAboveLast" => (WithBlend(add, add with { Operation = (BlendOperation)5 }), outOfRange, "ColorTargets[1].Blend.Alpha.Operation isn't a BlendOperation member, but is 5"),
            "StrideNegative" => (WithBuffer(new(-1, VertexStepMode.Vertex, ReadOnlyMemory<VertexAttribute>.Empty)), outOfRange, "VertexBuffers[1].Stride must be 0 or more, but is -1"),
            "StepModeNegative" => (WithBuffer(new(8, (VertexStepMode)(-1), ReadOnlyMemory<VertexAttribute>.Empty)), outOfRange, "VertexBuffers[1].StepMode isn't a VertexStepMode member, but is -1"),
            "StepModeAboveLast" => (WithBuffer(new(8, (VertexStepMode)2, ReadOnlyMemory<VertexAttribute>.Empty)), outOfRange, "VertexBuffers[1].StepMode isn't a VertexStepMode member, but is 2"),
            "LocationNegative" => (WithAttribute(new(-1, VertexFormat.Float32, 0)), outOfRange, "VertexBuffers[1].Attributes[1].Location must be 0 or more, but is -1"),
            "OffsetNegative" => (WithAttribute(new(5, VertexFormat.Float32, -1)), outOfRange, "VertexBuffers[1].Attributes[1].Offset must be 0 or more, but is -1"),
            "OffsetMinValue" => (WithAttribute(new(5, VertexFormat.Float32, int.MinValue)), outOfRange, "VertexBuffers[1].Attributes[1].Offset must be 0 or more, but is -2147483648"),
            "FormatNegative" => (WithAttribute(new(5, (VertexFormat)(-1), 0)), outOfRange, "VertexBuffers[1].Attributes[1].Format isn't a VertexFormat member, but is -1"),
            "FormatAboveLast" => (WithAttribute(new(5, (VertexFormat)11, 0)), outOfRange, "VertexBuffers[1].Attributes[1].Format isn't a VertexFormat member, but is 11"),
            "EndsPastStride" => (WithAttribute(new(5, VertexFormat.Float32x3, 8)), outOfRange, "VertexBuffers[1].Attributes[1].Offset must leave room for its Float32x3 (12 bytes) inside the buffer's Stride (16), but is 8"),
            "EndsPastZeroStride" => (WithBuffer(new(0, VertexStepMode.Instance, new VertexAttribute[] { new(5, VertexFormat.Uint8x4, 0) })), outOfRange, "VertexBuffers[1].Attributes[0].Offset must leave room for its Uint8x4 (4 bytes) inside the buffer's Stride (0), but is 0"),
            "EndsPastHugeOffset" => (WithAttribute(new(5, VertexFormat.Float32x4, int.MaxValue)), outOfRange, "VertexBuffers[1].Attributes[1].Offset must leave room for its Float32x4 (16 bytes) inside the buffer's Stride (16), but is 2147483647"),
            "LocationRepeatedInBuffer" => (WithAttribute(new(4, VertexFormat.Float32, 4)), invalid, "VertexBuffers[1].Attributes[1].Location must be unique across the vertex buffers, but 4 is also VertexBuffers[1].Attributes[0]'s"),
            "LocationRepeatedAcrossBuffers" => (WithAttribute(new(1, VertexFormat.Float32, 4)), invalid, "VertexBuffers[1].Attributes[1].Location must be unique across the vertex buffers, but 1 is also VertexBuffers[0].Attributes[1]'s"),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        AssertRejected(exception, "GraphicsPipelineDescription." + error, () => PipelineValidator.Validate(description));
    }

    [Fact]
    public void GraphicsPipeline_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PipelineValidator.Validate((GraphicsPipelineDescription)null!));
    }

    [Fact]
    public void GraphicsPipeline_Valid_Accepted()
    {
        PipelineValidator.Validate(_pipeline);
        PipelineValidator.Validate(_pipeline with { Fragment = null, VertexBuffers = ReadOnlyMemory<VertexBufferLayout>.Empty });
        foreach (int samples in new[] { 1, 2, 4, 8, 16 })
        {
            PipelineValidator.Validate(_pipeline with { SampleCount = samples });
        }

        // Depth with no color target, in both depth formats; and the offset that ends exactly at the stride (8 + 12 = 20).
        PipelineValidator.Validate(WithDepth(TextureFormat.Depth16Unorm) with { ColorTargets = ReadOnlyMemory<ColorTargetState>.Empty });
        PipelineValidator.Validate(WithDepth(TextureFormat.Depth32Float) with { ColorTargets = ReadOnlyMemory<ColorTargetState>.Empty });
        PipelineValidator.Validate(WithBuffer(new(20, VertexStepMode.Instance, new VertexAttribute[] { new(int.MaxValue, VertexFormat.Float32x3, 8) })));
        PipelineValidator.Validate(WithBuffer(new(0, VertexStepMode.Vertex, ReadOnlyMemory<VertexAttribute>.Empty)));
        PipelineValidator.Validate(WithTarget(_target with { Blend = null, WriteMask = ColorWriteMask.None }));
        for (int mask = 0; mask <= 15; mask++)
        {
            PipelineValidator.Validate(WithTarget(_target with { WriteMask = (ColorWriteMask)mask }));
        }
    }

    [Fact]
    public void GraphicsPipeline_EveryDefinedValue_Accepted()
    {
        foreach (PrimitiveTopology topology in Enum.GetValues<PrimitiveTopology>())
        {
            PipelineValidator.Validate(_pipeline with { Topology = topology });
        }

        foreach (CullMode cull in Enum.GetValues<CullMode>())
        {
            foreach (FrontFace front in Enum.GetValues<FrontFace>())
            {
                PipelineValidator.Validate(_pipeline with { Raster = new(cull, front) });
            }
        }

        foreach (CompareFunction compare in Enum.GetValues<CompareFunction>())
        {
            PipelineValidator.Validate(WithDepth(TextureFormat.Depth32Float, compare));
        }

        foreach (TextureFormat format in Enum.GetValues<TextureFormat>())
        {
            if (format is not (TextureFormat.Undefined or TextureFormat.Depth16Unorm or TextureFormat.Depth32Float))
            {
                PipelineValidator.Validate(WithTarget(_target with { Format = format }));
            }
        }

        foreach (BlendFactor factor in Enum.GetValues<BlendFactor>())
        {
            foreach (BlendOperation operation in Enum.GetValues<BlendOperation>())
            {
                BlendComponent component = new(factor, factor, operation);
                PipelineValidator.Validate(WithBlend(component, component));
            }
        }

        foreach (VertexStepMode step in Enum.GetValues<VertexStepMode>())
        {
            foreach (VertexFormat format in Enum.GetValues<VertexFormat>())
            {
                PipelineValidator.Validate(WithBuffer(new(16, step, new VertexAttribute[] { new(7, format, 0) })));
            }
        }
    }

    [Fact]
    public void VertexFormatSize_MatchesDocumentedSizes()
    {
        // The sizes each VertexFormat member's documentation gives (components times component bytes).
        (VertexFormat Format, int Size)[] expected =
        [
            (VertexFormat.Float32, 4), (VertexFormat.Float32x2, 8), (VertexFormat.Float32x3, 12), (VertexFormat.Float32x4, 16),
            (VertexFormat.Float16x2, 4), (VertexFormat.Float16x4, 8), (VertexFormat.Unorm8x4, 4), (VertexFormat.Uint8x4, 4),
            (VertexFormat.Uint32, 4), (VertexFormat.Uint32x2, 8), (VertexFormat.Sint32, 4),
        ];

        Assert.Equal(Enum.GetValues<VertexFormat>().Length, expected.Length);
        foreach ((VertexFormat format, int size) in expected)
        {
            Assert.Equal(size, PipelineValidator.VertexFormatSize(format));
        }
    }

    [Fact]
    public void VertexLayout_MatchesReference()
    {
        // Small ranges around every boundary: strides and offsets near the 4-16 byte attribute sizes (and -1), locations
        // 0-3 (often repeated, sometimes -1), formats -1 to 11 (one below and one above the defined 0-10), step modes
        // -1 to 2. Up to three buffers of up to four attributes.
        var attribute = Gen.Select(
            Gen.Frequency((8, Gen.Int[0, 3]), (1, Gen.Const(-1))),
            Gen.Frequency((10, Gen.Int[0, 10]), (1, Gen.Const(-1)), (1, Gen.Const(11))),
            Gen.Frequency((8, Gen.Int[0, 20]), (1, Gen.Int[-2, -1])),
            (location, format, offset) => new VertexAttribute(location, (VertexFormat)format, offset));
        var buffer = Gen.Select(
            Gen.Frequency((8, Gen.Int[0, 32]), (1, Gen.Const(-1))),
            Gen.Frequency((8, Gen.Int[0, 1]), (1, Gen.Const(-1)), (1, Gen.Const(2))),
            attribute.Array[0, 4],
            (stride, step, attributes) => new VertexBufferLayout(stride, (VertexStepMode)step, attributes));

        buffer.Array[0, 3].Sample(
            buffers =>
            {
                GraphicsPipelineDescription description = _pipeline with { VertexBuffers = buffers };
                string? expected = ReferenceVertexError(buffers);
                if (expected is null)
                {
                    PipelineValidator.Validate(description);
                }
                else
                {
                    ArgumentException e = Assert.ThrowsAny<ArgumentException>(() => PipelineValidator.Validate(description));
                    Assert.StartsWith("GraphicsPipelineDescription." + expected, e.Message, StringComparison.Ordinal);
                }
            },
            iter: 20_000);
    }

    // An independent model of the vertex rules: the documented sizes, then the first violation in the documented order
    // (each buffer's stride and step mode, then each attribute's location, offset, format, extent and uniqueness), as
    // the field path and the start of its requirement. Uniqueness is tracked with a set, not a rescan.
    private static string? ReferenceVertexError(VertexBufferLayout[] buffers)
    {
        int[] sizes = [4, 8, 12, 16, 4, 8, 4, 4, 4, 8, 4];
        var seen = new HashSet<int>();
        for (int b = 0; b < buffers.Length; b++)
        {
            VertexBufferLayout buffer = buffers[b];
            string at = $"VertexBuffers[{b}].";
            if (buffer.Stride < 0)
            {
                return at + "Stride must be 0 or more";
            }

            if ((int)buffer.StepMode is < 0 or > 1)
            {
                return at + "StepMode isn't";
            }

            VertexAttribute[] attributes = buffer.Attributes.ToArray();
            for (int a = 0; a < attributes.Length; a++)
            {
                VertexAttribute v = attributes[a];
                string attr = $"{at}Attributes[{a}].";
                if (v.Location < 0)
                {
                    return attr + "Location must be 0 or more";
                }

                if (v.Offset < 0)
                {
                    return attr + "Offset must be 0 or more";
                }

                if ((int)v.Format is < 0 or > 10)
                {
                    return attr + "Format isn't";
                }

                if (v.Offset + sizes[(int)v.Format] > buffer.Stride)
                {
                    return attr + "Offset must leave room";
                }

                if (!seen.Add(v.Location))
                {
                    return attr + "Location must be unique";
                }
            }
        }

        return null;
    }

    private static GraphicsPipelineDescription WithDepth(TextureFormat format, CompareFunction compare = CompareFunction.LessOrEqual) =>
        _pipeline with { Depth = new DepthState(format, Write: true, compare) };

    // The broken target comes second, after a valid one, so the message must name its own index.
    private static GraphicsPipelineDescription WithTarget(ColorTargetState target) => _pipeline with { ColorTargets = new[] { _target, target } };

    private static GraphicsPipelineDescription WithBlend(BlendComponent color, BlendComponent alpha) =>
        WithTarget(_target with { Blend = new BlendState(color, alpha) });

    // The buffer comes second, after the valid 20-byte one (locations 0 and 1).
    private static GraphicsPipelineDescription WithBuffer(VertexBufferLayout buffer) =>
        _pipeline with { VertexBuffers = new[] { _pipeline.VertexBuffers.Span[0], buffer } };

    // A second 16-byte buffer whose first attribute (location 4, Float32 at 0) is valid and whose second is the one given.
    private static GraphicsPipelineDescription WithAttribute(VertexAttribute attribute) =>
        WithBuffer(new(16, VertexStepMode.Instance, new VertexAttribute[] { new(4, VertexFormat.Float32, 0), attribute }));
}
