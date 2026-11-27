using System;
using System.Linq;
using System.Text;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// Interned debug labels (docs/design/graphics.md section 11). The project turns background garbage collection off,
/// which AllocationMeasurementTests guards, so the allocation count is exact.
/// </summary>
public sealed class GraphicsDeviceLabelTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Intern_Once_NameDoesNotAllocate()
    {
        GraphicsDevice device = _fixture.Create();
        string name = "Sprites";
        DebugLabel label = device.CreateDebugLabel(name);
        // An equal name in a different string instance, so a lookup by reference can't pass for interning.
        string sameName = new StringBuilder("Spri").Append("tes").ToString();
        _ = device.GetDebugLabelName(label);

        long before = GC.GetAllocatedBytesForCurrentThread();
        DebugLabel again = device.CreateDebugLabel(sameName);
        string read = device.GetDebugLabelName(label);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(label, again);
        Assert.Same(name, read);
        Assert.Equal("Sprites"u8.ToArray(), _fixture.BackendDevice.Objects.GetLabel(label).ToArray());
    }

    [Fact]
    public void Intern_DifferentNames_GetDifferentLabels()
    {
        GraphicsDevice device = _fixture.Create();

        DebugLabel a = device.CreateDebugLabel("Pass");
        DebugLabel b = device.CreateDebugLabel("pass");
        DebugLabel c = device.CreateDebugLabel("Pass ");

        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
        Assert.All(new[] { a, b, c }, label => Assert.False(label.IsNull));
        Assert.Equal(("Pass", "pass", "Pass "), (device.GetDebugLabelName(a), device.GetDebugLabelName(b), device.GetDebugLabelName(c)));
        Assert.Equal("é Ω"u8.ToArray(), _fixture.BackendDevice.Objects.GetLabel(device.CreateDebugLabel("é Ω")).ToArray());
    }

    [Fact]
    public void GetDebugLabelName_Default_IsEmpty()
    {
        GraphicsDevice device = _fixture.Create();

        Assert.Equal(string.Empty, device.GetDebugLabelName(default));
    }

    [Fact]
    public void GetDebugLabelName_NotInterned_ThrowsStale()
    {
        GraphicsDevice device = _fixture.Create();
        DebugLabel interned = device.CreateDebugLabel("One");

        Assert.Throws<StaleHandleException>(() => device.GetDebugLabelName(new DebugLabel(new Handle(1, 1))));
        Assert.Throws<StaleHandleException>(() => device.GetDebugLabelName(new DebugLabel(new Handle(interned.Value.Index, 2))));
    }

    [Fact]
    public void CreateDebugLabel_InvalidName_Throws()
    {
        GraphicsDevice device = _fixture.Create();

        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => device.CreateDebugLabel(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => device.CreateDebugLabel("")).ParamName);
        // Nothing was interned: the first real label takes the first slot.
        Assert.Equal(0u, device.CreateDebugLabel("First").Value.Index);
    }

    [Fact]
    public void CreateDebugLabel_TableCannotGrow_LeavesNothing()
    {
        GraphicsDevice device = _fixture.Create();
        _fixture.BackendDevice.Objects.BeforeGrowth = kind =>
        {
            if (kind is null)
            {
                throw new InsufficientMemoryException("Simulated: the label table can't grow.");
            }
        };

        Assert.Throws<InsufficientMemoryException>(() => device.CreateDebugLabel("Lost"));

        _fixture.BackendDevice.Objects.BeforeGrowth = null;
        DebugLabel label = device.CreateDebugLabel("Lost");
        // The failed call freed its slot: the retry reuses slot 0, one generation on, and no slot leaked.
        Assert.Equal(new Handle(0, 2), label.Value);
        Assert.Equal(1u, device.CreateDebugLabel("Next").Value.Index);
        Assert.Equal("Lost", device.GetDebugLabelName(label));
        Assert.Equal("Lost"u8.ToArray(), _fixture.BackendDevice.Objects.GetLabel(label).ToArray());
        Assert.Equal(label, device.CreateDebugLabel("Lost"));
    }
}
