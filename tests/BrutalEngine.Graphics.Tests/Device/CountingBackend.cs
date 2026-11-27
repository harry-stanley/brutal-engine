using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.TestUtilities;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// A backend over the fake that counts its calls, keeps the last enumeration request, wraps each device it creates in a <see cref="CountingBackendDevice"/>,
/// and is disposable, so a test can see whether the front end ever disposes a backend or calls it before a check.
/// </summary>
internal sealed class CountingBackend(FakeGraphicsBackend inner) : IGraphicsBackend, IDisposable
{
    public int EnumerateCalls { get; private set; }

    public int CreateCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public CountingBackendDevice? LastDevice { get; private set; }

    public BackendDeviceRequest? LastRequest { get; private set; }

    public string Name => inner.Name;

    public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request)
    {
        EnumerateCalls++;
        LastRequest = request;
        return inner.EnumerateAdapters(request);
    }

    public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects)
    {
        CreateCalls++;
        Result<IBackendDevice> created = inner.CreateDevice(request, adapterIndex, objects);
        if (!created.TryGetValue(out IBackendDevice? device))
        {
            return created;
        }

        LastDevice = new CountingBackendDevice(device);
        return new Result<IBackendDevice>(LastDevice);
    }

    public void Dispose() => DisposeCalls++;
}
