using System;
using System.Text;

namespace BrutalEngine.World;

/// <summary>
/// A component type's identity across processes and builds: the 64-bit FNV-1a hash of its full metadata name, encoded
/// as UTF-8.
/// </summary>
/// <param name="Value">The hash.</param>
/// <remarks>
/// <para>
/// Serialised data names component types by stable id, never by the dense <c>ComponentId</c> a registry
/// issues. The schema version is kept separately in <c>ComponentInfo.SchemaVersion</c>, so a type keeps its
/// stable id when its layout changes. The component generator computes the same value at compile time; both pin the
/// same known vectors in tests.
/// </para>
/// <para>
/// Equality compares <see cref="Value"/>. <c>default(StableComponentId)</c> has <see cref="Value"/> <c>0</c>, which no
/// name is known to hash to but which isn't reserved either. Immutable and safe to use from any thread. No member
/// allocates, except <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct StableComponentId(ulong Value)
{
    private const ulong OffsetBasis = 0xCBF29CE484222325;
    private const ulong Prime = 0x100000001B3;

    /// <summary>Computes the stable id of a full metadata name.</summary>
    /// <param name="fullName">
    /// The type's metadata name, as <c>Type.FullName</c> gives it: namespace and type, with <c>+</c> between a
    /// nested type and its declaring type, for example <c>MyGame.Velocity</c> or <c>MyGame.Outer+Inner</c>.
    /// </param>
    /// <returns>
    /// FNV-1a 64 (offset basis <c>0xcbf29ce484222325</c>, prime <c>0x100000001b3</c>) over the UTF-8 encoding of
    /// <paramref name="fullName"/>. A lone surrogate, which has no UTF-8 encoding, is encoded as U+FFFD (the bytes
    /// <c>EF BF BD</c>), as <see cref="Rune"/> enumeration and <see cref="Encoding.UTF8"/> do; the generator does the same.
    /// </returns>
    /// <remarks>Thread-safe. Allocates nothing: it encodes one scalar value at a time on the stack.</remarks>
    public static StableComponentId FromFullName(ReadOnlySpan<char> fullName)
    {
        ulong hash = OffsetBasis;
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in fullName.EnumerateRunes())
        {
            // Enumeration yields Rune.ReplacementChar for each lone surrogate. Every Rune encodes to 1-4 UTF-8 bytes,
            // which fit the buffer, so the encode can't fail.
            int length = rune.EncodeToUtf8(utf8);
            for (int i = 0; i < length; i++)
            {
                hash = (hash ^ utf8[i]) * Prime;
            }
        }

        return new StableComponentId(hash);
    }
}
