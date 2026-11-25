using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace BrutalEngine.Core.Memory;

/// <summary>
/// Wraps another <see cref="INativeAllocator"/> and records every live allocation, so leaks, double frees and
/// frees of foreign pointers are detected and reported with the call site that allocated the memory.
/// </summary>
/// <remarks>
/// <para>
/// Used in debug builds and in every test that exercises unmanaged memory (docs/03-architecture.md, section 3.3).
/// At shutdown, call <see cref="ThrowIfLeaks"/>, or inspect <see cref="CreateLeakReport"/>.
/// </para>
/// <para>
/// <see cref="Free"/> validates the pointer <em>before</em> passing it on, so an invalid free throws instead of
/// corrupting the inner allocator's heap.
/// </para>
/// <para>
/// Thread-safe: bookkeeping is serialised by a lock, and the inner allocator is called outside it. Allocate and
/// free don't allocate managed memory, except when the internal table grows past its capacity.
/// </para>
/// </remarks>
public sealed unsafe class TrackingAllocator : INativeAllocator
{
    private const int MaxReportedAllocations = 50;

    private readonly INativeAllocator _inner;
    private readonly Lock _lock = new();
    private readonly Dictionary<nint, NativeAllocationInfo> _live;
    private long _liveBytes;
    private long _peakLiveBytes;
    private long _totalAllocations;

    /// <summary>Creates a tracking allocator.</summary>
    /// <param name="inner">The allocator that provides the memory.</param>
    /// <param name="name">Name used in reports and exception messages, e.g. <c>"Renderer"</c>.</param>
    /// <param name="initialCapacity">Number of live allocations to reserve tracking space for.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    public TrackingAllocator(INativeAllocator inner, string name, int initialCapacity = 0)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);

        _inner = inner;
        Name = name;
        _live = new Dictionary<nint, NativeAllocationInfo>(initialCapacity);
    }

    /// <summary>Name used in reports and exception messages.</summary>
    public string Name { get; }

    /// <summary>Number of allocations not yet freed.</summary>
    public int LiveAllocationCount
    {
        get
        {
            lock (_lock)
            {
                return _live.Count;
            }
        }
    }

    /// <summary>Total bytes of allocations not yet freed.</summary>
    public long LiveBytes
    {
        get
        {
            lock (_lock)
            {
                return _liveBytes;
            }
        }
    }

    /// <summary>Highest value <see cref="LiveBytes"/> has reached.</summary>
    public long PeakLiveBytes
    {
        get
        {
            lock (_lock)
            {
                return _peakLiveBytes;
            }
        }
    }

    /// <summary>Number of allocations made since creation.</summary>
    public long TotalAllocationCount
    {
        get
        {
            lock (_lock)
            {
                return _totalAllocations;
            }
        }
    }

    /// <inheritdoc />
    public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
    {
        HeapAllocator.ValidateRequest(byteCount, alignment);
        void* memory = _inner.Allocate(byteCount, alignment, callerFile, callerLine);
        if (memory == null)
        {
            throw new InvalidOperationException($"Allocator '{Name}': the inner allocator returned null, which breaks the INativeAllocator contract.");
        }

        bool recorded;
        lock (_lock)
        {
            long sequence = _totalAllocations + 1;
            recorded = _live.TryAdd((nint)memory, new NativeAllocationInfo((nint)memory, byteCount, alignment, callerFile, callerLine, sequence));
            if (recorded)
            {
                _totalAllocations = sequence;
                _liveBytes += (long)byteCount;
                _peakLiveBytes = Math.Max(_peakLiveBytes, _liveBytes);
            }
        }

        if (!recorded)
        {
            // The inner allocator handed out an address that is still live: it is broken. Don't hand it on.
            throw new InvalidOperationException(
                $"Allocator '{Name}': the inner allocator returned 0x{(nint)memory:X}, which is still allocated.");
        }

        return memory;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <paramref name="memory"/> is not a live allocation of this allocator (a double free or a foreign pointer).
    /// Nothing is freed in that case.
    /// </exception>
    public void Free(void* memory)
    {
        if (memory == null)
        {
            return;
        }

        lock (_lock)
        {
            if (!_live.Remove((nint)memory, out NativeAllocationInfo info))
            {
                throw new InvalidOperationException(
                    $"Allocator '{Name}': 0x{(nint)memory:X} is not a live allocation (double free, or memory from another allocator).");
            }

            _liveBytes -= (long)info.ByteCount;
        }

        _inner.Free(memory);
    }

    /// <summary>Returns a snapshot of the live allocations, oldest first.</summary>
    /// <returns>A new array. Allocates.</returns>
    public NativeAllocationInfo[] GetLiveAllocations()
    {
        NativeAllocationInfo[] snapshot;
        lock (_lock)
        {
            snapshot = new NativeAllocationInfo[_live.Count];
            _live.Values.CopyTo(snapshot, 0);
        }

        Array.Sort(snapshot, static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return snapshot;
    }

    /// <summary>
    /// Describes the live allocations: how many, how many bytes, and (for the oldest 50) the size, alignment
    /// and call site of each.
    /// </summary>
    /// <returns>A human-readable report. Allocates.</returns>
    public string CreateLeakReport()
    {
        NativeAllocationInfo[] live = GetLiveAllocations();
        long bytes = 0;
        foreach (NativeAllocationInfo info in live)
        {
            bytes += (long)info.ByteCount;
        }

        StringBuilder report = new();
        _ = report.Append(CultureInfo.InvariantCulture, $"Allocator '{Name}': {live.Length} live allocation(s), {bytes} byte(s).");
        for (int i = 0; i < live.Length && i < MaxReportedAllocations; i++)
        {
            NativeAllocationInfo info = live[i];
            _ = report.AppendLine().Append(
                CultureInfo.InvariantCulture,
                $"  #{info.Sequence}: {info.ByteCount} byte(s), alignment {info.Alignment}, allocated at {info.CallerFile}:{info.CallerLine}");
        }

        if (live.Length > MaxReportedAllocations)
        {
            _ = report.AppendLine().Append(CultureInfo.InvariantCulture, $"  ... and {live.Length - MaxReportedAllocations} more.");
        }

        return report.ToString();
    }

    /// <summary>Throws if any allocation is still live.</summary>
    /// <exception cref="NativeMemoryLeakException">At least one allocation has not been freed. The message is the leak report.</exception>
    public void ThrowIfLeaks()
    {
        int count;
        long bytes;
        lock (_lock)
        {
            count = _live.Count;
            bytes = _liveBytes;
        }

        if (count > 0)
        {
            throw new NativeMemoryLeakException(CreateLeakReport(), count, bytes);
        }
    }
}
