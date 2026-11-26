using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.World.Internal;

namespace BrutalEngine.World;

/// <summary>
/// A world's component types: assigns each registered type a dense <see cref="ComponentId"/> and answers type and
/// stable-id lookups.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids.</b> Dense ids follow registration order, from <c>0</c>, and are meaningful only in this registry: two
/// worlds that register types in different orders give the same type different ids. Ids are never persisted;
/// serialisation uses <see cref="ComponentInfo.StableId"/>. A <see cref="ComponentId"/> from another registry is
/// misuse that is detected only when its index is at least <see cref="Count"/>; otherwise it names whichever type
/// has that index here.
/// </para>
/// <para>
/// <b>Registration.</b> <see cref="Register{T}"/> takes a type's size and alignment from the type itself, never from
/// its <see cref="IComponent.Info"/>, and checks them and the info against the registration rules: at most
/// 1,024 bytes, aligned to at most 64 bytes, a valid info (a schema version of at least 1, a non-empty full name, the
/// stable id of that name, and size 1 for a tag), and no other registered type with the same stable id. A rejected
/// registration throws and leaves the registry unchanged.
/// </para>
/// <para>
/// <b>Threading.</b> Owned by its world: every member checks that it is called on the world's owner thread and throws
/// <see cref="WrongThreadException"/> otherwise, and throws <see cref="ObjectDisposedException"/> once the world is
/// disposed. Worlds on different threads register types concurrently and independently.
/// </para>
/// <para>
/// <b>Allocation.</b> Registering a new type allocates (setup work): the registry's managed tables grow as types are
/// registered. Lookups, <see cref="Count"/>, <see cref="Capacity"/>, <see cref="GetComponentType"/> and registering
/// a type that is already registered allocate nothing.
/// </para>
/// </remarks>
public sealed class ComponentRegistry
{
    /// <summary>The largest component size, in bytes.</summary>
    internal const int MaxComponentSize = 1024;

    /// <summary>The largest component alignment, in bytes: the chunk alignment.</summary>
    internal const int MaxComponentAlignment = 64;

    private const int InitialTypeCapacity = 16;

    private readonly ThreadAffinity _owner;
    private readonly int _capacity;
    private readonly Dictionary<ulong, int> _idsByStableId = [];

    // Indexed by process-wide ordinal: the dense id + 1, or 0 when that type isn't registered here.
    private int[] _idsByOrdinal = [];
    private ComponentType[] _types;
    private int _count;
    private bool _disposed;

    /// <summary>Creates an empty registry owned by <paramref name="owner"/>.</summary>
    /// <param name="owner">The owner thread of the world that owns the registry.</param>
    /// <param name="capacity">The most types it holds: <see cref="WorldOptions.MaxComponentTypes"/>, already validated.</param>
    internal ComponentRegistry(ThreadAffinity owner, int capacity)
    {
        _owner = owner;
        _capacity = capacity;
        _types = new ComponentType[Math.Min(InitialTypeCapacity, capacity)];
    }

    /// <summary>The number of registered types. Registered types have ids <c>0</c> to <c>Count - 1</c>.</summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public int Count
    {
        get
        {
            ThrowIfUnusable("ComponentRegistry.Count");
            return _count;
        }
    }

    /// <summary>The most types this registry holds: the world's <see cref="WorldOptions.MaxComponentTypes"/>.</summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public int Capacity
    {
        get
        {
            ThrowIfUnusable("ComponentRegistry.Capacity");
            return _capacity;
        }
    }

    /// <summary>
    /// Registers <typeparamref name="T"/> if it isn't registered yet, and returns its id. Idempotent: registering a
    /// type again returns the same id.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>The type's id in this registry: <see cref="Count"/> before the call, for a new type.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="T"/> is new and breaks a rule: it is over 1,024 bytes or aligned to more than 64 bytes, or
    /// its <see cref="IComponent.Info"/> has a <see cref="ComponentInfo.SchemaVersion"/> of 0, a null or empty
    /// <see cref="ComponentInfo.FullName"/>, a <see cref="ComponentInfo.StableId"/> that isn't
    /// <see cref="StableComponentId.FromFullName"/> of the name, or <see cref="ComponentInfo.IsTag"/> set with a size
    /// other than 1. The message names the type and the limit or the field. The registry is unchanged.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> is new and another registered type has its stable id: a different name (a hash
    /// collision), or the same name with another schema version or tag-ness, or the same name, schema version and
    /// tag-ness (two types can't share one id, because they could disagree on layout). The message names both. Or the
    /// registry already holds <see cref="Capacity"/> types; the message names the type and
    /// <see cref="WorldOptions.MaxComponentTypes"/>. The registry is unchanged.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// The registry records <typeparamref name="T"/>'s <see cref="IComponent.Info"/> and computes its size
    /// (<see cref="Unsafe.SizeOf{T}"/>) and alignment (its offset after a single byte) itself. Allocates when the type
    /// is new here; returning an existing id allocates nothing.
    /// </remarks>
    public ComponentId Register<T>()
        where T : unmanaged, IComponent
    {
        ThrowIfUnusable("ComponentRegistry.Register");
        int ordinal = ComponentOrdinal.Get<T>();
        if (TryGetId(ordinal, out ComponentId id))
        {
            return id;
        }

        ComponentInfo info = T.Info;
        int size = Unsafe.SizeOf<T>();
        int alignment = AlignmentOf<T>();
        Validate(typeof(T), in info, size, alignment);
        return Add(ordinal, in info, size, alignment);
    }

    /// <summary>Looks up the id of <typeparamref name="T"/>, without registering it.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="id">The type's id when it is registered here; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> if <typeparamref name="T"/> is registered in this registry.</returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing: one static load, one array read and two branches after the checks.</remarks>
    public bool TryGetId<T>(out ComponentId id)
        where T : unmanaged, IComponent
    {
        ThrowIfUnusable("ComponentRegistry.TryGetId");

        // A plain read is enough. The ordinal never changes once assigned, and this registry holds T only if its
        // owner thread, this thread, read or assigned the ordinal while registering T. A 0 here means "unassigned",
        // which is "not registered".
        return TryGetId(ComponentOrdinal<T>.Value, out id);
    }

    /// <summary>Looks up the id of the registered type with a stable id.</summary>
    /// <param name="stableId">The stable id, for example read from serialised data.</param>
    /// <param name="id">The type's id when one is registered here; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> if a type with <paramref name="stableId"/> is registered in this registry.</returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public bool TryGetId(StableComponentId stableId, out ComponentId id)
    {
        ThrowIfUnusable("ComponentRegistry.TryGetId");
        if (_idsByStableId.TryGetValue(stableId.Value, out int index))
        {
            id = new ComponentId(index);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>Returns the description of a registered type: its id, info, size and alignment.</summary>
    /// <param name="id">An id this registry issued.</param>
    /// <returns>The type's description.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="id"/>'s index is negative or at least <see cref="Count"/>. An id from another registry with a
    /// smaller index isn't detected: it names whichever type has that index here.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing, except the exception when it throws.</remarks>
    public ComponentType GetComponentType(ComponentId id)
    {
        ThrowIfUnusable("ComponentRegistry.GetComponentType");
        if ((uint)id.Index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id.Index, $"The registry has {_count} component types.");
        }

        return _types[id.Index];
    }

    /// <summary>
    /// Adds a new, validated type under <paramref name="ordinal"/> as the next dense id, after the stable-id and
    /// capacity checks; the registry is unchanged when it throws. <see cref="Register{T}"/> calls it; tests also call
    /// it directly, to fill a registry with more types than they declare and to forge a stable-id collision.
    /// </summary>
    /// <param name="ordinal">The type's process-wide ordinal, not yet registered here.</param>
    /// <param name="info">The type's info.</param>
    /// <param name="size">The type's size in bytes.</param>
    /// <param name="alignment">The type's alignment in bytes.</param>
    /// <returns>The new id.</returns>
    /// <exception cref="InvalidOperationException">
    /// Another registered type has <paramref name="info"/>'s stable id, or the registry already holds
    /// <see cref="Capacity"/> types.
    /// </exception>
    internal ComponentId Add(int ordinal, in ComponentInfo info, int size, int alignment)
    {
        if (_idsByStableId.TryGetValue(info.StableId.Value, out int registered))
        {
            throw Conflict(_types[registered].Info, in info);
        }

        if (_count == _capacity)
        {
            throw new InvalidOperationException(
                $"Can't register component {info.FullName}: the registry is full (WorldOptions.MaxComponentTypes is {_capacity}).");
        }

        // First of the changes: the stable id is known to be free, so only running out of memory can throw here.
        int index = _count;
        _idsByStableId.Add(info.StableId.Value, index);

        if (index == _types.Length)
        {
            Array.Resize(ref _types, Math.Min(_types.Length * 2, _capacity));
        }

        if (ordinal >= _idsByOrdinal.Length)
        {
            Array.Resize(ref _idsByOrdinal, Math.Max(ordinal + 1, _idsByOrdinal.Length * 2));
        }

        var id = new ComponentId(index);
        _types[index] = new ComponentType(id, info, size, alignment);
        _idsByOrdinal[ordinal] = index + 1;
        _count = index + 1;
        return id;
    }

    /// <summary>
    /// Checks a type's info, size and alignment against the registration rules. <see cref="Register{T}"/> calls it;
    /// tests also call it directly, for an alignment no ordinary struct reaches.
    /// </summary>
    /// <param name="type">The component type, named in the message: its info may be the thing that's wrong.</param>
    /// <param name="info">The type's info.</param>
    /// <param name="size">The type's size in bytes.</param>
    /// <param name="alignment">The type's alignment in bytes.</param>
    /// <exception cref="ArgumentException">A rule is broken. The message names the type and the limit or field.</exception>
    internal static void Validate(Type type, in ComponentInfo info, int size, int alignment)
    {
        if (size > MaxComponentSize)
        {
            throw Invalid(type, $"it is {size} bytes, and components are at most {MaxComponentSize} bytes");
        }

        if (alignment > MaxComponentAlignment)
        {
            throw Invalid(type, $"it is aligned to {alignment} bytes, and components are aligned to at most {MaxComponentAlignment} bytes");
        }

        if (info.SchemaVersion == 0)
        {
            throw Invalid(type, "its ComponentInfo.SchemaVersion is 0, and schema versions start at 1");
        }

        if (string.IsNullOrEmpty(info.FullName))
        {
            throw Invalid(type, "its ComponentInfo.FullName is null or empty");
        }

        if (info.StableId != StableComponentId.FromFullName(info.FullName))
        {
            throw Invalid(type, $"its ComponentInfo.StableId isn't StableComponentId.FromFullName(\"{info.FullName}\")");
        }

        if (info.IsTag && size != 1)
        {
            throw Invalid(type, $"its ComponentInfo.IsTag is set, but it is {size} bytes; a tag has no instance fields, so size 1");
        }
    }

    /// <summary>Marks the registry unusable; called by the world's <c>Dispose</c>.</summary>
    internal void MarkDisposed() => _disposed = true;

    /// <summary>
    /// <typeparamref name="T"/>'s alignment: its offset in a struct that puts one byte before it, which is where the
    /// runtime places it in any struct or array.
    /// </summary>
    private static int AlignmentOf<T>()
        where T : unmanaged
        // The probe holds no references (T is unmanaged), so the runtime keeps its fields in declaration order: Value
        // starts at T's alignment, and the probe's size is that offset plus T's size, which is already a multiple of the
        // alignment. So the difference of the two sizes is the offset of Value.
        => Unsafe.SizeOf<AlignmentProbe<T>>() - Unsafe.SizeOf<T>();

    private static ArgumentException Invalid(Type type, string reason) =>
        new($"Can't register component type {type.FullName}: {reason}.");

    /// <summary>The exception for a new type whose stable id <paramref name="registered"/> already has.</summary>
    private static InvalidOperationException Conflict(in ComponentInfo registered, in ComponentInfo info)
    {
        string reason = !string.Equals(registered.FullName, info.FullName, StringComparison.Ordinal)
            ? $"its stable id {info.StableId.Value:X16} collides with registered component {registered.FullName}; rename one of them"
            : registered.SchemaVersion != info.SchemaVersion
                ? $"component {registered.FullName} is already registered with schema version {registered.SchemaVersion}, and changing the schema of a registered component isn't supported"
                : registered.IsTag != info.IsTag
                    ? $"component {registered.FullName} is already registered as {TagNess(registered.IsTag)}"
                    : $"a different type named {registered.FullName} is already registered, and two types can't share a stable id";
        return new InvalidOperationException(
            $"Can't register component {info.FullName} (schema version {info.SchemaVersion}, {TagNess(info.IsTag)}): {reason}.");
    }

    private static string TagNess(bool isTag) => isTag ? "a tag" : "a data component";

    private bool TryGetId(int ordinal, out ComponentId id)
    {
        int[] idsByOrdinal = _idsByOrdinal;
        if ((uint)ordinal < (uint)idsByOrdinal.Length && idsByOrdinal[ordinal] != 0)
        {
            id = new ComponentId(idsByOrdinal[ordinal] - 1);
            return true;
        }

        id = default;
        return false;
    }

    private void ThrowIfUnusable(string apiName)
    {
        _owner.ThrowIfNotCurrentThread(apiName);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// One byte, then <typeparamref name="T"/>: the offset of <see cref="Value"/> is the alignment of
    /// <typeparamref name="T"/>. Never instantiated; only its layout is used.
    /// </summary>
    private readonly struct AlignmentProbe<T>(byte before, T value)
        where T : unmanaged
    {
        public readonly byte Before = before;
        public readonly T Value = value;
    }
}
