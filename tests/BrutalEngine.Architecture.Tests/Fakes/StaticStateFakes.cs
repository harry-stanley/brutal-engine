using System;
using System.Linq;

// These types break the static-state rule on purpose, one way each, so the rule's negative cases stay tested.
#pragma warning disable CA2211, IDE0044, IDE0051, IDE0052, CS0067, CS0169, CS0649 // the fields exist only to be found by reflection

namespace BrutalEngine.Architecture.Tests.Fakes;

public static class StaticReferenceField
{
    public static object? State;
}

public static class StaticStringField
{
    private static string? _name;
}

public static class StaticStructField
{
    private static DateTime _time;
}

public static class StaticDelegateField
{
    internal static Action? Callback;
}

public static class StaticAutoProperty
{
    public static object? Current { get; set; }
}

public static class StaticEvent
{
    public static event Action? Changed;
}

public static class StaticThreadStaticField
{
    [ThreadStatic]
    private static object? _perThread;
}

public sealed class StaticGenericField<T>
{
    private static T? _value;
}

/// <summary>Static state the rule allows, plus a lambda so the compiler emits a static, non-readonly delegate cache.</summary>
public sealed unsafe class AllowedState
{
    public const string Name = "allowed";

    public static readonly string Greeting = "hello";

    private static readonly object _lock = new();

    private static int _counter;
    private static bool _flag;
    private static double _ratio;
    private static nint _native;
    private static DayOfWeek _day;
    private static byte* _buffer;
    private static delegate*<void> _function;

    private object? _instanceState;

    public static int Count { get; set; }

    public static Func<int, int> Square { get; } = x => x * x;

    public static int CountPositive(int[] values) => values.Count(v => v > 0);
}
