using System.Collections.Generic;
using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>A description of one adapter (GPU) the backend enumerated, and whether it can run the engine.</summary>
/// <remarks>
/// <para>
/// The backend builds these during adapter enumeration and device creation; applications read them, for example to
/// list GPUs for a <c>--gpu</c> override. All properties are required, so a value is always complete.
/// </para>
/// <para>
/// Immutable once constructed and safe to read from any thread. <see cref="RejectionReasons"/> must not be changed
/// after construction: the backend passes a list it never touches again. Equality is the record's: strings compare
/// ordinally and <see cref="RejectionReasons"/> compares by reference, so two descriptions of the same adapter built
/// from separate lists are not equal. Identify an adapter by <see cref="Index"/>, not with <c>==</c>. No member
/// allocates, except
/// <see cref="ToString"/>, which formats with the invariant culture.
/// </para>
/// </remarks>
public sealed record GraphicsAdapterInfo
{
    /// <summary>The adapter's zero-based position in the backend's enumeration, as used by <see cref="AdapterSelection.ByIndex"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The adapter's name as the driver reports it, for example <c>NVIDIA GeForce RTX 2070 SUPER</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The kind of adapter.</summary>
    public required AdapterType Type { get; init; }

    /// <summary>The driver's name from a fixed table, for example <c>NvidiaProprietary</c>, <c>MesaLlvmpipe</c> or <c>Moltenvk</c>.</summary>
    public required string Driver { get; init; }

    /// <summary>The graphics API and the version the adapter supports, for example <c>Vulkan 1.3.280</c>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>
    /// Why the adapter can't be used, one reason per entry; empty if it is suitable. Read-only: never modify the list.
    /// </summary>
    public required IReadOnlyList<string> RejectionReasons { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>GraphicsAdapterInfo { Index = 0, Name = llvmpipe, Type = Cpu, Driver = MesaLlvmpipe, ApiVersion = Vulkan 1.3.280, RejectionReasons = [] }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"GraphicsAdapterInfo {{ Index = {Index}, Name = {Name}, Type = {Type}, Driver = {Driver}, ApiVersion = {ApiVersion}, RejectionReasons = [{string.Join(", ", RejectionReasons)}] }}");
}
