using System.Collections.Generic;
using Brutal.FakeApi;

// These types break the binding-surface rule on purpose, one way each, so the rule's negative cases stay tested.
#pragma warning disable CA1051, CA1822, IDE0060 // visible fields, members that could be static, unused parameters

namespace BrutalEngine.Architecture.Tests.Fakes;

public sealed class LeaksField
{
    public FakeDevice Device;
}

public sealed class LeaksProperty
{
    public FakeDevice Device { get; set; }
}

public sealed class LeaksIndexer
{
    public int this[FakeDevice device] => device.Value;
}

public sealed class LeaksReturnType
{
    public FakeDevice Create() => default;
}

public sealed class LeaksParameter
{
    public void Use(FakeDevice device)
    {
    }
}

public sealed class LeaksByRefParameter
{
    public void Use(ref FakeDevice device)
    {
    }
}

public sealed class LeaksArray
{
    public FakeDevice[] Devices() => [];
}

public sealed unsafe class LeaksPointer
{
    public FakeDevice* Device() => null;
}

public sealed unsafe class LeaksFunctionPointer
{
    public delegate* unmanaged<FakeDevice, void> Callback;
}

public sealed class LeaksGenericArgument
{
    public IReadOnlyList<KeyValuePair<int, FakeDevice>> Devices() => [];
}

public sealed class LeaksEvent
{
    public event FakeCallback? Changed
    {
        add { }
        remove { }
    }
}

public sealed class LeaksConstructor
{
    public LeaksConstructor(FakeDevice device)
    {
    }
}

public sealed class LeaksOperator
{
    public static implicit operator FakeDevice(LeaksOperator value) => default;
}

public class LeaksBaseType : FakeBase
{
}

public sealed class LeaksInterface : IFakeCallbacks
{
}

public sealed class LeaksTypeConstraint<T>
    where T : IFakeCallbacks
{
}

public sealed class LeaksMethodConstraint
{
    public void Use<T>()
        where T : IFakeCallbacks
    {
    }
}

public class LeaksProtectedMember
{
    protected FakeDevice Create() => default;
}

public sealed class LeaksOuter
{
    public sealed class Nested
    {
        public FakeDevice Device;
    }
}

public class LeaksProtectedNestedOuter
{
    protected sealed class Nested
    {
        public FakeDevice Device;
    }
}

public delegate FakeDevice LeaksDelegateReturn();

/// <summary>Uses binding types only where no other assembly can see them.</summary>
public class KeepsBindingsInternal
{
    private FakeDevice _device;

    public int Value => _device.Value;

    internal FakeDevice Device => _device;

    private protected FakeDevice Create() => _device;

    internal void Use(FakeDevice device) => _device = device;

    private sealed class Hidden
    {
        public FakeDevice Device { get; set; }
    }
}

internal sealed class InternalType
{
    public FakeDevice Device { get; set; }
}
