using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BrutalEngine.Architecture.Tests.Fakes;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

public sealed class StaticStateTests
{
    [Fact]
    public void EngineModules_HaveNoStaticMutableNonPrimitiveFields()
    {
        List<string> violations = [];
        foreach (EngineModule module in EngineModules.Load())
        {
            violations.AddRange(StaticStateRule.Check(module.Assembly.GetTypes()));
        }

        Violations.AssertNone(StaticStateRule.Name, violations);
    }

    [Theory]
    [InlineData(typeof(StaticReferenceField), "StaticReferenceField.State", "System.Object")]
    [InlineData(typeof(StaticStringField), "StaticStringField._name", "System.String")]
    [InlineData(typeof(StaticStructField), "StaticStructField._time", "System.DateTime")]
    [InlineData(typeof(StaticDelegateField), "StaticDelegateField.Callback", "System.Action")]
    [InlineData(typeof(StaticAutoProperty), "StaticAutoProperty.Current (auto-property backing field)", "System.Object")]
    [InlineData(typeof(StaticEvent), "StaticEvent.Changed", "System.Action")]
    [InlineData(typeof(StaticThreadStaticField), "StaticThreadStaticField._perThread", "System.Object")]
    [InlineData(typeof(StaticGenericField<>), "StaticGenericField<T>._value", "T")]
    public void Check_StaticMutableNonPrimitiveField_IsReported(Type type, string member, string fieldType)
    {
        string violation = Assert.Single(StaticStateRule.Check([type]));

        Assert.StartsWith($"BrutalEngine.Architecture.Tests.Fakes.{member}: static mutable field of type {fieldType}.", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ConstReadonlyPrimitiveAndInstanceFields_AreAllowed()
    {
        Type[] types = [typeof(AllowedState), .. typeof(AllowedState).GetNestedTypes(BindingFlags.NonPublic)];

        // The lambdas make the compiler emit a nested <>c type with static, non-readonly delegate caches. Make sure
        // that case is really exercised, then check it is skipped as compiler-generated.
        Assert.Contains(types, t => t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) && t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Any(f => !f.IsInitOnly));
        Assert.Empty(StaticStateRule.Check(types));
    }

    [Fact]
    public void Check_ReportsEveryViolation()
    {
        IReadOnlyList<string> violations = StaticStateRule.Check([typeof(StaticReferenceField), typeof(StaticStringField), typeof(AllowedState)]);

        Assert.Equal(2, violations.Count);
    }

    [Theory]
    [InlineData(typeof(int), true)]
    [InlineData(typeof(nint), true)]
    [InlineData(typeof(DayOfWeek), true)]
    [InlineData(typeof(byte*), true)]
    [InlineData(typeof(string), false)]
    [InlineData(typeof(decimal), false)]
    [InlineData(typeof(DateTime), false)]
    [InlineData(typeof(object), false)]
    [InlineData(typeof(int[]), false)]
    public void IsAllowedType_OnlyPrimitivesEnumsAndPointers(Type type, bool expected)
    {
        Assert.Equal(expected, StaticStateRule.IsAllowedType(type));
    }
}
