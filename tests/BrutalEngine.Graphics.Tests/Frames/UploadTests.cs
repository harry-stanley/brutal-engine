using System;
using System.Linq;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Recording;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Frames;

/// <summary>Buffer and texture writes through the upload ring and pending upload blocks (docs/design/graphics.md section 6).</summary>
public sealed class UploadTests
{
    [Fact]
    public void Planner_UploadsBeforeRecordedCommands()
    {
        var allocator = new TrackingAllocator(new HeapAllocator(), "planner");
        using (var uploads = new CommandWriter(allocator))
        using (var recorded = new CommandWriter(allocator))
        using (var planner = new FramePlanner(allocator))
        {
            recorded.Append(CommandKind.Draw, new DrawCommand { Arguments = new DrawArguments(3) });
            Assert.True(planner.Plan(1, 5, uploads, recorded).Commands.SequenceEqual(recorded.Written));
            uploads.Append(CommandKind.CopyBufferToBuffer, new CopyBufferToBufferCommand { Source = 1, Destination = 2, Size = 4 });

            FramePlan plan = planner.Plan(1, 5, uploads, recorded);

            Assert.Equal((1, 5L), (plan.FrameSlot, plan.FrameNumber));
            var reader = new CommandReader(plan.Commands);
            Assert.True(reader.MoveNext());
            Assert.Equal(CommandKind.CopyBufferToBuffer, reader.Kind);
            Assert.True(reader.MoveNext());
            Assert.Equal((CommandKind.Draw, 3), (reader.Kind, reader.Payload<DrawCommand>().Arguments.VertexCount));
            Assert.False(reader.MoveNext());

            // Only uploads: the plan is the upload stream itself.
            recorded.Reset();
            Assert.True(planner.Plan(0, 6, uploads, recorded).Commands.SequenceEqual(uploads.Written));
        }

        allocator.ThrowIfLeaks();
    }
}
