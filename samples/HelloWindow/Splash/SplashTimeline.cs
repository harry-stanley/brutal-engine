using System;

namespace HelloWindow.Splash;

/// <summary>Decides, frame by frame, whether the BRUTAL splash or the scene is shown. The splash comes first and can't
/// be cut short: nothing ends it before its minimum.</summary>
/// <remarks>
/// <para>
/// Windowed runs use a monotonic clock: the splash lasts until at least <see cref="MinimumSeconds"/> have passed since
/// the first frame was decided, however many frames that takes. Headless runs have no viewer and an unpredictable frame
/// rate, so they count frames instead: the first <see cref="HeadlessFrames"/> frames, the minimum at 60 frames a
/// second. That makes CI exercise the splash and the scene deterministically.
/// </para>
/// <para>Once the scene has started, the splash never comes back. Not thread-safe: the frame loop owns it. Never
/// allocates after construction.</para>
/// </remarks>
internal sealed class SplashTimeline
{
    /// <summary>The shortest time the splash is shown, in seconds (long enough to be legible, BRUTAL LICENSE 2.2).</summary>
    public const double MinimumSeconds = 2.0;

    /// <summary>The frame rate that headless runs assume when converting <see cref="MinimumSeconds"/> to frames.</summary>
    public const int HeadlessFramesPerSecond = 60;

    /// <summary>The number of splash frames in a headless run: ceil(<see cref="MinimumSeconds"/> x
    /// <see cref="HeadlessFramesPerSecond"/>).</summary>
    public const int HeadlessFrames = 120;

    // Headless timelines count frames; windowed ones time the splash with _minimumTicks.
    private readonly bool _headless;
    private readonly long _minimumTicks;
    private bool _started;
    private long _startTimestamp;
    private long _lastTimestamp;
    private int _lastFrame;

    private SplashTimeline(bool headless, long minimumTicks)
    {
        _headless = headless;
        _minimumTicks = minimumTicks;
    }

    /// <summary>Gets a value indicating whether the splash is over and the scene has started.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Creates the timeline of a windowed run, timed by a monotonic clock.</summary>
    /// <param name="ticksPerSecond">The clock's frequency, e.g. <see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    /// <returns>A timeline whose splash lasts at least <see cref="MinimumSeconds"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticksPerSecond"/> is not positive.</exception>
    public static SplashTimeline Windowed(long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);

        // Rounded up, so the splash is never shorter than the minimum (exact for whole-number frequencies).
        return new SplashTimeline(headless: false, (long)Math.Ceiling(MinimumSeconds * ticksPerSecond));
    }

    /// <summary>Creates the timeline of a headless run, counted in frames.</summary>
    /// <returns>A timeline whose splash lasts the first <see cref="HeadlessFrames"/> frames.</returns>
    public static SplashTimeline Headless() => new(headless: true, minimumTicks: 0);

    /// <summary>Decides what a frame shows. Call it once per frame, before recording it; asking again about the same
    /// frame (one that wasn't presented) is allowed.</summary>
    /// <param name="frame">The number of frames presented so far, so 0 for the first frame. Never decreases.</param>
    /// <param name="timestamp">The monotonic clock's current value, in the ticks given to <see cref="Windowed"/>.
    /// Never decreases. Headless timelines ignore it apart from that check.</param>
    /// <returns><see langword="true"/> to show the splash, <see langword="false"/> to show the scene.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is negative, or either argument is less
    /// than in the previous call.</exception>
    public bool ShowsSplash(int frame, long timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (_started)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(frame, _lastFrame);
            ArgumentOutOfRangeException.ThrowIfLessThan(timestamp, _lastTimestamp);
        }
        else
        {
            _started = true;
            _startTimestamp = timestamp;
        }

        _lastFrame = frame;
        _lastTimestamp = timestamp;
        if (!IsComplete)
        {
            IsComplete = _headless ? frame >= HeadlessFrames : timestamp - _startTimestamp >= _minimumTicks;
        }

        return !IsComplete;
    }
}
