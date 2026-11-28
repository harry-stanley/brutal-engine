using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

/// <summary>
/// Guards the rule of the "Untracked handles" collection: a test that hands the validation layer a handle the layer
/// doesn't track runs with no other test in parallel, because the layer's search of every device in the process races
/// device and instance creation and destruction on other threads, and crashes the test host (#457).
/// </summary>
public sealed class UntrackedHandleValidationTests
{
    [Theory]
    [InlineData("RealDevice_WrongObjectType_ReachesValidation")]
    public void LayerDeviceSearch_UntrackedHandleTest_RunsWithNoOtherTestInParallel(string testMethod)
    {
        Type[] types = typeof(UntrackedHandleValidationTests).Assembly.GetTypes();
        MethodInfo method = Assert.Single(
            types.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)),
            m => m.Name == testMethod);
        Type owner = method.DeclaringType!;

        CollectionAttribute? collection = owner.GetCustomAttribute<CollectionAttribute>();
        Assert.True(collection is not null, $"{owner.Name} is in no collection, so it runs in parallel with other test classes.");
        CollectionDefinitionAttribute definition = Assert.Single(
            types.Select(t => t.GetCustomAttribute<CollectionDefinitionAttribute>()).OfType<CollectionDefinitionAttribute>(),
            d => d.Name == collection.Name);
        Assert.True(definition.DisableParallelization, $"Collection '{collection.Name}' runs in parallel with other collections.");
    }
}
