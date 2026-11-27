using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.TestUtilities;

namespace BrutalEngine.Graphics.Tests.Transfers;

/// <summary>The fake backend, with each device wrapped in a <see cref="MappedBufferDevice"/>.</summary>
internal sealed class MappedBufferBackend(FakeGraphicsBackend inner) : IGraphicsBackend
{
    public MappedBufferDevice? Device { get; private set; }

    public string Name => inner.Name;

    public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request) => inner.EnumerateAdapters(request);

    public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects)
    {
        Result<IBackendDevice> created = inner.CreateDevice(request, adapterIndex, objects);
        if (!created.TryGetValue(out IBackendDevice? device))
        {
            return created;
        }

        Device = new MappedBufferDevice(device);
        return new Result<IBackendDevice>(Device);
    }
}
