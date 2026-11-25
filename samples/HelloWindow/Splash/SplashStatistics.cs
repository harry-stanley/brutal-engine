using System;
using System.Globalization;

namespace HelloWindow.Splash;

/// <summary>What a run presented: splash frames, how long the splash lasted, and scene frames. Only presented frames
/// count.</summary>
/// <remarks>CI greps <see cref="Format"/>'s exact form and fails a run whose scene frame count is 0, so the scene is
/// exercised after the splash on every OS. Not thread-safe: the frame loop owns it.</remarks>
internal sealed class SplashStatistics
{
    private long _splashStart;
    private long _splashEnd;

    /// <summary>Gets the number of presented splash frames.</summary>
    public int SplashFrames { get; private set; }

    /// <summary>Gets the number of presented scene frames.</summary>
    public int SceneFrames { get; private set; }

    /// <summary>Records a presented frame. Doesn't allocate.</summary>
    /// <param name="splash">Whether the frame showed the splash.</param>
    /// <param name="timestamp">The monotonic clock when the frame was decided.</param>
    public void Presented(bool splash, long timestamp)
    {
        if (splash)
        {
            if (SplashFrames == 0)
            {
                _splashStart = timestamp;
            }

            SplashFrames++;
        }
        else
        {
            SceneFrames++;
        }

        // The splash lasts until the first scene frame; until then, it has lasted at least to this frame.
        if (SplashFrames > 0 && SceneFrames <= 1)
        {
            _splashEnd = timestamp;
        }
    }

    /// <summary>Gets the time from the first splash frame to the first scene frame, or to the last splash frame if the
    /// scene never started.</summary>
    /// <param name="ticksPerSecond">The clock's frequency.</param>
    /// <returns>The splash duration in seconds, or 0 if no splash frame was presented.</returns>
    public double SplashSeconds(long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        return SplashFrames == 0 ? 0 : (double)(_splashEnd - _splashStart) / ticksPerSecond;
    }

    /// <summary>Formats the statistics for the log.</summary>
    /// <param name="ticksPerSecond">The clock's frequency.</param>
    /// <returns><c>splash: X frames over Y.YY s, scene: Z frames</c>. Allocates: call it once, at exit.</returns>
    public string Format(long ticksPerSecond) => string.Create(
        CultureInfo.InvariantCulture,
        $"splash: {SplashFrames} frames over {SplashSeconds(ticksPerSecond):F2} s, scene: {SceneFrames} frames");
}
