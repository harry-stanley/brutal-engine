using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>The command stream's record kinds (docs/design/graphics.md, "The frame plan").</summary>
public sealed class CommandKindTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        // Written from the note's CommandKind block, in its order, numbered from 0: the values are the stream format.
        (string Name, int Value)[] expected =
        [
            ("Barrier", 0), ("CopyBufferToBuffer", 1), ("CopyBufferToTexture", 2), ("CopyTextureToBuffer", 3),
            ("CopyTextureToTexture", 4), ("BeginRenderPass", 5), ("EndRenderPass", 6), ("BeginComputePass", 7),
            ("EndComputePass", 8), ("SetPipeline", 9), ("SetBindGroup", 10), ("SetVertexBuffer", 11), ("SetIndexBuffer", 12),
            ("SetViewport", 13), ("SetScissor", 14), ("PushConstants", 15), ("Draw", 16), ("DrawIndexed", 17), ("Dispatch", 18),
            ("PushDebugGroup", 19), ("PopDebugGroup", 20), ("ClearSwapchainImage", 21),
        ];

        // Looked up by name, so a missing type is a test failure rather than a build break.
        Type? kind = typeof(GraphicsDeviceOptions).Assembly.GetType("BrutalEngine.Graphics.Backend.CommandKind");
        Assert.NotNull(kind);
        Assert.True(kind.IsPublic);
        Assert.True(kind.IsEnum);
        Assert.False(kind.IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(ushort), Enum.GetUnderlyingType(kind));
        Assert.Equal(expected, Enum.GetValuesAsUnderlyingType(kind).Cast<ushort>().Select(v => (Enum.GetName(kind, v)!, (int)v)));
    }
}
