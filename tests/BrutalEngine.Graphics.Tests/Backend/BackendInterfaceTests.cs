using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The backend interface's contract types (docs/design/graphics.md, "The backend interface"): the interfaces' member
/// signatures, the frame plan and the device request.
/// </summary>
public sealed class BackendInterfaceTests : IDisposable
{
    private readonly Logger _logger = new(new LoggerOptions { Sinks = [] });

    public void Dispose() => _logger.Dispose();

    public static TheoryData<string> Interfaces() => [nameof(IGraphicsBackend), nameof(IBackendDevice)];

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void Interfaces_MatchDesign(string type)
    {
        // Written from the note's code block, in its order. IBackendDevice lacks the six pipeline and bind-group Create*
        // members until their descriptions exist (#118).
        (Type interfaceType, string[] expected, Type[] bases) = type switch
        {
            nameof(IGraphicsBackend) => (
                typeof(IGraphicsBackend),
                new[]
                {
                    "string Name { get; }",
                    "Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest)",
                    "Result<IBackendDevice> CreateDevice(BackendDeviceRequest, int, BackendObjectTable)",
                },
                Type.EmptyTypes),
            nameof(IBackendDevice) => (
                typeof(IBackendDevice),
                [
                    "GraphicsAdapterInfo Adapter { get; }",
                    "GraphicsCapabilities Capabilities { get; }",
                    "FormatSupport GetFormatSupport(TextureFormat)",
                    "Result<BackendBuffer> CreateBuffer(in BufferDescription, BackendMemory)",
                    "Result<ulong> CreateTexture(in TextureDescription)",
                    "Result<ulong> CreateTextureView(in TextureViewDescription)",
                    "Result<ulong> CreateSampler(in SamplerDescription)",
                    "void SetDebugName(BackendObjectKind, ulong, ReadOnlySpan<byte>)",
                    "void Destroy(BackendObjectKind, ulong)",
                    "Result<ulong> CreateSurface(WindowSurfaceSource)",
                    "void DestroySurface(ulong)",
                    "Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest)",
                    "void DestroySwapchain(ulong)",
                    "void GetSwapchainImage(ulong, int, out ulong, out ulong)",
                    "BackendAcquire AcquireImage(ulong, int)",
                    "BackendPresent Present(ulong, int)",
                    "Result WaitForFrame(int)",
                    "Result<bool> IsFrameComplete(int)",
                    "Result BeginFrame(int)",
                    "Result Execute(in FramePlan)",
                    "bool TryReadTimestamps(int, Span<ulong>)",
                    "Result WaitIdle()",
                    "long ValidationErrors { get; }",
                    "long ValidationWarnings { get; }",
                    "void InjectValidationError()",
                ],
                [typeof(IDisposable)]),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown interface."),
        };

        Assert.True(interfaceType.IsInterface);
        Assert.Equal(bases, interfaceType.GetInterfaces());
        Assert.Equal(expected.Order(StringComparer.Ordinal), Signatures(interfaceType).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FramePlan_IsRefStruct_WithInternalConstructorOnly()
    {
        Assert.True(typeof(FramePlan).IsByRefLike);
        Assert.Empty(typeof(FramePlan).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        ConstructorInfo constructor = Assert.Single(typeof(FramePlan).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.True(constructor.IsAssembly);
    }

    [Fact]
    public void FramePlan_Constructor_KeepsEveryField()
    {
        byte[] commands = [1, 2, 3];
        SwapchainImageUse[] acquired = [new(7, 0), new(9, 2)];

        var plan = new FramePlan(2, 41, commands, acquired);

        Assert.Equal((2, 41L), (plan.FrameSlot, plan.FrameNumber));
        Assert.True(plan.Commands == commands.AsSpan());
        Assert.True(plan.Acquired == acquired.AsSpan());
    }

    [Fact]
    public void FramePlan_Default_IsEmpty()
    {
        FramePlan plan = default;

        Assert.Equal((0, 0L), (plan.FrameSlot, plan.FrameNumber));
        Assert.True(plan.Commands.IsEmpty);
        Assert.True(plan.Acquired.IsEmpty);
    }

    [Fact]
    public void BackendDeviceRequest_Defaults_MatchDesign()
    {
        var request = new BackendDeviceRequest { Logger = _logger, Allocator = new HeapAllocator() };

        Assert.Null(request.CompatibleSurface);
        Assert.False(request.Validation);
        Assert.Equal(0, request.FramesInFlight);
        Assert.Equal((GraphicsFeatures.None, GraphicsFeatures.None), (request.RequiredFeatures, request.OptionalFeatures));
        Assert.Equal(TimeSpan.Zero, request.GpuTimeout);
        Assert.Equal("", request.ApplicationName);
    }

    [Fact]
    public void BackendDeviceRequest_ToString_UnderAnotherCulture_IsInvariant()
    {
        var request = new BackendDeviceRequest
        {
            Logger = _logger,
            Allocator = new HeapAllocator(),
            CompatibleSurface = new WindowSurfaceSource(new FakeWindow(), new FakeVulkanSurfaceSource([], (nint)1)),
            Validation = true,
            FramesInFlight = -2,
            RequiredFeatures = GraphicsFeatures.TimestampQueries,
            OptionalFeatures = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TextureCompressionBC,
            GpuTimeout = TimeSpan.FromMilliseconds(-1500),
            ApplicationName = "Test",
        };

        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish writes the minus sign as U+2212, so a culture-sensitive format of the negative values shows.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.NotEqual("-1", (-1).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(
                "BackendDeviceRequest { Logger = BrutalEngine.Core.Logging.Logger, Allocator = BrutalEngine.Core.Memory.HeapAllocator, "
                + "CompatibleSurface = BrutalEngine.Graphics.Backend.WindowSurfaceSource, Validation = True, FramesInFlight = -2, "
                + "RequiredFeatures = TimestampQueries, OptionalFeatures = SamplerAnisotropy, TextureCompressionBC, GpuTimeout = -00:00:01.5000000, "
                + "ApplicationName = Test }",
                request.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>Every member of <paramref name="type"/>, written as the note writes it, without parameter names.</summary>
    private static string[] Signatures(Type type)
    {
        string[] properties = type.GetProperties().Select(p => $"{Name(p.PropertyType)} {p.Name} {{ get; }}").ToArray();
        string[] methods = type.GetMethods()
            .Where(m => !m.IsSpecialName)
            .Select(m => $"{Name(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Parameter))})")
            .ToArray();
        Assert.All(type.GetProperties(), p => Assert.False(p.CanWrite));
        return [.. properties, .. methods];
    }

    private static string Parameter(ParameterInfo parameter)
    {
        if (!parameter.ParameterType.IsByRef)
        {
            return Name(parameter.ParameterType);
        }

        string modifier = parameter.IsOut ? "out" : parameter.IsIn ? "in" : "ref";
        return $"{modifier} {Name(parameter.ParameterType.GetElementType()!)}";
    }

    private static string Name(Type type)
    {
        string keyword = type switch
        {
            _ when type == typeof(void) => "void",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(byte) => "byte",
            _ when type == typeof(int) => "int",
            _ when type == typeof(long) => "long",
            _ when type == typeof(ulong) => "ulong",
            _ when type == typeof(string) => "string",
            _ => "",
        };
        if (keyword.Length > 0)
        {
            return keyword;
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }
}
