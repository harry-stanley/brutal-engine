using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.Platform;
using BrutalEngine.TestUtilities;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary>
/// The detecting calls the device-loss tests drive, one per way a loss reaches the device, and what each must report
/// (docs/design/graphics.md section 12).
/// </summary>
internal static class LossKit
{
    /// <summary>
    /// Every detecting call: the fault-seam operation it gates, the public member the event names, and whether it
    /// returns a <see cref="Result"/> (the rest report a status).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (FaultSeam.Operation Operation, string Detected)> Scenarios =
        new Dictionary<string, (FaultSeam.Operation, string)>
        {
            ["WaitForFrame"] = (FaultSeam.Operation.WaitForFrame, "BeginFrame"),
            ["BeginFrame"] = (FaultSeam.Operation.BeginFrame, "BeginFrame"),
            ["Execute"] = (FaultSeam.Operation.Execute, "SubmitFrame"),
            ["Present"] = (FaultSeam.Operation.Present, "SubmitFrame"),
            ["AcquireImage"] = (FaultSeam.Operation.AcquireImage, "AcquireSwapchainImage"),
            ["WaitIdle"] = (FaultSeam.Operation.WaitIdle, "WaitIdle"),
            ["RecreationWait"] = (FaultSeam.Operation.WaitIdle, "WaitIdle"),
            ["CreateBuffer"] = (FaultSeam.Operation.CreateBuffer, "CreateBuffer"),
            ["CreateTexture"] = (FaultSeam.Operation.CreateTexture, "CreateTexture"),
            ["DefaultView"] = (FaultSeam.Operation.CreateTextureView, "CreateTexture"),
            ["CreateTextureView"] = (FaultSeam.Operation.CreateTextureView, "CreateTextureView"),
            ["CreateSampler"] = (FaultSeam.Operation.CreateSampler, "CreateSampler"),
            ["CreateSurface"] = (FaultSeam.Operation.CreateSurface, "CreateSwapchain"),
            ["CreateSwapchain"] = (FaultSeam.Operation.CreateSwapchain, "CreateSwapchain"),
            ["Recreation"] = (FaultSeam.Operation.CreateSwapchain, "AcquireSwapchainImage"),
            ["WriteBuffer"] = (FaultSeam.Operation.CreateBuffer, "WriteBuffer"),
            ["WriteTexture"] = (FaultSeam.Operation.CreateBuffer, "WriteTexture"),
            ["AllocateTransient"] = (FaultSeam.Operation.CreateBuffer, "AllocateTransient"),
        };

    /// <summary>The scenarios of creations and writes: each returns a <see cref="Result"/>, never a frame status.</summary>
    public static readonly string[] Creations =
    [
        "CreateBuffer", "CreateTexture", "DefaultView", "CreateTextureView", "CreateSampler", "CreateSurface", "CreateSwapchain",
        "WriteBuffer", "WriteTexture", "AllocateTransient",
    ];

    /// <summary>The fake backend's operation of the same name as a seam operation.</summary>
    public static FakeBackendOperation Fake(FaultSeam.Operation operation) => Enum.Parse<FakeBackendOperation>(operation.ToString());

    /// <summary>How many calls of an operation the fake backend device received.</summary>
    public static int Count(FakeBackendDevice backend, FaultSeam.Operation operation) =>
        backend.Calls.Count(call => call.Operation == Fake(operation));
}

/// <summary>
/// One device with a swapchain on its compatible window, a live buffer and texture, and the DeviceLost events it raised.
/// </summary>
internal sealed class LossRig
{
    public LossRig(DeviceFixture fixture)
    {
        Fixture = fixture;
        Window = Window();
        Device = NewDevice(fixture, Window);
        Backend = fixture.BackendDevice;
        Swapchain = Device.CreateSwapchain(Window, new SwapchainOptions { DebugName = "main" }).Value;
        Buffer = Device.CreateBuffer(new BufferDescription { Size = 256, Usage = BufferUsage.CopyDestination | BufferUsage.CopySource }).Value;
        Texture = Device.CreateTexture(TextureDescription).Value;
        Device.DeviceLost += (sender, args) =>
        {
            Events.Add((sender, args.Operation, args.NativeCode, args.TimedOut));
            // At the end of the detecting call: the device already reports the loss.
            LostInHandler.Add(Device.IsLost);
        };
    }

    public static TextureDescription TextureDescription =>
        new() { Format = TextureFormat.Rgba8Unorm, Width = 4, Height = 4, Usage = TextureUsage.Sampled | TextureUsage.CopyDestination };

    public DeviceFixture Fixture { get; }

    public FakeWindow Window { get; }

    public GraphicsDevice Device { get; }

    public FakeBackendDevice Backend { get; }

    public SwapchainHandle Swapchain { get; }

    public BufferHandle Buffer { get; }

    public TextureHandle Texture { get; }

    public List<(object? Sender, string Operation, int NativeCode, bool TimedOut)> Events { get; } = [];

    public List<bool> LostInHandler { get; } = [];

    /// <summary>The frame the last <see cref="Run"/> began for its scenario, for a test whose detecting call threw.</summary>
    public CommandList OpenFrame { get; private set; }

    /// <summary>A frame that acquires the swapchain and submits, with its statuses.</summary>
    public (FrameStatus Begun, AcquireStatus Acquired, FrameStatus Submitted) Frame()
    {
        FrameStatus begun = Device.BeginFrame(out CommandList commands);
        AcquireStatus acquired = commands.AcquireSwapchainImage(Swapchain, out _);
        return (begun, acquired, Device.SubmitFrame(commands));
    }

    /// <summary>
    /// Arms <paramref name="loss"/> for the scenario's operation, then makes the scenario's detecting call. Returns what
    /// it returned (a status or a result), how many calls of the gated operation reached the backend during it, and how
    /// many DeviceLost events had been raised when it returned (before a frame it left open is closed).
    /// </summary>
    public (object Returned, int BackendCalls, int Events) Run(string scenario, in EngineError loss)
    {
        FaultSeam.Operation operation = LossKit.Scenarios[scenario].Operation;
        CommandList commands = default;
        // The setup each detecting call needs, before the loss is armed.
        switch (scenario)
        {
            case "Execute" or "AcquireImage" or "Present" or "Recreation" or "RecreationWait" or "AllocateTransient":
                if (scenario.StartsWith("Recreation", StringComparison.Ordinal))
                {
                    Window.PixelSize = new WindowSize(800, 600);
                }

                _ = Device.BeginFrame(out commands);
                OpenFrame = commands;
                if (scenario == "Present")
                {
                    _ = commands.AcquireSwapchainImage(Swapchain, out _);
                }

                break;
            case "WaitForFrame" or "BeginFrame":
                _ = Frame();
                break;
            default:
                break;
        }

        int before = LossKit.Count(Backend, operation);
        Device.Faults.InjectLoss(operation, 1, loss);
        object returned = scenario switch
        {
            "WaitForFrame" or "BeginFrame" => Device.BeginFrame(out commands),
            "Execute" or "Present" => Device.SubmitFrame(commands),
            "AcquireImage" or "Recreation" or "RecreationWait" => commands.AcquireSwapchainImage(Swapchain, out _),
            "WaitIdle" => Device.WaitIdle(),
            _ => Creation(scenario, commands),
        };
        int calls = LossKit.Count(Backend, operation) - before;
        int events = Events.Count;

        // Close a frame the scenario left open, so every scenario ends between frames.
        if (scenario is "WaitForFrame" or "BeginFrame" or "AcquireImage" or "Recreation" or "RecreationWait" or "AllocateTransient")
        {
            _ = Device.SubmitFrame(commands);
        }

        return (returned, calls, events);
    }

    /// <summary>A creation or write scenario's call; a transient allocation goes into <paramref name="commands"/>' frame.</summary>
    public Result Creation(string scenario, CommandList commands) => scenario switch
    {
        "CreateBuffer" => Device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).ToResult(),
        "CreateTexture" or "DefaultView" => Device.CreateTexture(TextureDescription).ToResult(),
        "CreateTextureView" => Device.CreateTextureView(new TextureViewDescription { Texture = Texture }).ToResult(),
        "CreateSampler" => Device.CreateSampler(new SamplerDescription()).ToResult(),
        // A second window: the compatible window already has its swapchain.
        "CreateSurface" or "CreateSwapchain" => Device.CreateSwapchain(Window(), new SwapchainOptions()).ToResult(),
        "WriteBuffer" => Device.WriteBuffer(Buffer, 0, [1, 2, 3]),
        "WriteTexture" => Device.WriteTexture(new TextureWrite { Target = new(Texture), Region = new(0, 0, 1, 1) }, [1, 2, 3, 4]),
        "AllocateTransient" => commands.AllocateTransient(16, TransientUsage.Vertex, out _),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };
}
