using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>
/// The pipeline validator's shader module, pipeline layout and compute pipeline rules (docs/design/graphics.md section 7,
/// and the rules settled on #117). Bind group rules are in PipelineValidatorTests.BindGroups.cs.
/// </summary>
public sealed partial class PipelineValidatorTests
{
    private const string DebugName = "probe";

    // The SPIR-V magic number 0x07230203 as little-endian bytes (03 02 23 07), then the version word for SPIR-V 1.6
    // (0x00010600: 00 06 01 00), as the spec's physical layout gives them.
    private static readonly byte[] _spirV = [0x03, 0x02, 0x23, 0x07, 0x00, 0x06, 0x01, 0x00];

    private static readonly ShaderStage _stage = new(new ShaderModuleHandle(new Handle(1, 1)), "main");

    public static TheoryData<string, string?> SpirVRows() => new()
    {
        // Row, then the expected error text after "ShaderModuleDescription.Code[0].Bytes " (null: accepted).
        { "MagicAndVersion", null },
        { "MagicOnly", null },
        { "Empty", "must not be empty" },
        { "OneByte", "must be a whole number of 4-byte SPIR-V words, but is 1 bytes long" },
        { "ThreeBytes", "must be a whole number of 4-byte SPIR-V words, but is 3 bytes long" },
        { "MagicPlusTwoBytes", "must be a whole number of 4-byte SPIR-V words, but is 6 bytes long" },
        { "MagicPlusSevenBytes", "must be a whole number of 4-byte SPIR-V words, but is 11 bytes long" },
        { "BigEndianMagic", "must start with the SPIR-V magic number 0x07230203 (little-endian), but starts with 0x03022307" },
        { "MagicLowByteWrong", "must start with the SPIR-V magic number 0x07230203 (little-endian), but starts with 0x07230204" },
        { "MagicHighByteWrong", "must start with the SPIR-V magic number 0x07230203 (little-endian), but starts with 0x08230203" },
        { "MagicInSecondWord", "must start with the SPIR-V magic number 0x07230203 (little-endian), but starts with 0x00000000" },
    };

    [Theory]
    [MemberData(nameof(SpirVRows))]
    public void SpirV_Structure(string row, string? error)
    {
        byte[] bytes = row switch
        {
            "MagicAndVersion" => _spirV,
            "MagicOnly" => _spirV[..4],
            "Empty" => [],
            "OneByte" => [0x03],
            "ThreeBytes" => _spirV[..3],
            "MagicPlusTwoBytes" => _spirV[..6],
            "MagicPlusSevenBytes" => [.. _spirV, 0x01, 0x02, 0x03],
            "BigEndianMagic" => [0x07, 0x23, 0x02, 0x03],
            "MagicLowByteWrong" => [0x04, 0x02, 0x23, 0x07],
            "MagicHighByteWrong" => [0x03, 0x02, 0x23, 0x08],
            "MagicInSecondWord" => [0x00, 0x00, 0x00, 0x00, 0x03, 0x02, 0x23, 0x07],
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };
        var description = new ShaderModuleDescription { Code = new[] { new ShaderCode(ShaderCodeFormat.SpirV, bytes) }, DebugName = DebugName };

        if (error is null)
        {
            PipelineValidator.Validate(description);
        }
        else
        {
            AssertRejected(typeof(ArgumentException), "ShaderModuleDescription.Code[0].Bytes " + error, () => PipelineValidator.Validate(description));
        }
    }

    [Fact]
    public void ShaderModule_DuplicateFormat_Throws()
    {
        var description = new ShaderModuleDescription
        {
            Code = new[] { new ShaderCode(ShaderCodeFormat.SpirV, _spirV), new ShaderCode(ShaderCodeFormat.SpirV, _spirV) },
            DebugName = DebugName,
        };

        AssertRejected(
            typeof(ArgumentException),
            "ShaderModuleDescription.Code[1].Format must differ from every other entry's, but SpirV is also Code[0]'s",
            () => PipelineValidator.Validate(description));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(1)]
    public void ShaderModule_UndefinedFormat_Throws(int format)
    {
        // The undefined entry comes second, after a valid one, so the message must name its own index.
        var description = new ShaderModuleDescription
        {
            Code = new[] { new ShaderCode(ShaderCodeFormat.SpirV, _spirV), new ShaderCode((ShaderCodeFormat)format, _spirV) },
            DebugName = DebugName,
        };

        AssertRejected(
            typeof(ArgumentOutOfRangeException),
            "ShaderModuleDescription.Code[1].Format isn't a ShaderCodeFormat member",
            () => PipelineValidator.Validate(description));
    }

    [Fact]
    public void ShaderModule_EveryFormatAndNoCode_Accepted()
    {
        foreach (ShaderCodeFormat format in Enum.GetValues<ShaderCodeFormat>())
        {
            PipelineValidator.Validate(new ShaderModuleDescription { Code = new[] { new ShaderCode(format, _spirV) } });
        }

        // No code at all is the device's Unsupported result (no format the backend consumes), not a structural error.
        PipelineValidator.Validate(new ShaderModuleDescription { Code = ReadOnlyMemory<ShaderCode>.Empty });
    }

    public static TheoryData<string, int, Type?, string?> PushConstantRows() => new()
    {
        // Stages, size, then the expected exception and error text after "PipelineLayoutDescription.PushConstants."
        // (null: accepted).
        { "None", 0, null, null },
        { "Vertex", 0, null, null },
        { "Vertex", 4, null, null },
        { "All", 128, null, null },
        { "Fragment", int.MaxValue - 3, null, null },
        { "Vertex", -1, typeof(ArgumentOutOfRangeException), "Size must be 0 or more, but is -1" },
        { "Vertex", -4, typeof(ArgumentOutOfRangeException), "Size must be 0 or more, but is -4" },
        { "Vertex", int.MinValue, typeof(ArgumentOutOfRangeException), "Size must be 0 or more" },
        { "Vertex", 2, typeof(ArgumentException), "Size must be a multiple of 4, but is 2" },
        { "Vertex", 6, typeof(ArgumentException), "Size must be a multiple of 4, but is 6" },
        { "Vertex", int.MaxValue, typeof(ArgumentException), "Size must be a multiple of 4" },
        { "Undefined8", 4, typeof(ArgumentOutOfRangeException), "Stages has flags that aren't ShaderStages members, but is 9" },
        { "SignBit", 0, typeof(ArgumentOutOfRangeException), "Stages has flags that aren't ShaderStages members, but is -2147483648" },
        { "None", 4, typeof(ArgumentException), "Stages must name at least one stage when Size is above 0" },
    };

    [Theory]
    [MemberData(nameof(PushConstantRows))]
    public void Layout_PushConstants(string stages, int size, Type? exception, string? error)
    {
        ShaderStages value = stages switch
        {
            "None" => ShaderStages.None,
            "Vertex" => ShaderStages.Vertex,
            "Fragment" => ShaderStages.Fragment,
            "All" => ShaderStages.Vertex | ShaderStages.Fragment | ShaderStages.Compute,
            "Undefined8" => ShaderStages.Vertex | (ShaderStages)8,
            _ => (ShaderStages)int.MinValue,
        };
        var description = new PipelineLayoutDescription
        {
            BindGroupLayouts = ReadOnlyMemory<BindGroupLayoutHandle>.Empty,
            PushConstants = new PushConstantRange(value, size),
            DebugName = DebugName,
        };

        if (exception is null)
        {
            PipelineValidator.Validate(description);
        }
        else
        {
            AssertRejected(exception, "PipelineLayoutDescription.PushConstants." + error, () => PipelineValidator.Validate(description));
        }
    }

    [Fact]
    public void Layout_EveryStageCombination_Accepted()
    {
        for (int stages = 0; stages <= 7; stages++)
        {
            PipelineValidator.Validate(new PipelineLayoutDescription
            {
                BindGroupLayouts = new[] { new BindGroupLayoutHandle(new Handle(1, 1)) },
                PushConstants = new PushConstantRange((ShaderStages)stages, stages == 0 ? 0 : 16),
            });
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ComputePipeline_EmptyEntryPoint_Throws(string? entryPoint)
    {
        var description = new ComputePipelineDescription
        {
            Layout = new PipelineLayoutHandle(new Handle(2, 1)),
            Compute = _stage with { EntryPoint = entryPoint! },
            DebugName = DebugName,
        };

        AssertRejected(
            typeof(ArgumentException),
            "ComputePipelineDescription.Compute.EntryPoint must not be null or empty",
            () => PipelineValidator.Validate(description));
    }

    [Fact]
    public void ComputePipeline_ValidOrNull()
    {
        PipelineValidator.Validate(new ComputePipelineDescription { Layout = default, Compute = _stage with { EntryPoint = "m" } });

        Assert.Throws<ArgumentNullException>(() => PipelineValidator.Validate((ComputePipelineDescription)null!));
    }

    [Fact]
    public void Rejection_WithoutDebugName_SaysNoDebugName()
    {
        // Both exception kinds go through the same description of the debug name; a null one reads "no debug name".
        var layout = new PipelineLayoutDescription { BindGroupLayouts = ReadOnlyMemory<BindGroupLayoutHandle>.Empty, PushConstants = new(ShaderStages.Vertex, 6) };
        var module = new ShaderModuleDescription { Code = new[] { new ShaderCode((ShaderCodeFormat)1, _spirV) } };

        ArgumentException invalid = Assert.Throws<ArgumentException>(() => PipelineValidator.Validate(layout));
        ArgumentOutOfRangeException outOfRange = Assert.Throws<ArgumentOutOfRangeException>(() => PipelineValidator.Validate(module));

        Assert.Contains("PipelineLayoutDescription.PushConstants.Size must be a multiple of 4, but is 6 (no debug name).", invalid.Message, StringComparison.Ordinal);
        Assert.Contains("ShaderModuleDescription.Code[0].Format isn't a ShaderCodeFormat member, but is 1 (no debug name).", outOfRange.Message, StringComparison.Ordinal);
    }

    private static void AssertRejected(Type exceptionType, string error, Action validate)
    {
        // Exact type: an ArgumentOutOfRangeException where an ArgumentException is documented fails, and vice versa.
        var exception = (ArgumentException)Assert.Throws(exceptionType, validate);

        Assert.Equal("description", exception.ParamName);
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
        Assert.Contains("(debug name 'probe')", exception.Message, StringComparison.Ordinal);
    }
}
