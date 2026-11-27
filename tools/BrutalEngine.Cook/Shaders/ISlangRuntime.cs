using Brutal.SlangApi;

namespace BrutalEngine.Cook.Shaders;

/// <summary>
/// The two Slang calls that bracket a global session's life: the seam the compiler's tests count releases through.
/// <see cref="SlangRuntime"/> is the real one.
/// </summary>
internal interface ISlangRuntime
{
    /// <summary>Creates a Slang global session.</summary>
    /// <param name="session">The new session; null when the call fails.</param>
    /// <returns>Slang's result code.</returns>
    /// <exception cref="System.TypeInitializationException">The Slang native library failed to load.</exception>
    public Result TryCreateGlobalSession(out IGlobalSession? session);

    /// <summary>Releases a session returned by <see cref="TryCreateGlobalSession"/>. The session is unusable afterwards.</summary>
    /// <param name="session">The session to release.</param>
    public void Release(IGlobalSession session);
}
