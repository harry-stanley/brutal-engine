using System;
using System.Collections.Generic;
using System.Globalization;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Validation;

namespace BrutalEngine.Graphics;

/// <content>Transient allocations from the upload ring and their alias handles (docs/design/graphics.md section 6).</content>
public sealed partial class GraphicsDevice
{
    private const string AllocateTransientApi = nameof(CommandList) + "." + nameof(CommandList.AllocateTransient);

    // The open frame's blocks that have an alias, so the submit can make each one stale. Grows only past the most aliases
    // an earlier frame had.
    private readonly List<UploadRing> _aliased = [];

    /// <summary>The device side of <see cref="CommandList.AllocateTransient"/>; the list's documentation is the contract.</summary>
    internal Result AllocateTransient(CommandList commands, long byteCount, TransientUsage usage, out TransientAllocation allocation)
    {
        allocation = default;
        ThrowIfUnusable(AllocateTransientApi);
        ThrowIfNotOpenFrame(commands, AllocateTransientApi);
        CopyValidator.ValidateTransientAllocation(byteCount, usage);
        if (byteCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCount),
                string.Create(CultureInfo.InvariantCulture, $"{AllocateTransientApi}: byteCount must be at most {int.MaxValue} (Data is a span), but is {byteCount}."));
        }

        if (!TryStage(byteCount, TransientAlignment(usage), AllocateTransientApi, out UploadRing? block, out long offset, out bool overflow, out EngineError error))
        {
            return error;
        }

        if (block.Alias.IsNull)
        {
            // Listed first, so a failed AddAlias leaves nothing live that the submit doesn't know about. The alias reports
            // the block's own usages: every transient usage and copy source, never copy destination.
            _aliased.Add(block);
            block.Alias = _handles.AddAlias(block.Id, new BufferDescription { Size = block.Capacity, Usage = StagingUsage, Memory = MemoryIntent.Upload });
        }

        if (overflow)
        {
            CountOverflow(byteCount, AllocateTransientApi);
        }

        allocation = new TransientAllocation(new BufferHandle(block.Alias), offset, block.Bytes(offset, (int)byteCount));
        return Result.Success;
    }

    // Uniform and storage offsets follow the device's limits, every other usage gets the ring's 16 bytes, and none gets
    // less. The limits are powers of two (Vulkan guarantees it), so the largest of them is a multiple of every other.
    private long TransientAlignment(TransientUsage usage)
    {
        GraphicsLimits limits = Capabilities.Limits;
        long alignment = UploadRing.Alignment;
        if ((usage & TransientUsage.Uniform) != 0)
        {
            alignment = Math.Max(alignment, limits.MinUniformBufferOffsetAlignment);
        }

        if ((usage & TransientUsage.Storage) != 0)
        {
            alignment = Math.Max(alignment, limits.MinStorageBufferOffsetAlignment);
        }

        return alignment;
    }

    // At SubmitFrame: the frame's aliases go stale. Their blocks stay: the slot's ring, or an overflow block its deletion
    // queue destroys.
    private void EndTransients()
    {
        foreach (UploadRing block in _aliased)
        {
            if (!block.Alias.IsNull)
            {
                _handles.RemoveAlias(block.Alias);
                block.Alias = default;
            }
        }

        _aliased.Clear();
    }
}
