namespace BrutalEngine.TestUtilities;

/// <summary>
/// Names each call the fake backend can receive: the <see cref="FakeGraphicsBackend"/> methods and every
/// <see cref="FakeBackendDevice"/> method of the backend interface. Recorded in <see cref="FakeBackendCall.Operation"/>, and
/// the target of <see cref="FakeBackendScript.FailNext"/>.
/// </summary>
public enum FakeBackendOperation
{
    /// <summary><see cref="FakeGraphicsBackend.EnumerateAdapters"/>.</summary>
    EnumerateAdapters = 0,

    /// <summary><see cref="FakeGraphicsBackend.CreateDevice"/>.</summary>
    CreateDevice = 1,

    /// <summary><see cref="FakeBackendDevice.GetFormatSupport"/>.</summary>
    GetFormatSupport = 2,

    /// <summary><see cref="FakeBackendDevice.CreateBuffer"/>.</summary>
    CreateBuffer = 3,

    /// <summary><see cref="FakeBackendDevice.CreateTexture"/>.</summary>
    CreateTexture = 4,

    /// <summary><see cref="FakeBackendDevice.CreateTextureView"/>.</summary>
    CreateTextureView = 5,

    /// <summary><see cref="FakeBackendDevice.CreateSampler"/>.</summary>
    CreateSampler = 6,

    /// <summary><see cref="FakeBackendDevice.SetDebugName"/>.</summary>
    SetDebugName = 7,

    /// <summary><see cref="FakeBackendDevice.Destroy"/>.</summary>
    Destroy = 8,

    /// <summary><see cref="FakeBackendDevice.CreateSurface"/>.</summary>
    CreateSurface = 9,

    /// <summary><see cref="FakeBackendDevice.DestroySurface"/>.</summary>
    DestroySurface = 10,

    /// <summary><see cref="FakeBackendDevice.CreateSwapchain"/>.</summary>
    CreateSwapchain = 11,

    /// <summary><see cref="FakeBackendDevice.DestroySwapchain"/>.</summary>
    DestroySwapchain = 12,

    /// <summary><see cref="FakeBackendDevice.GetSwapchainImage"/>.</summary>
    GetSwapchainImage = 13,

    /// <summary><see cref="FakeBackendDevice.AcquireImage"/>.</summary>
    AcquireImage = 14,

    /// <summary><see cref="FakeBackendDevice.Present"/>.</summary>
    Present = 15,

    /// <summary><see cref="FakeBackendDevice.WaitForFrame"/>.</summary>
    WaitForFrame = 16,

    /// <summary><see cref="FakeBackendDevice.IsFrameComplete"/>.</summary>
    IsFrameComplete = 17,

    /// <summary><see cref="FakeBackendDevice.BeginFrame"/>.</summary>
    BeginFrame = 18,

    /// <summary><see cref="FakeBackendDevice.Execute"/>.</summary>
    Execute = 19,

    /// <summary><see cref="FakeBackendDevice.TryReadTimestamps"/>.</summary>
    TryReadTimestamps = 20,

    /// <summary><see cref="FakeBackendDevice.WaitIdle"/>.</summary>
    WaitIdle = 21,

    /// <summary><see cref="FakeBackendDevice.InjectValidationError"/>.</summary>
    InjectValidationError = 22,

    /// <summary><see cref="FakeBackendDevice.Dispose"/> (its first call; later calls do nothing).</summary>
    Dispose = 23,
}
