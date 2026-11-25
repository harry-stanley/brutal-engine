using System;
using System.IO;
using System.Reflection;
using Brutal.Strings;

namespace BrutalEngine.Aot.Smoke;

/// <summary>
/// Proves that BRUTAL's string-builder pool runs on the centrally pinned <c>Microsoft.Extensions.ObjectPool</c>
/// 11.0.0 GA release (ADR-0011), under the JIT and NativeAOT alike.
/// </summary>
/// <remarks>
/// <para>
/// The only BRUTAL code that uses the pool is <c>Brutal.Strings.StringBuilderPool&lt;TChar&gt;</c>, a
/// <c>DefaultObjectPool&lt;StringBuilder&lt;TChar&gt;&gt;</c> (Brutal.Strings/StringBuilder/StringBuilderPool.cs:12,
/// <c>Shared</c> at :31). The harness's Vulkan scenario never touches it, so the probe rents and returns a builder
/// explicitly.
/// </para>
/// <para>
/// The assembly version alone can't tell the GA release from the preview BRUTAL declares: both builds of the package
/// have assembly version 11.0.0.0. The probe therefore also requires the informational version's package part (before
/// the <c>+</c> commit suffix) to be exactly <c>11.0.0</c>, which the preview's <c>11.0.0-preview.5.26302.115</c> isn't.
/// </para>
/// </remarks>
internal static class ObjectPoolProbe
{
    private const string ExpectedAssemblyName = "Microsoft.Extensions.ObjectPool";
    private const string ExpectedPackageVersion = "11.0.0";
    private const string ProbeText = "BrutalEngine.Aot.Smoke ObjectPool probe";

    /// <summary>
    /// Rents a builder from <c>StringBuilderPool&lt;char&gt;.Shared</c>, writes to it, returns it, and checks the
    /// loaded pool assembly.
    /// </summary>
    /// <param name="log">Receives one line with the loaded assembly's name, versions and the verdict.</param>
    /// <returns>
    /// <see cref="ExitCode.Pass"/> if the pool worked and its assembly is the 11.0.0 GA release, otherwise
    /// <see cref="ExitCode.SetupFailure"/> (as an <see cref="int"/>, the process exit code).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    public static int Run(TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            StringBuilderPool<char> pool = StringBuilderPool<char>.Shared;
            StringBuilder<char> builder = pool.Get();
            bool written;
            try
            {
                builder.Append(ProbeText.AsSpan());
                written = builder.WrittenSpan.SequenceEqual(ProbeText);
            }
            finally
            {
                pool.Return(builder);
            }

            // The pool's base class lives in the ObjectPool assembly; reaching it through BaseType keeps the harness
            // free of direct references to ObjectPool types (ADR-0011: never referenced directly).
            Assembly assembly = typeof(StringBuilderPool<char>).BaseType!.Assembly;
            AssemblyName name = assembly.GetName();
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string? package = informational?.Split('+')[0];

            bool pass = written
                && name.Name == ExpectedAssemblyName
                && name.Version == new Version(11, 0, 0, 0)
                && package == ExpectedPackageVersion;
            string verdict = pass
                ? "PROBE OK"
                : $"PROBE FAILED (expected {ExpectedAssemblyName} 11.0.0.0 from package {ExpectedPackageVersion}, builder written: {written})";
            log.WriteLine($"ObjectPool probe: rented and returned StringBuilderPool<char>.Shared; {name.Name} {name.Version} / {informational ?? "(no informational version)"}, {verdict}");
            return pass ? (int)ExitCode.Pass : (int)ExitCode.SetupFailure;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.WriteLine($"ObjectPool probe: PROBE FAILED: {exception}");
            return (int)ExitCode.SetupFailure;
        }
    }
}
