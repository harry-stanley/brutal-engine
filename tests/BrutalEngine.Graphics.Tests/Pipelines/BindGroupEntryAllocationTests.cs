using System;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Pipelines;

/// <summary>
/// Frame bind groups are described in the frame loop without a managed allocation (docs/design/graphics.md section 8):
/// every factory, every read property, the entries' and layout entries' equality and hash, and a description over a
/// stack span are measured here. The project
/// turns background garbage collection off, which AllocationMeasurementTests guards, so the counts are exact.
/// </summary>
public sealed class BindGroupEntryAllocationTests
{
    private const int Iterations = 1_000;

    [Fact]
    public void FactoriesReadsAndEquality_DoNotAllocate()
    {
        byte[] ring = new byte[64];
        var buffer = new BufferHandle(new Handle(1, 1));
        var alias = new BufferHandle(new Handle(2, 1));
        var view = new TextureViewHandle(new Handle(3, 1));
        var sampler = new SamplerHandle(new Handle(4, 1));

        long checksum = Exercise(ring, buffer, alias, view, sampler);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            checksum += Exercise(ring, buffer, alias, view, sampler);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal((Iterations + 1) * 1_182L, checksum);
    }

    private static long Exercise(byte[] ring, BufferHandle buffer, BufferHandle alias, TextureViewHandle view, SamplerHandle sampler)
    {
        Span<BindGroupEntry> entries = stackalloc BindGroupEntry[4];
        entries[0] = BindGroupEntry.FromBuffer(0, new BufferBinding(buffer, 256, 64));
        entries[1] = BindGroupEntry.FromTransient(1, new TransientAllocation(alias, 512, ring), 32);
        entries[2] = BindGroupEntry.FromTexture(2, view, 1);
        entries[3] = BindGroupEntry.FromSampler(3, sampler, 2);
        var group = new BindGroupDescription { Layout = default, Entries = entries, Lifetime = BindGroupLifetime.Frame };

        // Bindings 0 + 1 + 2 + 3 = 6, array elements 0 + 0 + 1 + 2 = 3, kinds (0 + 0 + 1 + 2) * 100 = 300, the buffer
        // reads 256 + 64 + 512 + 32 = 864, and 1 per handle read back right (4) and per equality that held (5): 1,182.
        long sum = 0;
        foreach (ref readonly BindGroupEntry entry in group.Entries)
        {
            sum += entry.Binding + entry.ArrayElement + (int)entry.Kind * 100;
        }

        sum += entries[0].Buffer.Offset + entries[0].Buffer.Size + entries[1].Buffer.Offset + entries[1].Buffer.Size;
        sum += (entries[0].Buffer.Buffer == buffer ? 1 : 0) + (entries[1].Buffer.Buffer == alias ? 1 : 0);
        sum += (entries[2].View == view ? 1 : 0) + (entries[3].Sampler == sampler ? 1 : 0);
        sum += entries[2].Equals(entries[2]) ? 1 : 0;
        sum += entries[2] != entries[3] ? 1 : 0;
        sum += entries[0].GetHashCode() == entries[0].GetHashCode() ? 1 : 0;
        var layoutEntry = new BindGroupLayoutEntry(0, ShaderStages.Vertex, BindingType.UniformBuffer);
        sum += layoutEntry == new BindGroupLayoutEntry(0, ShaderStages.Vertex, BindingType.UniformBuffer, Count: 1) ? 1 : 0;
        sum += entries[0].Buffer.Equals(new BufferBinding(buffer, 256, 64)) && entries[0].Buffer.GetHashCode() == new BufferBinding(buffer, 256, 64).GetHashCode() ? 1 : 0;
        return sum;
    }
}
