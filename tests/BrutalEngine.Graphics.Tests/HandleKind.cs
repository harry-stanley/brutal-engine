using System;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Tests;

/// <summary>
/// One GPU handle kind under test. Its members are reached through delegates, so every test in
/// <see cref="HandleKindTests"/> runs over every kind without knowing the concrete type.
/// </summary>
internal abstract class HandleKind
{
    protected HandleKind(string name) => Name = name;

    /// <summary>The kind name that <c>ToString</c> prints, e.g. <c>Buffer</c>.</summary>
    public string Name { get; }

    public abstract Type Type { get; }

    /// <summary><c>IsNull</c> of the kind's <c>default</c> value.</summary>
    public abstract bool DefaultIsNull { get; }

    /// <summary>The Core handle inside the kind's <c>default</c> value.</summary>
    public abstract Handle DefaultValue { get; }

    /// <summary><c>ToString</c> of the kind's <c>default</c> value.</summary>
    public abstract string DefaultToString();

    /// <summary><c>IsNull</c> of a handle of this kind wrapping <paramref name="value"/>.</summary>
    public abstract bool IsNull(Handle value);

    /// <summary>Wraps <paramref name="value"/> and reads it back through the internal <c>Value</c>.</summary>
    public abstract Handle RoundTrip(Handle value);

    /// <summary><c>ToString</c> of a handle of this kind wrapping <paramref name="value"/>.</summary>
    public abstract string Format(Handle value);

    /// <summary>A boxed handle of this kind wrapping <paramref name="value"/>.</summary>
    public abstract object Box(Handle value);

    /// <summary><c>Equals(object)</c> of a handle of this kind wrapping <paramref name="value"/>.</summary>
    public abstract bool EqualsObject(Handle value, object? other);

    /// <summary>Compares handles of this kind wrapping <paramref name="left"/> and <paramref name="right"/> every way.</summary>
    public abstract HandleComparison Compare(Handle left, Handle right);

    public override string ToString() => Name;
}

/// <summary>A handle kind over its concrete type <typeparamref name="T"/>.</summary>
internal sealed class HandleKind<T> : HandleKind
    where T : struct, IEquatable<T>
{
    private readonly Func<Handle, T> _create;
    private readonly Func<T, Handle> _value;
    private readonly Func<T, bool> _isNull;
    private readonly Func<T, T, bool> _equalityOperator;
    private readonly Func<T, T, bool> _inequalityOperator;

    public HandleKind(
        string name,
        Func<Handle, T> create,
        Func<T, Handle> value,
        Func<T, bool> isNull,
        Func<T, T, bool> equalityOperator,
        Func<T, T, bool> inequalityOperator)
        : base(name)
    {
        _create = create;
        _value = value;
        _isNull = isNull;
        _equalityOperator = equalityOperator;
        _inequalityOperator = inequalityOperator;
    }

    public override Type Type => typeof(T);

    public override bool DefaultIsNull => _isNull(default);

    public override Handle DefaultValue => _value(default);

    public override string DefaultToString() => default(T).ToString()!;

    public override bool IsNull(Handle value) => _isNull(_create(value));

    public override Handle RoundTrip(Handle value) => _value(_create(value));

    public override string Format(Handle value) => _create(value).ToString()!;

    public override object Box(Handle value) => _create(value);

    public override bool EqualsObject(Handle value, object? other) => _create(value).Equals(other);

    public override HandleComparison Compare(Handle left, Handle right)
    {
        T a = _create(left);
        T b = _create(right);
        return new HandleComparison(
            a.Equals(b),
            a.Equals((object)b),
            _equalityOperator(a, b),
            _inequalityOperator(a, b),
            a.GetHashCode() == b.GetHashCode());
    }
}

/// <summary>Every way two handles of one kind compare: <c>Equals(T)</c>, <c>Equals(object)</c>, <c>==</c>, <c>!=</c> and hash codes.</summary>
internal readonly record struct HandleComparison(
    bool TypedEquals,
    bool ObjectEquals,
    bool EqualityOperator,
    bool InequalityOperator,
    bool SameHashCode);
