using System;

namespace BrutalEngine.World;

/// <summary>
/// An entity together with the world that owns it, so components can be read and changed without passing the entity
/// to every call: <c>world.Ref(entity).Set(new Health(10))</c>. Every member forwards to the <see cref="EcsWorld"/>
/// member of the same name with <see cref="Entity"/>, with the same behaviour, stamps and exceptions.
/// </summary>
/// <remarks>
/// <para>
/// Create one with <see cref="EcsWorld.Ref(Entity)"/>, which checks the entity once. The reference doesn't keep the
/// entity alive or follow it: after the entity is despawned, members that need an alive entity throw
/// <see cref="BrutalEngine.Core.StaleHandleException"/> as the world's would, and <see cref="IsAlive"/> returns
/// <see langword="false"/>.
/// </para>
/// <para>
/// It holds a reference to the world, so it isn't unmanaged and can't be stored in a component; components store
/// <see cref="BrutalEngine.World.Entity"/> instead. A <c>default</c> instance has no world: its <see cref="Entity"/> is
/// <see cref="BrutalEngine.World.Entity.Null"/>, and every other member, <see cref="World"/> included, throws
/// <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// The forwarders' inherited exceptions for a null or pending entity can't occur for a reference made by
/// <see cref="EcsWorld.Ref(Entity)"/>, which rejects both; a reference whose entity is despawned later gets
/// <see cref="BrutalEngine.Core.StaleHandleException"/>.
/// </para>
/// <para>
/// The struct itself is immutable. Its members have the world's threading rules (owner thread only), and none of them
/// allocates managed memory beyond what the forwarded world member allocates.
/// </para>
/// </remarks>
public readonly struct EntityRef
{
    private readonly EcsWorld? _world;

    /// <summary>Creates a reference to an entity the world has already checked.</summary>
    /// <param name="world">The world that owns <paramref name="entity"/>.</param>
    /// <param name="entity">An entity that was alive in <paramref name="world"/> when the reference was made.</param>
    internal EntityRef(EcsWorld world, Entity entity)
    {
        _world = world;
        Entity = entity;
    }

    /// <summary>Gets the world that owns <see cref="Entity"/>.</summary>
    /// <value>The world the reference was made by.</value>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public EcsWorld World => _world ?? throw new InvalidOperationException(
        "This is a default EntityRef, which has no world; create one with EcsWorld.Ref.");

    /// <summary>Gets the entity the reference stands for. Never throws.</summary>
    /// <value>
    /// The entity, which may have been despawned since the reference was made; <see cref="BrutalEngine.World.Entity.Null"/>
    /// for a <c>default</c> reference.
    /// </value>
    public Entity Entity { get; }

    /// <inheritdoc cref="EcsWorld.Has{T}(Entity)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public bool Has<T>()
        where T : unmanaged, IComponent => World.Has<T>(Entity);

    /// <inheritdoc cref="EcsWorld.Get{T}(Entity)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public ref readonly T Get<T>()
        where T : unmanaged, IComponent => ref World.Get<T>(Entity);

    /// <inheritdoc cref="EcsWorld.Write{T}(Entity)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public ref T Write<T>()
        where T : unmanaged, IComponent => ref World.Write<T>(Entity);

    /// <inheritdoc cref="EcsWorld.TryGet{T}(Entity, out T)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public bool TryGet<T>(out T value)
        where T : unmanaged, IComponent => World.TryGet(Entity, out value);

    /// <inheritdoc cref="EcsWorld.Add{T}(Entity, in T)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public void Add<T>(in T value)
        where T : unmanaged, IComponent => World.Add(Entity, value);

    /// <inheritdoc cref="EcsWorld.Set{T}(Entity, in T)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public void Set<T>(in T value)
        where T : unmanaged, IComponent => World.Set(Entity, value);

    /// <inheritdoc cref="EcsWorld.Remove{T}(Entity)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public bool Remove<T>()
        where T : unmanaged, IComponent => World.Remove<T>(Entity);

    /// <inheritdoc cref="EcsWorld.Despawn(Entity)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public void Despawn() => World.Despawn(Entity);

    /// <inheritdoc cref="EcsWorld.IsAlive(Entity)"/>
    /// <exception cref="InvalidOperationException">The reference is <c>default</c>, so it has no world.</exception>
    public bool IsAlive() => World.IsAlive(Entity);
}
