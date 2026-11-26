using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>The device's owner-thread rule (docs/design/graphics.md section 14).</summary>
public sealed class GraphicsDeviceThreadTests : IDisposable
{
    private static readonly TimeSpan _joinTimeout = TimeSpan.FromSeconds(30);

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>Every member that checks the owner thread: all but <see cref="GraphicsDevice.Adapter"/> and <see cref="GraphicsDevice.Capabilities"/>.</summary>
    public static TheoryData<string> Members()
    {
        TheoryData<string> members = ["Dispose"];
        foreach (string member in GraphicsDeviceDisposeTests.Members().Select(row => row.Data))
        {
            members.Add(member);
        }

        return members;
    }

    [Fact]
    public void Members_CoverEveryPublicMember()
    {
        // Every public instance member of the device, by name, except the two any-thread properties and object's own.
        string[] expected =
        [
            .. typeof(GraphicsDevice).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(member => member is MethodInfo { IsSpecialName: false } or PropertyInfo)
                .Select(member => member.Name)
                .Where(name => name is not (nameof(GraphicsDevice.Adapter) or nameof(GraphicsDevice.Capabilities)))
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];
        string[] covered = [.. Members().Select(row => row.Data.Split('(')[0]).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(expected, covered);
        // And one Destroy row per overload.
        Assert.Equal(
            typeof(GraphicsDevice).GetMethods().Count(method => method.Name == nameof(GraphicsDevice.Destroy)),
            Members().Count(row => row.Data.StartsWith("Destroy(", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void OtherThread_Throws(string member)
    {
        GraphicsDevice device = _fixture.Create();
        var prepared = GraphicsDeviceDisposeTests.Prepared.On(device);
        int calls = _fixture.BackendDevice.Calls.Count;
        int live = device.Statistics.LiveObjects;

        Exception? thrown = OnOtherThread(() => GraphicsDeviceDisposeTests.Call(device, member, prepared));

        WrongThreadException wrongThread = Assert.IsType<WrongThreadException>(thrown);
        Assert.Equal("GraphicsDevice." + member.Split('(')[0], wrongThread.ApiName);
        Assert.Equal(Environment.CurrentManagedThreadId, wrongThread.ExpectedThreadId);
        // Nothing happened: no backend call, the same live objects, and the device still works on its owner thread.
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        Assert.Equal(live, device.Statistics.LiveObjects);
        Assert.False(_fixture.BackendDevice.IsDisposed);
        _ = device.GetFormatSupport(TextureFormat.Rgba8Unorm);
        prepared.Destroy(device);
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void OtherThread_AfterDispose_ThrowsWrongThreadFirst(string member)
    {
        GraphicsDevice device = _fixture.Create();
        var prepared = GraphicsDeviceDisposeTests.Prepared.On(device);
        prepared.Destroy(device);
        device.Dispose();

        Exception? thrown = OnOtherThread(() => GraphicsDeviceDisposeTests.Call(device, member, prepared));

        Assert.IsType<WrongThreadException>(thrown);
    }

    [Fact]
    public void OtherThread_AdapterAndCapabilities_Work()
    {
        GraphicsDevice device = _fixture.Create();
        GraphicsAdapterInfo? adapter = null;
        GraphicsCapabilities? capabilities = null;

        Exception? thrown = OnOtherThread(() =>
        {
            adapter = device.Adapter;
            capabilities = device.Capabilities;
        });

        Assert.Null(thrown);
        Assert.Same(device.Adapter, adapter);
        Assert.Same(device.Capabilities, capabilities);
    }

    /// <summary>Runs an action on a new thread and returns what it threw, or <see langword="null"/>.</summary>
    internal static Exception? OnOtherThread(Action action)
    {
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
        });
        thread.Start();
        Assert.True(thread.Join(_joinTimeout), "The worker thread didn't finish.");
        return thrown;
    }
}
