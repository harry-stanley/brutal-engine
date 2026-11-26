using System.Collections.Generic;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Instances;

/// <summary>
/// The matcher behind <see cref="GlobalCommandsTests.Backend_GlobalCommands_AreCalledOnlyFromTheGateFile"/>, fed source
/// text: every way of writing a global command it claims to catch is flagged, and what real backend code writes
/// around those names (comments, messages, the gate's own wrapper, the engine's call ids) is not.
/// </summary>
public sealed class GlobalCommandScanTests
{
    /// <summary>Sources that make a global command without the gate, one per form the scan catches.</summary>
    public static TheoryData<string, string> Bypasses => new()
    {
        { "raw binding", "r = global::Brutal.VulkanApi.Vulkan.PInvoke.vkCreateInstance(&info, null, &instance);" },
        { "raw binding, version", "r = Vulkan.PInvoke.vkEnumerateInstanceVersion(&version);" },
        { "BRUTAL wrapper", "Instance i = global::Brutal.VulkanApi.Vulkan.CreateInstance(info);" },
        { "BRUTAL wrapper, layer count", "int n = Vulkan.EnumerateInstanceLayerProperties_Count();" },
        { "BRUTAL wrapper, layer write", "Vulkan.EnumerateInstanceLayerProperties_Write(layers);" },
        { "BRUTAL wrapper, extension count", "int n = Vulkan.EnumerateInstanceExtensionProperties_Count(name);" },
        { "BRUTAL wrapper, extension write", "Vulkan.EnumerateInstanceExtensionProperties_Write(name, extensions);" },
        { "BRUTAL wrapper, version", "Vulkan.EnumerateInstanceVersion(&version);" },
        { "alias of the raw bindings", "using VkGlobal = Brutal.VulkanApi.Vulkan.PInvoke;" },
        { "alias of the raw bindings, used", "r = VkGlobal.vkEnumerateInstanceLayerProperties(&count, null);" },
        { "alias of the binding class", "using Vk = global::Brutal.VulkanApi.Vulkan;\nInstance i = Vk.Create(info);" },
        { "using static, bare wrapper call", "using static Brutal.VulkanApi.Vulkan;\nInstance i = CreateInstance(info);" },
        { "using static of the raw bindings", "global using static Brutal.VulkanApi.Vulkan.PInvoke;" },
        { "call split over two lines", "r = global::Brutal.VulkanApi.Vulkan.PInvoke\n    .vkEnumerateInstanceExtensionProperties(null, &count, null);" },
        { "wrapper split over two lines", "Instance i = global::Brutal.VulkanApi.Vulkan\n    .CreateInstance(info);" },
        { "comment between the tokens", "Instance i = Vulkan /* the wrapper */ . CreateInstance(info);" },
        { "method group", "Func<Instance.CreateInfo, Instance> create = global::Brutal.VulkanApi.Vulkan.CreateInstance;" },
        { "function pointer", "delegate* unmanaged<int*, VkLayerProperties*, VkResult> f = &global::Brutal.VulkanApi.Vulkan.PInvoke.vkEnumerateInstanceLayerProperties;" },
        { "verbatim identifier", "r = Vulkan.PInvoke.@vkCreateInstance(&info, null, &instance);" },
        { "lookup by name, UTF-8 literal", "PfnVkVoidFunction f = lookup(default, \"vkCreateInstance\"u8);" },
        { "lookup by name, export", "nint f = NativeLibrary.GetExport(library, \"vkEnumerateInstanceVersion\");" },
        { "lookup through BRUTAL's vkGetInstanceProcAddr", "PfnVkVoidFunction f = global::Brutal.VulkanApi.Vulkan.GetInstanceProcAddr.Invoke(default, name);" },
        { "lookup split over two lines", "PfnVkVoidFunction f = Vulkan.GetInstanceProcAddr\n    .Invoke(default, name);" },
        { "call after a string holding //", "string url = \"https://example.org\"; Vulkan.CreateInstance(info);" },
        { "call after a verbatim string with quotes", "string s = @\"say \"\"hi\"\" // here\"; Vulkan.CreateInstance(info);" },
        { "call after a char literal quote", "char q = '\"'; Vulkan.CreateInstance(info);" },
        { "call after an escaped quote", "string s = \"a \\\" b\"; Vulkan.CreateInstance(info);" },
        { "call inside an interpolation hole", "string s = $\"v{global::Brutal.VulkanApi.Vulkan.PInvoke.vkEnumerateInstanceVersion(&version)}\";" },
        { "call after a raw string", "string s = \"\"\"\n  \"quoted\" // text\n  \"\"\"; Vulkan.CreateInstance(info);" },
    };

    /// <summary>What the backend writes around the names without making a global command.</summary>
    public static TheoryData<string, string> Allowed => new()
    {
        { "comment naming the gate", "// Every global command goes through GlobalCommands.CreateInstance (vkCreateInstance)." },
        { "doc comment naming a command", "/// <summary><c>vkEnumerateInstanceLayerProperties</c> through the gate, not Vulkan.CreateInstance.</summary>" },
        { "block comment with a call", "/* Vulkan.CreateInstance(info); Vulkan.PInvoke.vkCreateInstance(&info, null, &i); */ int x = 0;" },
        { "the gate's own wrapper", "instance = GlobalCommands.CreateInstance(source, new Instance.CreateInfo { Flags = flags });" },
        { "the gate's enumerations", "result = GlobalCommands.EnumerateExtensions(source, layerName, ref count, properties);" },
        { "a source's own member", "return source.CreateInstance(createInfo);" },
        { "the engine's call ids", "const VulkanCall call = VulkanCall.EnumerateInstanceExtensionProperties;\nif (gate.TryTakeFault(VulkanCall.CreateInstance, out VkResult injected)) { }" },
        { "a message naming a command", "VulkanCall.EnumerateInstanceLayerProperties => \"vkEnumerateInstanceLayerProperties failed\"," },
        { "a message prefix naming a command", "return error.Message.StartsWith(\"Vulkan call to vkCreateInstance \", StringComparison.Ordinal);" },
        { "a raw string naming a call", "string s = \"\"\"\n  Vulkan.CreateInstance(info) and vkCreateInstance\n  \"\"\";" },
        { "an interpolated message", "string s = $\"{nameof(GlobalCommands)}: vkCreateInstance {count:D2} {(ok ? \"yes\" : \"no\")}\";" },
        { "reading BRUTAL's vkGetInstanceProcAddr pointer", "entryPoint = (nint)global::Brutal.VulkanApi.Vulkan.GetInstanceProcAddr.Invoke;" },
        { "the loader's export name", "private const string EntryPointName = \"vkGetInstanceProcAddr\";" },
        { "namespace usings", "using Brutal.VulkanApi;\nusing BrutalEngine.Graphics.Vulkan.Instances;\nusing static System.Math;" },
        { "an alias of another type", "using Gate = System.Threading.Lock;" },
        { "a using statement", "using (Gate.EnterScope())\n{\n}" },
        { "identifiers containing a name", "int vkCreateInstanceCount = 0; int myEnumerateInstanceVersion = 1; string CreateInstanceCall = name;" },
        { "a device command", "r = device.PInvoke.vkCreateDevice(physical, &info, null, &device);" },
    };

    [Theory]
    [MemberData(nameof(Bypasses))]
    public void FindBypasses_BypassForm_IsFlagged(string form, string source)
    {
        List<string> found = GlobalCommandScan.FindBypasses(source);

        Assert.True(found.Count > 0, form + " not flagged:\n" + source);
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void FindBypasses_AllowedForm_IsNotFlagged(string form, string source)
    {
        List<string> found = GlobalCommandScan.FindBypasses(source);

        Assert.True(found.Count == 0, form + " flagged:\n" + string.Join('\n', found));
    }

    [Fact]
    public void FindBypasses_CallOnALaterLine_ReportsItsLine()
    {
        List<string> found = GlobalCommandScan.FindBypasses("int a = 0;\n// Vulkan.CreateInstance\nr = Vulkan.PInvoke\n    .vkCreateInstance(&info, null, &i);");

        Assert.Equal(["4: vkCreateInstance"], found);
    }
}
