using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

/// <summary>
/// Tests that hand the validation layer a handle it doesn't track (a handle named as the wrong object type, say), which
/// run alone, after every parallel test has finished. For a handle it doesn't find, the layer searches every device in
/// the process (<c>ObjectLifetimes::CheckObjectValidity</c>). Layers before 1.4.304, among them the 1.3.275 that the
/// Linux CI runner gets from the Ubuntu archive, walk a global map for that search without a lock, while another
/// thread's <c>vkCreateDevice</c>, <c>vkDestroyDevice</c> or instance creation and destruction change it: the search
/// then reads an entry that is being freed, and the test host dies with SIGSEGV (#457). From 1.4.304 the search holds
/// a lock.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UntrackedHandleValidation
{
    public const string Name = "Untracked handles";
}
