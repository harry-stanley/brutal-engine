using System;
using BrutalEngine.Core;

namespace BrutalEngine.World;

// The change tick: what writes and new rows are stamped with, and how it advances (docs/design/world.md, section 5,
// "Change detection").
public sealed partial class EcsWorld
{
    // The tick that new rows and writes are stamped with. Starts at 1; only Advance moves it on (and the test seam).
    private uint _changeTick = 1;

    /// <summary>
    /// The world's change tick: new rows and writes are stamped with it, and change filters compare stamps against a
    /// consumer's last-run tick. Starts at 1 and only ever advances by one.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// <para>
    /// The tick wraps from <see cref="uint.MaxValue"/> to 0, so ticks are compared by their signed difference, never
    /// as plain numbers: a stamp is newer than a last-run tick when it is ahead by 1 to 2³¹ - 1 ticks
    /// (docs/design/world.md, section 5, "Wrap-safe comparison").
    /// </para>
    /// <para>Allocates nothing. Not thread-safe: like every member, it is for the owner thread only.</para>
    /// </remarks>
    public uint ChangeTick
    {
        get
        {
            ThrowIfUnusable("EcsWorld.ChangeTick");
            return _changeTick;
        }
    }

    /// <summary>
    /// Advances <see cref="ChangeTick"/> by one, so writes after the call are newer than every last-run tick taken
    /// before it.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// From <see cref="uint.MaxValue"/> the tick wraps to 0. Allocates no managed memory and makes no allocator
    /// call.
    /// </remarks>
    public void AdvanceChangeTick()
    {
        ThrowIfUnusable("EcsWorld.AdvanceChangeTick");
        Advance();
    }

    /// <summary>
    /// Test seam: sets the change tick, so stamps can be told apart. The engine never calls it.
    /// </summary>
    /// <param name="tick">The new tick.</param>
    internal void SetChangeTickForTesting(uint tick)
    {
        ThrowIfUnusable("EcsWorld.SetChangeTickForTesting");
        _changeTick = tick;
    }

    /// <summary>
    /// The one advance step every advance path goes through: increments the change tick, wrapping. The caller has
    /// checked the thread and disposal.
    /// </summary>
    internal void Advance() => _changeTick = unchecked(_changeTick + 1);
}
