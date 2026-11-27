namespace BrutalEngine.World.Ticks;

/// <summary>
/// Wrap-safe change-tick arithmetic (docs/design/world.md, section 5, "Wrap-safe comparison"): the newer-than test
/// and the clamp floors applied whenever the world's change tick reaches a multiple of <see cref="ClampInterval"/>.
/// </summary>
/// <remarks>
/// Ticks are <see cref="uint"/> values that wrap from <see cref="uint.MaxValue"/> to 0, so they are compared by the
/// signed difference: a tick is newer than another when it is ahead by 1 to 2³¹ - 1 ticks. The world's clamp keeps
/// every stored tick within that range of its change tick. Every member is pure, allocates nothing and is safe on any
/// thread.
/// </remarks>
internal static class TickMath
{
    /// <summary>The clamp period, 2³⁰ ticks: the world clamps old ticks whenever its change tick is a multiple of it.</summary>
    public const uint ClampInterval = 1u << 30;

    /// <summary>Returns whether <paramref name="tick"/> is newer than <paramref name="since"/>.</summary>
    /// <param name="tick">A stamp, such as a chunk's <c>Changed</c> tick.</param>
    /// <param name="since">A consumer's last-run tick.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="tick"/> is ahead of <paramref name="since"/> by 1 to 2³¹ - 1 ticks,
    /// across the wrap from <see cref="uint.MaxValue"/> to 0 too; <see langword="false"/> when they are equal or
    /// <paramref name="tick"/> is behind.
    /// </returns>
    public static bool IsNewer(uint tick, uint since) => unchecked((int)(tick - since)) > 0;

    /// <summary>Returns whether <paramref name="tick"/> is older than <paramref name="floor"/>, by the same wrap-safe compare.</summary>
    /// <param name="tick">A stored tick.</param>
    /// <param name="floor">A clamp floor.</param>
    /// <returns><see langword="true"/> when <paramref name="floor"/> is newer than <paramref name="tick"/>.</returns>
    public static bool IsOlderThanFloor(uint tick, uint floor) => IsNewer(floor, tick);

    /// <summary>Returns whether reaching <paramref name="changeTick"/> triggers a clamp: it is a multiple of <see cref="ClampInterval"/>.</summary>
    /// <param name="changeTick">The world's change tick, just advanced.</param>
    /// <returns><see langword="true"/> for 0, 2³⁰, 2³¹ and 3 x 2³⁰.</returns>
    public static bool IsClampTick(uint changeTick) => (changeTick & (ClampInterval - 1)) == 0;

    /// <summary>The floor that stamps (chunk ticks) older than it are raised to: <c>changeTick - 2³⁰ + 1</c>.</summary>
    /// <param name="changeTick">The world's change tick at the clamp.</param>
    /// <returns>The stamp floor, wrapped.</returns>
    public static uint StampFloor(uint changeTick) => unchecked(changeTick - ClampInterval + 1);

    /// <summary>
    /// The floor that consumers' last-run ticks older than the stamp floor are raised to: one less than
    /// <see cref="StampFloor"/>, <c>changeTick - 2³⁰</c>, so a clamped consumer stays strictly below every clamped
    /// stamp and a change pending against it stays newer.
    /// </summary>
    /// <param name="changeTick">The world's change tick at the clamp.</param>
    /// <returns>The consumer floor, wrapped.</returns>
    public static uint ConsumerFloor(uint changeTick) => unchecked(changeTick - ClampInterval);
}
