using System;
using System.Collections.Generic;
using BrutalEngine.Architecture.Tests.Fakes;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

public sealed class BindingSurfaceTests
{
    [Fact]
    public void NonBackendModules_ExposeNoBindingTypes()
    {
        List<string> violations = [];
        foreach (EngineModule module in EngineModules.Load())
        {
            if (module.Definition.Kind != ModuleKind.Backend)
            {
                violations.AddRange(BindingSurfaceRule.Check(module.Assembly.GetTypes()));
            }
        }

        Violations.AssertNone(BindingSurfaceRule.Name, violations);
    }

    [Theory]
    [InlineData("Brutal.VulkanApi", true)]
    [InlineData("Brutal.VulkanApi.Vma", true)]
    [InlineData("Brutal.SdlApi", true)]
    [InlineData("Brutal.SdlApi.Ttf", true)]
    [InlineData("Brutal.ImGuiApi.Internal.InlineArrays", true)]
    [InlineData("Brutal.Box2DApi", true)]
    [InlineData("Brutal.MeshOptimizer", true)]
    [InlineData("Brutal.Numerics", false)]
    [InlineData("Brutal.Collections", false)]
    [InlineData("Brutal.Api", false)]
    [InlineData("Brutal", false)]
    [InlineData("Brutal.MeshOptimizerX", false)]
    [InlineData("BrutalEngine.GraphicsApi", false)]
    [InlineData("MyGame.WidgetApi", false)]
    [InlineData(null, false)]
    public void IsBindingNamespace_MatchesBrutalBindingNamespaces(string? ns, bool expected)
    {
        Assert.Equal(expected, BindingSurfaceRule.IsBindingNamespace(ns));
    }

    [Theory]
    [InlineData(typeof(LeaksField), "LeaksField.Device (field type)", "FakeDevice")]
    [InlineData(typeof(LeaksProperty), "LeaksProperty.Device (property type)", "FakeDevice")]
    [InlineData(typeof(LeaksIndexer), "LeaksIndexer.Item (indexer parameter 'device')", "FakeDevice")]
    [InlineData(typeof(LeaksReturnType), "LeaksReturnType.Create (return type)", "FakeDevice")]
    [InlineData(typeof(LeaksParameter), "LeaksParameter.Use (parameter 'device')", "FakeDevice")]
    [InlineData(typeof(LeaksByRefParameter), "LeaksByRefParameter.Use (parameter 'device')", "FakeDevice")]
    [InlineData(typeof(LeaksArray), "LeaksArray.Devices (return type)", "FakeDevice")]
    [InlineData(typeof(LeaksPointer), "LeaksPointer.Device (return type)", "FakeDevice")]
    [InlineData(typeof(LeaksFunctionPointer), "LeaksFunctionPointer.Callback (field type)", "FakeDevice")]
    [InlineData(typeof(LeaksGenericArgument), "LeaksGenericArgument.Devices (return type)", "FakeDevice")]
    [InlineData(typeof(LeaksEvent), "LeaksEvent.Changed (event type)", "FakeCallback")]
    [InlineData(typeof(LeaksConstructor), "LeaksConstructor..ctor (parameter 'device')", "FakeDevice")]
    [InlineData(typeof(LeaksOperator), "LeaksOperator.op_Implicit (return type)", "FakeDevice")]
    [InlineData(typeof(LeaksBaseType), "LeaksBaseType (base type)", "FakeBase")]
    [InlineData(typeof(LeaksInterface), "LeaksInterface (interface)", "IFakeCallbacks")]
    [InlineData(typeof(LeaksTypeConstraint<>), "LeaksTypeConstraint<T> (constraint on 'T')", "IFakeCallbacks")]
    [InlineData(typeof(LeaksMethodConstraint), "LeaksMethodConstraint.Use (constraint on 'T')", "IFakeCallbacks")]
    [InlineData(typeof(LeaksProtectedMember), "LeaksProtectedMember.Create (return type)", "FakeDevice")]
    [InlineData(typeof(LeaksOuter.Nested), "LeaksOuter.Nested.Device (field type)", "FakeDevice")]
    [InlineData(typeof(LeaksDelegateReturn), "LeaksDelegateReturn.Invoke (return type)", "FakeDevice")]
    public void Check_BindingTypeInPublicSurface_IsReported(Type type, string member, string bindingType)
    {
        IReadOnlyList<string> violations = BindingSurfaceRule.Check([type]);

        // A delegate's return type also appears on the compiler-emitted EndInvoke, so look for the expected entry.
        Assert.Contains(violations, v => v.StartsWith($"BrutalEngine.Architecture.Tests.Fakes.{member} exposes Brutal.FakeApi.{bindingType} from binding namespace Brutal.FakeApi.", StringComparison.Ordinal));
        if (type != typeof(LeaksDelegateReturn))
        {
            Assert.Single(violations);
        }
    }

    [Fact]
    public void Check_ProtectedNestedTypeOfUnsealedType_IsReported()
    {
        Type nested = typeof(LeaksProtectedNestedOuter).GetNestedType("Nested", System.Reflection.BindingFlags.NonPublic)!;

        string violation = Assert.Single(BindingSurfaceRule.Check([nested]));
        Assert.Contains("LeaksProtectedNestedOuter.Nested.Device (field type)", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_BindingTypesOnlyInNonPublicPositions_ReportsNothing()
    {
        Type[] types =
        [
            typeof(KeepsBindingsInternal),
            .. typeof(KeepsBindingsInternal).GetNestedTypes(System.Reflection.BindingFlags.NonPublic),
            typeof(InternalType),
        ];

        Assert.Empty(BindingSurfaceRule.Check(types));
    }

    [Fact]
    public void Check_SkipsTypesInjectedByCodeCoverage()
    {
        Assert.Empty(BindingSurfaceRule.Check([typeof(Microsoft.CodeCoverage.Instrumentation.Static.Tracker.FakeStaticManagedTrackerTemplate)]));
    }
}
