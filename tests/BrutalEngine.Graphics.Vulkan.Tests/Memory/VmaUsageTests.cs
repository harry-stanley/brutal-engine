using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Graphics.Vulkan.Memory;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Memory;

/// <summary>
/// Section 6 and section 13, rule 5: the backend reaches VMA only through the generated, result-returning calls, never
/// through BRUTAL's convenience layer, which throws a bare <see cref="Exception"/>
/// (<c>Brutal.Vulkan.Vma/Manual/VmaDeviceExtensions.cs:38-40</c>, <c>Brutal.Vulkan.Vma/Manual/VmaAllocator.cs:90-91</c>).
/// Checked on the built assembly's metadata, so a call through any syntax counts.
/// </summary>
public sealed class VmaUsageTests
{
    // The manual layer's types (Brutal.Vulkan.Vma/Manual/VmaAllocator.cs:30, Manual/VmaDeviceExtensions.cs:5). Neither
    // is generic, so a reference to one of their members always has a TypeReference parent.
    private static readonly string[] _bannedTypes = ["Brutal.VulkanApi.Vma.VmaAllocator", "Brutal.VulkanApi.Vma.VmaDeviceExtensions"];

    // The manual layer's allocator helpers, banned on any type (Manual/VmaDeviceExtensions.cs:12, :21, :49).
    private static readonly string[] _bannedMembers = ["CreateVmaAllocator", "CreateVmaAllocatorHandle", "DestroyVmaAllocator"];

    [Fact]
    public void Assembly_ReferencesNoThrowingVmaWrappers()
    {
        (List<string> types, List<string> members) = References(typeof(VmaAllocatorFactory).Assembly.Location);

        Assert.DoesNotContain(types, type => Array.IndexOf(_bannedTypes, type) >= 0);
        Assert.DoesNotContain(members, member => Array.IndexOf(_bannedTypes, member[..member.IndexOf("::", StringComparison.Ordinal)]) >= 0);
        Assert.DoesNotContain(members, member => Array.IndexOf(_bannedMembers, member[(member.IndexOf("::", StringComparison.Ordinal) + 2)..]) >= 0);

        // The scan sees the raw calls the backend does make, so an empty or wrong scan fails here.
        Assert.Contains("Brutal.VulkanApi.Vma.Vma+PInvoke::vmaCreateAllocator", members);
        Assert.Contains("Brutal.VulkanApi.Vma.Vma+PInvoke::vmaDestroyAllocator", members);
    }

    [Fact]
    public void References_PlantedThrowingWrappers_AreFound()
    {
        // This test assembly references the banned members on purpose (Planted, never invoked), so the scan must find
        // them in it: a scan that can't see them would pass the assembly test vacuously.
        Assert.NotNull(Planted());

        (List<string> types, List<string> members) = References(typeof(VmaUsageTests).Assembly.Location);

        Assert.Contains("Brutal.VulkanApi.Vma.VmaAllocator", types);
        Assert.Contains("Brutal.VulkanApi.Vma.VmaDeviceExtensions::CreateVmaAllocatorHandle", members);
        Assert.Contains("Brutal.VulkanApi.Vma.Vma+PInvoke::vmaCreateAllocator", members);
    }

    // A reference to each kind of banned item, as metadata records it: a method group and a type token.
    private static object[] Planted() =>
        [(Func<Device, VmaAllocatorCreateInfo, VmaAllocatorHandle>)VmaDeviceExtensions.CreateVmaAllocatorHandle, typeof(VmaAllocator)];

    // Every TypeReference's full name (nested types as Outer+Inner), and every MemberReference whose parent is a
    // TypeReference as "Type::Member". Members of generic instantiations (TypeSpecification parents) are skipped.
    private static (List<string> Types, List<string> Members) References(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        List<string> types = [];
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            types.Add(NameOf(metadata, handle));
        }

        List<string> members = [];
        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                members.Add(NameOf(metadata, (TypeReferenceHandle)member.Parent) + "::" + metadata.GetString(member.Name));
            }
        }

        return (types, members);
    }

    private static string NameOf(MetadataReader metadata, TypeReferenceHandle handle)
    {
        TypeReference type = metadata.GetTypeReference(handle);
        string name = metadata.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? NameOf(metadata, (TypeReferenceHandle)type.ResolutionScope) + "+" + name
            : metadata.GetString(type.Namespace) + "." + name;
    }
}
