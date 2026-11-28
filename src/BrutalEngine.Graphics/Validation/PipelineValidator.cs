using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace BrutalEngine.Graphics.Validation;

/// <summary>
/// The front end's pure checks of shader, pipeline and bind group descriptions: every rule that docs/design/graphics.md
/// sections 7 and 8 make an exception and that the description alone determines, so the device only calls these before
/// handing a description to the backend.
/// </summary>
/// <remarks>
/// <para>
/// A structurally invalid description throws <see cref="ArgumentException"/>, or
/// <see cref="ArgumentOutOfRangeException"/> for a value out of its range (including a value outside its enum's
/// members). Rules that need live objects or the device (layout compatibility, resource usage, offsets and sizes against
/// limits, matching bind group entries to their layout) are the device's, not checked here. Every exception's
/// <see cref="ArgumentException.ParamName"/> is <c>description</c>, and its message names the field (with the index of
/// a list entry) and the debug name.
/// </para>
/// <para>
/// Stateless and safe to call from any thread. A valid description allocates nothing, so a frame bind group can be
/// checked in the frame loop; only the failure paths build messages.
/// </para>
/// </remarks>
internal static class PipelineValidator
{
    /// <summary>The first word of every SPIR-V module, read little-endian (the byte order of every supported target).</summary>
    internal const uint SpirVMagic = 0x07230203;

    private const ShaderStages AllStages = ShaderStages.Vertex | ShaderStages.Fragment | ShaderStages.Compute;

    /// <summary>Checks a shader module description: each code entry's format, no format twice, and SPIR-V's structure.</summary>
    /// <param name="description">The description to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">A code entry's format isn't a <see cref="ShaderCodeFormat"/> member.</exception>
    /// <exception cref="ArgumentException">
    /// Two code entries have the same format, or SPIR-V code is empty, isn't a whole number of 4-byte words, or doesn't
    /// start with the magic number <c>0x07230203</c> read little-endian.
    /// </exception>
    public static void Validate(in ShaderModuleDescription description)
    {
        const string Type = nameof(ShaderModuleDescription);
        const string Code = nameof(ShaderModuleDescription.Code);
        string? name = description.DebugName;
        ReadOnlySpan<ShaderCode> code = description.Code.Span;
        for (int i = 0; i < code.Length; i++)
        {
            ShaderCodeFormat format = code[i].Format;
            if ((uint)format > (uint)ShaderCodeFormat.SpirV)
            {
                ThrowOutOfRange(nameof(description), Type, Code, i, nameof(ShaderCode.Format), "isn't a ShaderCodeFormat member", (long)format, name);
            }

            for (int j = 0; j < i; j++)
            {
                if (code[j].Format == format)
                {
                    ThrowInvalid(nameof(description), Type, Code, i, nameof(ShaderCode.Format), string.Create(CultureInfo.InvariantCulture, $"must differ from every other entry's, but {format} is also Code[{j}]'s"), name);
                }
            }

            // Every ShaderCodeFormat member is SPIR-V today, so every entry gets SPIR-V's structural check; a later format
            // adds its own check here.
            ReadOnlySpan<byte> bytes = code[i].Bytes.Span;
            if (bytes.IsEmpty)
            {
                ThrowInvalid(nameof(description), Type, Code, i, nameof(ShaderCode.Bytes), "must not be empty", name);
            }

            if (bytes.Length % sizeof(uint) != 0)
            {
                ThrowInvalid(nameof(description), Type, Code, i, nameof(ShaderCode.Bytes), string.Create(CultureInfo.InvariantCulture, $"must be a whole number of 4-byte SPIR-V words, but is {bytes.Length} bytes long"), name);
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (magic != SpirVMagic)
            {
                ThrowInvalid(nameof(description), Type, Code, i, nameof(ShaderCode.Bytes), string.Create(CultureInfo.InvariantCulture, $"must start with the SPIR-V magic number 0x07230203 (little-endian), but starts with 0x{magic:X8}"), name);
            }
        }
    }

    /// <summary>Checks a pipeline layout description's push-constant range.</summary>
    /// <param name="description">The description to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The push-constant size is negative, or its stages have flags that aren't <see cref="ShaderStages"/> members.
    /// </exception>
    /// <exception cref="ArgumentException">The size isn't a multiple of 4, or is above 0 with no stages.</exception>
    public static void Validate(in PipelineLayoutDescription description)
    {
        const string Type = nameof(PipelineLayoutDescription);
        const string Field = nameof(PipelineLayoutDescription.PushConstants);
        string? name = description.DebugName;
        PushConstantRange range = description.PushConstants;
        if (range.Size < 0)
        {
            ThrowOutOfRange(nameof(description), Type, Field, -1, nameof(PushConstantRange.Size), "must be 0 or more", range.Size, name);
        }

        if (range.Size % 4 != 0)
        {
            ThrowInvalid(nameof(description), Type, Field, -1, nameof(PushConstantRange.Size), string.Create(CultureInfo.InvariantCulture, $"must be a multiple of 4, but is {range.Size}"), name);
        }

        if ((range.Stages & ~AllStages) != 0)
        {
            ThrowOutOfRange(nameof(description), Type, Field, -1, nameof(PushConstantRange.Stages), "has flags that aren't ShaderStages members", (long)range.Stages, name);
        }

        if (range.Size > 0 && range.Stages == ShaderStages.None)
        {
            ThrowInvalid(nameof(description), Type, Field, -1, nameof(PushConstantRange.Stages), "must name at least one stage when Size is above 0", name);
        }
    }

    /// <summary>Checks a compute pipeline description: its entry point names a function.</summary>
    /// <param name="description">The description to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The compute stage's entry point is <see langword="null"/> or empty.</exception>
    public static void Validate(ComputePipelineDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        CheckEntryPoint(nameof(description), nameof(ComputePipelineDescription), nameof(ComputePipelineDescription.Compute), description.Compute, description.DebugName);
    }

    /// <summary>Checks a bind group layout description: each binding's number, values and count, and no number twice.</summary>
    /// <param name="description">The description to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A binding number is negative, a count is below 1, a visibility has flags that aren't <see cref="ShaderStages"/>
    /// members, or a type isn't a <see cref="BindingType"/> member.
    /// </exception>
    /// <exception cref="ArgumentException">Two entries have the same binding number.</exception>
    public static void Validate(in BindGroupLayoutDescription description)
    {
        const string Type = nameof(BindGroupLayoutDescription);
        const string Entries = nameof(BindGroupLayoutDescription.Entries);
        string? name = description.DebugName;
        ReadOnlySpan<BindGroupLayoutEntry> entries = description.Entries.Span;
        for (int i = 0; i < entries.Length; i++)
        {
            BindGroupLayoutEntry entry = entries[i];
            if (entry.Binding < 0)
            {
                ThrowOutOfRange(nameof(description), Type, Entries, i, nameof(BindGroupLayoutEntry.Binding), "must be 0 or more", entry.Binding, name);
            }

            if (entry.Count < 1)
            {
                ThrowOutOfRange(nameof(description), Type, Entries, i, nameof(BindGroupLayoutEntry.Count), "must be at least 1", entry.Count, name);
            }

            if ((entry.Visibility & ~AllStages) != 0)
            {
                ThrowOutOfRange(nameof(description), Type, Entries, i, nameof(BindGroupLayoutEntry.Visibility), "has flags that aren't ShaderStages members", (long)entry.Visibility, name);
            }

            if ((uint)entry.Type > (uint)BindingType.Sampler)
            {
                ThrowOutOfRange(nameof(description), Type, Entries, i, nameof(BindGroupLayoutEntry.Type), "isn't a BindingType member", (long)entry.Type, name);
            }

            for (int j = 0; j < i; j++)
            {
                if (entries[j].Binding == entry.Binding)
                {
                    ThrowInvalid(nameof(description), Type, Entries, i, nameof(BindGroupLayoutEntry.Binding), string.Create(CultureInfo.InvariantCulture, $"must be unique, but {entry.Binding} is also Entries[{j}]'s"), name);
                }
            }
        }
    }

    /// <summary>
    /// Checks a bind group description on its own: its lifetime, each entry made by a factory, and no binding and array
    /// element twice. Matching the entries against the layout is the device's check.
    /// </summary>
    /// <param name="description">The description to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">The lifetime isn't a <see cref="BindGroupLifetime"/> member.</exception>
    /// <exception cref="ArgumentException">
    /// An entry's resource is the null handle (as in <c>default(BindGroupEntry)</c>), or two entries have the same binding
    /// and array element.
    /// </exception>
    public static void Validate(in BindGroupDescription description)
    {
        const string Type = nameof(BindGroupDescription);
        const string Entries = nameof(BindGroupDescription.Entries);
        string? name = description.DebugName;
        if ((uint)description.Lifetime > (uint)BindGroupLifetime.Frame)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(BindGroupDescription.Lifetime), -1, null, "isn't a BindGroupLifetime member", (long)description.Lifetime, name);
        }

        ReadOnlySpan<BindGroupEntry> entries = description.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            BindGroupEntry entry = entries[i];
            bool isNull = entry.Kind switch
            {
                BindGroupEntryKind.Buffer => entry.Buffer.Buffer.IsNull,
                BindGroupEntryKind.Texture => entry.View.IsNull,
                _ => entry.Sampler.IsNull,
            };
            if (isNull)
            {
                ThrowInvalid(nameof(description), Type, Entries, i, null, "must be made by a BindGroupEntry factory, but its resource is the null handle", name);
            }

            for (int j = 0; j < i; j++)
            {
                if (entries[j].Binding == entry.Binding && entries[j].ArrayElement == entry.ArrayElement)
                {
                    ThrowInvalid(nameof(description), Type, Entries, i, null, string.Create(CultureInfo.InvariantCulture, $"must not repeat a binding and array element, but binding {entry.Binding} element {entry.ArrayElement} is also Entries[{j}]'s"), name);
                }
            }
        }
    }

    /// <summary>
    /// Checks a graphics pipeline description: entry points, state values, targets, sample count and vertex layouts.
    /// </summary>
    /// <param name="description">The description to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is outside its enum (topology, cull mode, front face, a format, a blend factor or operation, a write mask,
    /// the depth comparison, a step mode); the sample count isn't 1, 2, 4, 8 or 16; a stride, location or offset is
    /// negative; or an attribute ends past its buffer's stride.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An entry point is <see langword="null"/> or empty; there is neither a color target nor a depth state; the depth
    /// format isn't a depth format or a color target's format isn't a color format; or two attributes share a location.
    /// </exception>
    public static void Validate(GraphicsPipelineDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        const string Type = nameof(GraphicsPipelineDescription);
        string? name = description.DebugName;
        CheckEntryPoint(nameof(description), Type, nameof(GraphicsPipelineDescription.Vertex), description.Vertex, name);
        if (description.Fragment is { } fragment)
        {
            CheckEntryPoint(nameof(description), Type, nameof(GraphicsPipelineDescription.Fragment), fragment, name);
        }

        if ((uint)description.Topology > (uint)PrimitiveTopology.LineStrip)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(GraphicsPipelineDescription.Topology), -1, null, "isn't a PrimitiveTopology member", (long)description.Topology, name);
        }

        if ((uint)description.Raster.CullMode > (uint)CullMode.Back)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(GraphicsPipelineDescription.Raster), -1, nameof(RasterState.CullMode), "isn't a CullMode member", (long)description.Raster.CullMode, name);
        }

        if ((uint)description.Raster.FrontFace > (uint)FrontFace.Clockwise)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(GraphicsPipelineDescription.Raster), -1, nameof(RasterState.FrontFace), "isn't a FrontFace member", (long)description.Raster.FrontFace, name);
        }

        int samples = description.SampleCount;
        if (samples < 1 || samples > 16 || !BitOperations.IsPow2(samples))
        {
            ThrowOutOfRange(nameof(description), Type, nameof(GraphicsPipelineDescription.SampleCount), -1, null, "must be 1, 2, 4, 8 or 16", samples, name);
        }

        ReadOnlySpan<ColorTargetState> targets = description.ColorTargets.Span;
        if (targets.IsEmpty && description.Depth is null)
        {
            ThrowInvalid(nameof(description), Type, nameof(GraphicsPipelineDescription.ColorTargets), -1, null, "must not be empty when there is no Depth state", name);
        }

        if (description.Depth is { } depth)
        {
            CheckFormat(nameof(description), nameof(GraphicsPipelineDescription.Depth), -1, depth.Format, wantDepth: true, name);
            if ((uint)depth.Compare > (uint)CompareFunction.Always)
            {
                ThrowOutOfRange(nameof(description), Type, nameof(GraphicsPipelineDescription.Depth), -1, nameof(DepthState.Compare), "isn't a CompareFunction member", (long)depth.Compare, name);
            }
        }

        for (int i = 0; i < targets.Length; i++)
        {
            ColorTargetState target = targets[i];
            CheckFormat(nameof(description), nameof(GraphicsPipelineDescription.ColorTargets), i, target.Format, wantDepth: false, name);
            if ((target.WriteMask & ~ColorWriteMask.All) != 0)
            {
                ThrowOutOfRange(nameof(description), Type, nameof(GraphicsPipelineDescription.ColorTargets), i, nameof(ColorTargetState.WriteMask), "has flags that aren't ColorWriteMask members", (long)target.WriteMask, name);
            }

            if (target.Blend is { } blend)
            {
                CheckBlend(nameof(description), i, "Blend.Color", blend.Color, name);
                CheckBlend(nameof(description), i, "Blend.Alpha", blend.Alpha, name);
            }
        }

        CheckVertexBuffers(nameof(description), description.VertexBuffers.Span, name);
    }

    /// <summary>Returns the bytes one attribute of <paramref name="format"/> occupies.</summary>
    /// <param name="format">A <see cref="VertexFormat"/> member.</param>
    /// <returns>4, 8, 12 or 16, as each member's documentation gives.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> isn't a member of the enum.</exception>
    internal static int VertexFormatSize(VertexFormat format) => format switch
    {
        VertexFormat.Float32 or VertexFormat.Float16x2 or VertexFormat.Unorm8x4 or VertexFormat.Uint8x4
            or VertexFormat.Uint32 or VertexFormat.Sint32 => 4,
        VertexFormat.Float32x2 or VertexFormat.Float16x4 or VertexFormat.Uint32x2 => 8,
        VertexFormat.Float32x3 => 12,
        VertexFormat.Float32x4 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a VertexFormat member."),
    };

    // Each buffer's stride and step mode, then each attribute's location, offset, format and extent, and no location
    // twice across all buffers (checked against every earlier attribute, so nothing is allocated).
    private static void CheckVertexBuffers(string paramName, ReadOnlySpan<VertexBufferLayout> buffers, string? debugName)
    {
        const string Type = nameof(GraphicsPipelineDescription);
        const string Field = nameof(GraphicsPipelineDescription.VertexBuffers);
        for (int b = 0; b < buffers.Length; b++)
        {
            VertexBufferLayout buffer = buffers[b];
            if (buffer.Stride < 0)
            {
                ThrowOutOfRange(paramName, Type, Field, b, nameof(VertexBufferLayout.Stride), "must be 0 or more", buffer.Stride, debugName);
            }

            if ((uint)buffer.StepMode > (uint)VertexStepMode.Instance)
            {
                ThrowOutOfRange(paramName, Type, Field, b, nameof(VertexBufferLayout.StepMode), "isn't a VertexStepMode member", (long)buffer.StepMode, debugName);
            }

            ReadOnlySpan<VertexAttribute> attributes = buffer.Attributes.Span;
            for (int a = 0; a < attributes.Length; a++)
            {
                VertexAttribute attribute = attributes[a];
                if (attribute.Location < 0)
                {
                    ThrowOutOfRange(paramName, Type, Field, b, Attribute(a, nameof(VertexAttribute.Location)), "must be 0 or more", attribute.Location, debugName);
                }

                if (attribute.Offset < 0)
                {
                    ThrowOutOfRange(paramName, Type, Field, b, Attribute(a, nameof(VertexAttribute.Offset)), "must be 0 or more", attribute.Offset, debugName);
                }

                if ((uint)attribute.Format > (uint)VertexFormat.Sint32)
                {
                    ThrowOutOfRange(paramName, Type, Field, b, Attribute(a, nameof(VertexAttribute.Format)), "isn't a VertexFormat member", (long)attribute.Format, debugName);
                }

                // Offset and size are both non-negative here, so their sum can't overflow a long.
                int size = VertexFormatSize(attribute.Format);
                if ((long)attribute.Offset + size > buffer.Stride)
                {
                    ThrowOutOfRange(
                        paramName,
                        Type,
                        Field,
                        b,
                        Attribute(a, nameof(VertexAttribute.Offset)),
                        string.Create(CultureInfo.InvariantCulture, $"must leave room for its {attribute.Format} ({size} bytes) inside the buffer's Stride ({buffer.Stride})"),
                        attribute.Offset,
                        debugName);
                }

                CheckLocationUnique(paramName, buffers, b, a, attribute.Location, debugName);
            }
        }
    }

    // The attribute at (buffer, index) against every attribute before it, in its own buffer and in earlier buffers.
    private static void CheckLocationUnique(string paramName, ReadOnlySpan<VertexBufferLayout> buffers, int buffer, int index, int location, string? debugName)
    {
        for (int b = 0; b <= buffer; b++)
        {
            ReadOnlySpan<VertexAttribute> earlier = buffers[b].Attributes.Span;
            int count = b == buffer ? index : earlier.Length;
            for (int a = 0; a < count; a++)
            {
                if (earlier[a].Location == location)
                {
                    ThrowInvalid(
                        paramName,
                        nameof(GraphicsPipelineDescription),
                        nameof(GraphicsPipelineDescription.VertexBuffers),
                        buffer,
                        Attribute(index, nameof(VertexAttribute.Location)),
                        string.Create(CultureInfo.InvariantCulture, $"must be unique across the vertex buffers, but {location} is also VertexBuffers[{b}].Attributes[{a}]'s"),
                        debugName);
                }
            }
        }
    }

    // A format outside the enum is out of range; a defined format of the wrong kind (Undefined, or depth for a color
    // target, or color for the depth state) is invalid.
    private static void CheckFormat(string paramName, string field, int index, TextureFormat format, bool wantDepth, string? debugName)
    {
        const string Type = nameof(GraphicsPipelineDescription);
        if ((uint)format > (uint)TextureFormat.Depth32Float)
        {
            ThrowOutOfRange(paramName, Type, field, index, nameof(ColorTargetState.Format), "isn't a TextureFormat member", (long)format, debugName);
        }

        bool isDepth = format is TextureFormat.Depth16Unorm or TextureFormat.Depth32Float;
        if (format == TextureFormat.Undefined || isDepth != wantDepth)
        {
            ThrowInvalid(paramName, Type, field, index, nameof(ColorTargetState.Format), string.Create(CultureInfo.InvariantCulture, $"must be a {(wantDepth ? "depth" : "color")} format, but is {format}"), debugName);
        }
    }

    private static void CheckBlend(string paramName, int index, string member, BlendComponent component, string? debugName)
    {
        const string Type = nameof(GraphicsPipelineDescription);
        const string Field = nameof(GraphicsPipelineDescription.ColorTargets);
        if ((uint)component.Source > (uint)BlendFactor.OneMinusDestinationAlpha)
        {
            ThrowOutOfRange(paramName, Type, Field, index, member + ".Source", "isn't a BlendFactor member", (long)component.Source, debugName);
        }

        if ((uint)component.Destination > (uint)BlendFactor.OneMinusDestinationAlpha)
        {
            ThrowOutOfRange(paramName, Type, Field, index, member + ".Destination", "isn't a BlendFactor member", (long)component.Destination, debugName);
        }

        if ((uint)component.Operation > (uint)BlendOperation.Max)
        {
            ThrowOutOfRange(paramName, Type, Field, index, member + ".Operation", "isn't a BlendOperation member", (long)component.Operation, debugName);
        }
    }

    private static string Attribute(int index, string member) =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(VertexBufferLayout.Attributes)}[{index}].{member}");

    // A shader stage's entry point must name a function; the module handle is the device's check.
    private static void CheckEntryPoint(string paramName, string type, string field, in ShaderStage stage, string? debugName)
    {
        if (string.IsNullOrEmpty(stage.EntryPoint))
        {
            ThrowInvalid(paramName, type, field, -1, nameof(ShaderStage.EntryPoint), "must not be null or empty", debugName);
        }
    }

    // The field path: Type.Field, Type.Field.Member, Type.Field[index] or Type.Field[index].Member (index -1 for none).
    private static string Path(string type, string field, int index, string? member)
    {
        string list = index < 0 ? field : string.Create(CultureInfo.InvariantCulture, $"{field}[{index}]");
        return member is null ? $"{type}.{list}" : $"{type}.{list}.{member}";
    }

    [DoesNotReturn]
    private static void ThrowOutOfRange(string paramName, string type, string field, int index, string? member, string requirement, long actual, string? debugName) =>
        throw new ArgumentOutOfRangeException(
            paramName,
            string.Create(CultureInfo.InvariantCulture, $"{Path(type, field, index, member)} {requirement}, but is {actual} ({Describe(debugName)})."));

    [DoesNotReturn]
    private static void ThrowInvalid(string paramName, string type, string field, int index, string? member, string requirement, string? debugName) =>
        throw new ArgumentException($"{Path(type, field, index, member)} {requirement} ({Describe(debugName)}).", paramName);

    private static string Describe(string? debugName) => debugName is null ? "no debug name" : $"debug name '{debugName}'";
}
