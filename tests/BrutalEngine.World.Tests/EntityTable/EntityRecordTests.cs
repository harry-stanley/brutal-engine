using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.EntityTable;

public sealed class EntityRecordTests
{
    [Fact]
    public void Size_Is16Bytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<EntityRecord>());
    }

    [Fact]
    public void States_AreDistinctAndNegative()
    {
        int[] states = [EntityRecord.FreeState, EntityRecord.PendingState, EntityRecord.RetiredState];

        Assert.All(states, state => Assert.True(state < 0));
        Assert.Equal(states.Length, new HashSet<int>(states).Count);
    }
}
