using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrutalEngine.Graphics.Recording;

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// Reads the records of a command stream in order, such as a <see cref="FramePlan.Commands"/> (docs/design/graphics.md,
/// "The frame plan"): each record's <see cref="Kind"/>, its payload struct and its trailing part.
/// </summary>
/// <remarks>
/// <para>
/// <b>Use.</b> Call <see cref="MoveNext"/> until it returns <see langword="false"/>, switch on <see cref="Kind"/>, and
/// read the record with <see cref="Payload{T}"/> and, for the kinds that have one, <see cref="Trailing{T}"/>. Each
/// <see cref="CommandKind"/> member names its payload struct. The trailing parts are <see cref="ResourceTransition"/>
/// for <see cref="CommandKind.Barrier"/>, <see cref="AttachmentCommand"/> for
/// <see cref="CommandKind.BeginRenderPass"/> and <see cref="byte"/> for <see cref="CommandKind.PushConstants"/>.
/// </para>
/// <para>
/// <b>Checks.</b> A backend only reads streams the front end wrote, so the checks guard against bugs, not input.
/// <see cref="MoveNext"/> throws <see cref="InvalidOperationException"/> for a malformed record: a truncated header,
/// an unknown kind, a non-zero reserved field, a size too small for the kind's payload, or a record (with its padding)
/// past the end of the stream. <see cref="Payload{T}"/> and <see cref="Trailing{T}"/> throw it when the type argument
/// isn't the kind's type. Each record is checked before it becomes current, and the reader
/// stops at the end of the stream, so it never reads outside the span.
/// </para>
/// <para>
/// <b>Memory.</b> The reader copies nothing: <see cref="Payload{T}"/> and <see cref="Trailing{T}"/> return a reference
/// and a span into the stream, valid as long as the stream is (for a frame plan, during <c>Execute</c>). The stream
/// should start 8-byte aligned, as the front end's streams do, so the payloads and trailing parts are aligned. No
/// member allocates managed memory, except to throw.
/// </para>
/// <para>
/// A <see langword="ref struct"/> over the caller's span: use it on one thread, and don't copy it to read the same
/// stream twice (each copy has its own position). <c>default(CommandReader)</c> reads as an empty stream.
/// </para>
/// </remarks>
public ref struct CommandReader
{
    private readonly ReadOnlySpan<byte> _commands;
    private int _next;
    private int _current;
    private int _size;
    private CommandKind _kind;
    private bool _hasCurrent;

    /// <summary>Creates a reader positioned before the stream's first record.</summary>
    /// <param name="commands">The command stream. Not copied; must stay valid while the reader is used.</param>
    public CommandReader(ReadOnlySpan<byte> commands)
    {
        _commands = commands;
        _next = 0;
        _current = 0;
        _size = 0;
        _kind = default;
        _hasCurrent = false;
    }

    /// <summary>The current record's kind.</summary>
    /// <exception cref="InvalidOperationException">
    /// There is no current record: <see cref="MoveNext"/> hasn't been called, or it returned <see langword="false"/>.
    /// </exception>
    public readonly CommandKind Kind
    {
        get
        {
            ThrowIfNoRecord();
            return _kind;
        }
    }

    /// <summary>Moves to the next record and checks it.</summary>
    /// <returns><see langword="true"/> at a record; <see langword="false"/> at the end of the stream, and on every call after.</returns>
    /// <exception cref="InvalidOperationException">The next record is malformed. The reader stays where it was.</exception>
    public bool MoveNext()
    {
        int remaining = _commands.Length - _next;
        if (remaining == 0)
        {
            _hasCurrent = false;
            return false;
        }

        if (remaining < CommandRecordHeader.Alignment)
        {
            throw Malformed(_next, $"only {remaining} bytes remain, less than a record header");
        }

        CommandRecordHeader header = MemoryMarshal.Read<CommandRecordHeader>(_commands[_next..]);
        int payloadSize = PayloadSize(header.Kind);
        if (payloadSize == 0)
        {
            throw Malformed(_next, $"the kind {(ushort)header.Kind} isn't a CommandKind");
        }

        if (header.Reserved != 0)
        {
            throw Malformed(_next, $"the reserved field is {header.Reserved}, not 0");
        }

        long minimum = CommandRecordHeader.TrailingOffset(payloadSize);
        if (header.Size < minimum)
        {
            throw Malformed(_next, $"its size {header.Size} is less than the {minimum} bytes a {header.Kind} record needs");
        }

        long padded = CommandRecordHeader.AlignUp(header.Size);
        if (padded > remaining)
        {
            throw Malformed(_next, $"its size {header.Size} (padded to {padded}) runs past the end of the stream");
        }

        _current = _next;
        _kind = header.Kind;
        _size = header.Size;
        _hasCurrent = true;
        _next += (int)padded;
        return true;
    }

    /// <summary>Returns the current record's payload, by reference into the stream.</summary>
    /// <typeparam name="T">The payload struct the current <see cref="Kind"/> names.</typeparam>
    /// <returns>A read-only reference to the payload, valid as long as the stream is.</returns>
    /// <exception cref="InvalidOperationException">
    /// There is no current record, or <typeparamref name="T"/> isn't the payload struct of <see cref="Kind"/>.
    /// </exception>
    public readonly ref readonly T Payload<T>()
        where T : unmanaged
    {
        ThrowIfNoRecord();
        if (!IsPayload<T>(_kind))
        {
            throw new InvalidOperationException(
                $"A {_kind} record's payload isn't a {typeof(T).Name}. Read the payload struct the CommandKind member names.");
        }

        return ref MemoryMarshal.AsRef<T>(_commands.Slice(_current + CommandRecordHeader.Alignment, Unsafe.SizeOf<T>()));
    }

    /// <summary>Returns the current record's trailing part, as a span into the stream.</summary>
    /// <typeparam name="T">The trailing element type of <see cref="Kind"/>; any type for a kind without one.</typeparam>
    /// <returns>
    /// The trailing elements, valid as long as the stream is; empty for a kind without a trailing part, and for an
    /// empty trailing part.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// There is no current record; <see cref="Kind"/> has a trailing part whose element type isn't
    /// <typeparamref name="T"/>; or the trailing part isn't a whole number of elements (a malformed record).
    /// </exception>
    public readonly ReadOnlySpan<T> Trailing<T>()
        where T : unmanaged
    {
        ThrowIfNoRecord();
        TrailingMatch match = MatchTrailing<T>(_kind);
        if (match == TrailingMatch.None)
        {
            return [];
        }

        if (match == TrailingMatch.Wrong)
        {
            throw new InvalidOperationException($"A {_kind} record's trailing elements aren't {typeof(T).Name}s.");
        }

        int offset = (int)CommandRecordHeader.TrailingOffset(PayloadSize(_kind));
        int bytes = _size - offset;
        if (bytes % Unsafe.SizeOf<T>() != 0)
        {
            throw Malformed(_current, $"its trailing part of {bytes} bytes isn't a whole number of {typeof(T).Name}s");
        }

        return MemoryMarshal.Cast<byte, T>(_commands.Slice(_current + offset, bytes));
    }

    // The payload struct's size for each kind; 0 for a value that isn't a kind.
    private static int PayloadSize(CommandKind kind) => kind switch
    {
        CommandKind.Barrier => Unsafe.SizeOf<BarrierCommand>(),
        CommandKind.CopyBufferToBuffer => Unsafe.SizeOf<CopyBufferToBufferCommand>(),
        CommandKind.CopyBufferToTexture or CommandKind.CopyTextureToBuffer => Unsafe.SizeOf<CopyBufferTextureCommand>(),
        CommandKind.CopyTextureToTexture => Unsafe.SizeOf<CopyTextureToTextureCommand>(),
        CommandKind.BeginRenderPass => Unsafe.SizeOf<BeginRenderPassCommand>(),
        CommandKind.EndRenderPass or CommandKind.EndComputePass => Unsafe.SizeOf<EndPassCommand>(),
        CommandKind.BeginComputePass => Unsafe.SizeOf<BeginComputePassCommand>(),
        CommandKind.SetPipeline => Unsafe.SizeOf<SetPipelineCommand>(),
        CommandKind.SetBindGroup => Unsafe.SizeOf<SetBindGroupCommand>(),
        CommandKind.SetVertexBuffer => Unsafe.SizeOf<SetVertexBufferCommand>(),
        CommandKind.SetIndexBuffer => Unsafe.SizeOf<SetIndexBufferCommand>(),
        CommandKind.SetViewport => Unsafe.SizeOf<SetViewportCommand>(),
        CommandKind.SetScissor => Unsafe.SizeOf<SetScissorCommand>(),
        CommandKind.PushConstants => Unsafe.SizeOf<PushConstantsCommand>(),
        CommandKind.Draw => Unsafe.SizeOf<DrawCommand>(),
        CommandKind.DrawIndexed => Unsafe.SizeOf<DrawIndexedCommand>(),
        CommandKind.Dispatch => Unsafe.SizeOf<DispatchCommand>(),
        CommandKind.PushDebugGroup or CommandKind.PopDebugGroup => Unsafe.SizeOf<DebugGroupCommand>(),
        CommandKind.ClearSwapchainImage => Unsafe.SizeOf<ClearSwapchainImageCommand>(),
        _ => 0,
    };

    // Whether T is the kind's payload struct. The typeof comparisons are constants for each T the JIT compiles, so
    // the check allocates nothing.
    private static bool IsPayload<T>(CommandKind kind) => kind switch
    {
        CommandKind.Barrier => typeof(T) == typeof(BarrierCommand),
        CommandKind.CopyBufferToBuffer => typeof(T) == typeof(CopyBufferToBufferCommand),
        CommandKind.CopyBufferToTexture or CommandKind.CopyTextureToBuffer => typeof(T) == typeof(CopyBufferTextureCommand),
        CommandKind.CopyTextureToTexture => typeof(T) == typeof(CopyTextureToTextureCommand),
        CommandKind.BeginRenderPass => typeof(T) == typeof(BeginRenderPassCommand),
        CommandKind.EndRenderPass or CommandKind.EndComputePass => typeof(T) == typeof(EndPassCommand),
        CommandKind.BeginComputePass => typeof(T) == typeof(BeginComputePassCommand),
        CommandKind.SetPipeline => typeof(T) == typeof(SetPipelineCommand),
        CommandKind.SetBindGroup => typeof(T) == typeof(SetBindGroupCommand),
        CommandKind.SetVertexBuffer => typeof(T) == typeof(SetVertexBufferCommand),
        CommandKind.SetIndexBuffer => typeof(T) == typeof(SetIndexBufferCommand),
        CommandKind.SetViewport => typeof(T) == typeof(SetViewportCommand),
        CommandKind.SetScissor => typeof(T) == typeof(SetScissorCommand),
        CommandKind.PushConstants => typeof(T) == typeof(PushConstantsCommand),
        CommandKind.Draw => typeof(T) == typeof(DrawCommand),
        CommandKind.DrawIndexed => typeof(T) == typeof(DrawIndexedCommand),
        CommandKind.Dispatch => typeof(T) == typeof(DispatchCommand),
        CommandKind.PushDebugGroup or CommandKind.PopDebugGroup => typeof(T) == typeof(DebugGroupCommand),
        CommandKind.ClearSwapchainImage => typeof(T) == typeof(ClearSwapchainImageCommand),
        _ => false,
    };

    private static TrailingMatch MatchTrailing<T>(CommandKind kind) => kind switch
    {
        CommandKind.Barrier => typeof(T) == typeof(ResourceTransition) ? TrailingMatch.Match : TrailingMatch.Wrong,
        CommandKind.BeginRenderPass => typeof(T) == typeof(AttachmentCommand) ? TrailingMatch.Match : TrailingMatch.Wrong,
        CommandKind.PushConstants => typeof(T) == typeof(byte) ? TrailingMatch.Match : TrailingMatch.Wrong,
        _ => TrailingMatch.None,
    };

    private static InvalidOperationException Malformed(int offset, string reason) =>
        new($"The command stream is malformed: the record at byte {offset}: {reason}.");

    private readonly void ThrowIfNoRecord()
    {
        if (!_hasCurrent)
        {
            throw new InvalidOperationException(
                "The reader has no current record: call MoveNext first, and stop when it returns false.");
        }
    }

    private enum TrailingMatch
    {
        None,
        Match,
        Wrong,
    }
}
