using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.TestUtilities;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// A backend over the fake whose devices are <see cref="ReversedIdBackendDevice"/>s, so object ids fall in creation order.
/// The fake's device gets its own empty table to resolve view textures through; the front end keeps the one it passed in.
/// </summary>
internal sealed class ReversedIdBackend(FakeGraphicsBackend inner) : IGraphicsBackend
{
    public ReversedIdBackendDevice? LastDevice { get; private set; }

    public string Name => inner.Name;

    public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request) => inner.EnumerateAdapters(request);

    public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects)
    {
        var fakeObjects = new BackendObjectTable();
        Result<IBackendDevice> created = inner.CreateDevice(request, adapterIndex, fakeObjects);
        if (!created.TryGetValue(out IBackendDevice? device))
        {
            return created;
        }

        LastDevice = new ReversedIdBackendDevice(device, objects, fakeObjects);
        return new Result<IBackendDevice>(LastDevice);
    }
}
