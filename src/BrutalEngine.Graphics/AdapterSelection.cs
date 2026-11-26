using System;
using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>Which adapter (GPU) a graphics device should use: automatic, or an override by index, name or type.</summary>
/// <remarks>
/// <para>
/// The backend applies the selection when it creates a device: it narrows the suitable adapters to the ones the
/// override matches, then ranks them as it does for <see cref="Auto"/>. An override that matches no suitable adapter
/// fails device creation; it never falls back to another adapter (docs/design/graphics.md section 2).
/// </para>
/// <para>
/// Hosts read overrides from text (the <c>--gpu</c> option and the <c>graphics.adapter</c> config key) with
/// <see cref="TryParse"/>; <see cref="ToString"/> returns the same form, so the two round-trip.
/// <c>default(AdapterSelection)</c> is <see cref="Auto"/>.
/// </para>
/// <para>
/// Equality compares the kind and its argument; names compare ordinally, case included. An immutable value that is
/// safe to copy and use from any thread. The factories and <see cref="TryParse"/> allocate nothing beyond the name
/// substring <see cref="TryParse"/> copies out of its input; <see cref="ToString"/> allocates. Selection happens once
/// per device creation, never in a frame.
/// </para>
/// </remarks>
public readonly record struct AdapterSelection
{
    private const string AutoText = "auto";
    private const string IndexPrefix = "index:";
    private const string NamePrefix = "name:";
    private const string TypePrefix = "type:";

    private AdapterSelection(AdapterSelectionKind kind, int index, string? name, AdapterType type)
    {
        Kind = kind;
        Index = index;
        Name = name;
        Type = type;
    }

    /// <summary>The backend's own choice: the best suitable adapter, discrete before integrated, virtual and CPU.</summary>
    /// <value>The same value as <c>default(AdapterSelection)</c>.</value>
    public static AdapterSelection Auto => default;

    /// <summary>Which form of selection this is.</summary>
    internal AdapterSelectionKind Kind { get; }

    /// <summary>The adapter's enumeration index for <see cref="AdapterSelectionKind.Index"/>; otherwise <c>0</c>.</summary>
    internal int Index { get; }

    /// <summary>The name substring for <see cref="AdapterSelectionKind.Name"/>; otherwise <see langword="null"/>.</summary>
    internal string? Name { get; }

    /// <summary>The adapter type for <see cref="AdapterSelectionKind.Type"/>; otherwise <see cref="AdapterType.Other"/>.</summary>
    internal AdapterType Type { get; }

    /// <summary>Selects the adapter at an enumeration index, as listed by the backend's adapter enumeration.</summary>
    /// <param name="index">The zero-based index of the adapter.</param>
    /// <returns>The selection. Allocates nothing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public static AdapterSelection ByIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new AdapterSelection(AdapterSelectionKind.Index, index, null, AdapterType.Other);
    }

    /// <summary>Selects the adapters whose name contains a substring, compared ordinally and ignoring case.</summary>
    /// <param name="nameSubstring">
    /// The text to look for, kept verbatim: whitespace is part of it. The selection keeps a reference to the string.
    /// </param>
    /// <returns>The selection. Allocates nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nameSubstring"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="nameSubstring"/> is empty.</exception>
    public static AdapterSelection ByName(string nameSubstring)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameSubstring);
        return new AdapterSelection(AdapterSelectionKind.Name, 0, nameSubstring, AdapterType.Other);
    }

    /// <summary>Selects the adapters of one type.</summary>
    /// <param name="type">The adapter type.</param>
    /// <returns>The selection. Allocates nothing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> isn't a defined <see cref="AdapterType"/> value.</exception>
    public static AdapterSelection ByType(AdapterType type)
    {
        // A range check rather than Enum.IsDefined, which allocates when its reflection cache is rebuilt after a GC.
        if (type is < AdapterType.Other or > AdapterType.Cpu)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Not a defined AdapterType value.");
        }

        return new AdapterSelection(AdapterSelectionKind.Type, 0, null, type);
    }

    /// <summary>Parses a selection from its text form, as the host reads it from the command line or config.</summary>
    /// <remarks>
    /// The accepted forms are exact and case-sensitive, with no surrounding whitespace: <c>auto</c>; <c>index:</c>
    /// followed by a non-negative decimal integer written in ASCII digits with no sign or whitespace (leading zeros
    /// are accepted, so <c>index:007</c> is index 7 and <see cref="ToString"/> gives <c>index:7</c>; a value above
    /// <see cref="int.MaxValue"/> returns <see langword="false"/>);
    /// <c>name:</c> followed by non-empty text, taken verbatim (spaces, colons and commas included); and
    /// <c>type:</c> followed by exactly one of <c>other</c>, <c>discrete</c>, <c>integrated</c>, <c>virtual</c>
    /// or <c>cpu</c>. Type names are matched against that list, so numeric values, other casings and comma lists
    /// are rejected. Anything else returns <see langword="false"/>.
    /// </remarks>
    /// <param name="text">The text to parse. <see langword="null"/> is rejected.</param>
    /// <param name="selection">
    /// The parsed selection; <see cref="Auto"/> (the default value) when the method returns <see langword="false"/>.
    /// </param>
    /// <returns><see langword="true"/> if <paramref name="text"/> is one of the accepted forms.</returns>
    public static bool TryParse(string? text, out AdapterSelection selection)
    {
        selection = default;
        if (text is null)
        {
            return false;
        }

        if (text.Equals(AutoText, StringComparison.Ordinal))
        {
            return true;
        }

        if (text.StartsWith(IndexPrefix, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> digits = text.AsSpan(IndexPrefix.Length);
            if (digits.IsEmpty || !IsAsciiDigits(digits)
                || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                return false;
            }

            selection = ByIndex(index);
            return true;
        }

        if (text.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            if (text.Length == NamePrefix.Length)
            {
                return false;
            }

            selection = ByName(text[NamePrefix.Length..]);
            return true;
        }

        if (text.StartsWith(TypePrefix, StringComparison.Ordinal) && TryParseType(text.AsSpan(TypePrefix.Length), out AdapterType type))
        {
            selection = ByType(type);
            return true;
        }

        return false;
    }

    /// <summary>Returns the text form <see cref="TryParse"/> accepts: <c>auto</c>, <c>index:N</c>, <c>name:text</c> or <c>type:name</c>.</summary>
    /// <returns>The selection's text form. Allocates.</returns>
    public override string ToString() => Kind switch
    {
        AdapterSelectionKind.Index => string.Create(CultureInfo.InvariantCulture, $"{IndexPrefix}{Index}"),
        AdapterSelectionKind.Name => NamePrefix + Name,
        AdapterSelectionKind.Type => TypePrefix + TypeName(Type),
        _ => AutoText,
    };

    // Defence in depth beside NumberStyles.None (which already rejects signs, whitespace and non-ASCII digits): it
    // states the documented contract here, so a later change to the number style can't widen what TryParse accepts.
    private static bool IsAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    // Exact matches against the documented names, never Enum.TryParse: that accepts numbers, any casing, whitespace
    // and comma lists, so a typo could select a different real type.
    private static bool TryParseType(ReadOnlySpan<char> text, out AdapterType type)
    {
        switch (text)
        {
            case "other":
                type = AdapterType.Other;
                return true;
            case "discrete":
                type = AdapterType.Discrete;
                return true;
            case "integrated":
                type = AdapterType.Integrated;
                return true;
            case "virtual":
                type = AdapterType.Virtual;
                return true;
            case "cpu":
                type = AdapterType.Cpu;
                return true;
            default:
                type = AdapterType.Other;
                return false;
        }
    }

    private static string TypeName(AdapterType type) => type switch
    {
        AdapterType.Discrete => "discrete",
        AdapterType.Integrated => "integrated",
        AdapterType.Virtual => "virtual",
        AdapterType.Cpu => "cpu",
        _ => "other",
    };
}
