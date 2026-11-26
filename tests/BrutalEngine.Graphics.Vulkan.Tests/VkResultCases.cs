using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests;

/// <summary>The status mapping as docs/design/graphics-vulkan.md section 13 states it, and the values to test it on.</summary>
internal static class VkResultCases
{
    /// <summary>The values the note names. Every other value, named or not, is <see cref="ErrorKind.Native"/>.</summary>
    public static readonly IReadOnlyDictionary<VkResult, ErrorKind> Specified = new Dictionary<VkResult, ErrorKind>
    {
        [VkResult.ErrorOutOfHostMemory] = ErrorKind.OutOfMemory,
        [VkResult.ErrorOutOfDeviceMemory] = ErrorKind.OutOfMemory,
        [VkResult.ErrorOutOfPoolMemory] = ErrorKind.OutOfMemory,
        [VkResult.ErrorDeviceLost] = ErrorKind.Unavailable,
        [VkResult.Timeout] = ErrorKind.Timeout,
        [VkResult.ErrorFormatNotSupported] = ErrorKind.Unsupported,
        [VkResult.ErrorFeatureNotPresent] = ErrorKind.Unsupported,
        [VkResult.ErrorExtensionNotPresent] = ErrorKind.Unsupported,
        [VkResult.ErrorLayerNotPresent] = ErrorKind.Unsupported,
        [VkResult.ErrorIncompatibleDriver] = ErrorKind.Unsupported,
        [VkResult.ErrorInvalidShaderNV] = ErrorKind.InvalidData,
    };

    /// <summary>Values BRUTAL's enum doesn't name: a driver or layer newer than the binding can return them.</summary>
    private static readonly VkResult[] _unnamed =
    [
        (VkResult)(-14),
        (VkResult)(-999_999),
        (VkResult)int.MinValue,
        (VkResult)6,
        (VkResult)999_999,
    ];

    /// <summary>
    /// Every distinct value of the pinned package's <see cref="VkResult"/> except <c>SUCCESS</c> (every negative error,
    /// <c>TIMEOUT</c>, and the other success codes such as <c>NOT_READY</c>, <c>INCOMPLETE</c> and
    /// <c>SUBOPTIMAL_KHR</c>), plus the unnamed values above.
    /// </summary>
    public static IReadOnlyList<VkResult> Failures { get; } =
    [
        .. Enum.GetValues<VkResult>().Concat(_unnamed).Where(r => r != VkResult.Success).Distinct().Order(),
    ];

    /// <summary>The kind the note specifies for <paramref name="result"/>.</summary>
    public static ErrorKind ExpectedKind(VkResult result) => Specified.GetValueOrDefault(result, ErrorKind.Native);

    public static TheoryData<VkResult, ErrorKind> Table()
    {
        TheoryData<VkResult, ErrorKind> data = [];
        foreach (VkResult result in Failures)
        {
            data.Add(result, ExpectedKind(result));
        }

        return data;
    }
}
