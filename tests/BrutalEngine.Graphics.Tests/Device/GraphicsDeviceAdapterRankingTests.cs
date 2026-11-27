using System;
using System.Linq;
using BrutalEngine.TestUtilities;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// How <see cref="GraphicsDevice.Create"/> ranks suitable adapters (docs/design/graphics.md section 2, "Adapter
/// selection"): discrete, then integrated, virtual, CPU and other, ties to the lowest index; an override narrows the
/// suitable set first.
/// </summary>
public sealed class GraphicsDeviceAdapterRankingTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // Adapters as "<type letter>[!]" in index order: D discrete, I integrated, V virtual, C CPU, O other; "!" = rejected.
    // Where a type preference decides, the preferred adapter sits at a later index than the ones it beats, so
    // "first suitable wins" fails those rows; the rest pin ties, rejections and narrowing overrides.
    public static TheoryData<string, string, int> Rankings() => new()
    {
        { "C V I D", "auto", 3 },
        { "C V I", "auto", 2 },
        { "O C V", "auto", 2 },
        { "O C", "auto", 1 },
        { "V I", "auto", 1 },
        { "C I V", "auto", 1 },
        { "I D D", "auto", 1 },
        { "C C", "auto", 0 },
        { "D! I", "auto", 1 },
        { "D! I! C", "auto", 2 },
        { "I D! V", "auto", 0 },
        { "I D I", "type:integrated", 0 },
        { "C I! I D", "type:integrated", 2 },
        { "D C", "index:1", 1 },
        { "C:Shared I:Shared D:Other", "name:shared", 1 },
        { "D:Alpha C:Beta V:Beta", "name:beta", 2 },
    };

    [Theory]
    [MemberData(nameof(Rankings))]
    public void Create_RanksSuitableAdapters(string adapters, string selection, int expected)
    {
        string[] entries = adapters.Split(' ');
        _fixture.Script.Adapters.Clear();
        for (int index = 0; index < entries.Length; index++)
        {
            string entry = entries[index];
            AdapterType type = entry[0] switch
            {
                'D' => AdapterType.Discrete,
                'I' => AdapterType.Integrated,
                'V' => AdapterType.Virtual,
                'C' => AdapterType.Cpu,
                'O' => AdapterType.Other,
                _ => throw new ArgumentOutOfRangeException(nameof(adapters), adapters, null),
            };
            string[] reasons = entry.Contains('!', StringComparison.Ordinal) ? ["rejected for the test"] : [];
            string name = entry.Contains(':', StringComparison.Ordinal) ? entry[(entry.IndexOf(':', StringComparison.Ordinal) + 1)..] : entry;
            _fixture.Script.Adapters.Add(FakeBackendScript.CreateAdapter(index, reasons) with { Type = type, Name = $"{name} {index}" });
        }

        Assert.True(AdapterSelection.TryParse(selection, out AdapterSelection chosen));

        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Adapter = chosen });

        Assert.Equal(expected, device.Adapter.Index);
        Assert.Equal(expected, _fixture.BackendDevice.Adapter.Index);
    }

    [Fact]
    public void Rankings_CoverEveryAdapterType() =>
        Assert.Equal(
            Enum.GetValues<AdapterType>().Select(type => type.ToString()[0]).Order(),
            Rankings().SelectMany(row => row.Data.Item1.Split(' ')).Select(entry => entry[0]).Distinct().Order());
}
