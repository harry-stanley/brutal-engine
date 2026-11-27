using Brutal.SlangApi;

namespace BrutalEngine.Cook.Shaders;

/// <summary>The real <see cref="ISlangRuntime"/>: BRUTAL's <c>Slang.TryCreateGlobalSession</c> and <c>Slang.Release</c>.</summary>
/// <remarks>
/// Global sessions are COM proxies that the garbage collector never releases (Brutal.Slang/Slang.cs:5-16), so every
/// session must reach <see cref="Release"/>.
/// </remarks>
internal sealed class SlangRuntime : ISlangRuntime
{
    /// <inheritdoc/>
    public Result TryCreateGlobalSession(out IGlobalSession? session) =>
        Slang.TryCreateGlobalSession(Constants.ApiVersion, out session);

    /// <inheritdoc/>
    public void Release(IGlobalSession session) => Slang.Release(session);
}
