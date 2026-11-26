namespace BrutalEngine.Graphics.Vulkan;

/// <summary>
/// One Khronos validation message that the backend's devices tolerate, with the reason it is tolerated
/// (docs/design/graphics-vulkan.md section 1). An allowed message is logged at <c>Debug</c> with its reason and counted
/// as allowed, never as a validation error or warning.
/// </summary>
/// <param name="MessageIdNumber">
/// The validation layer's <c>messageIdNumber</c> for the message: the layer's stable hash of the VUID.
/// </param>
/// <param name="Reason">Why the message is tolerated. Required: not <see langword="null"/>, empty or white space.</param>
/// <remarks>
/// The value only describes the entry: <c>VulkanBackend.Create</c> rejects an entry without a reason, so
/// <c>default(ValidationAllowance)</c> is never accepted. Equality compares both members, the reason ordinally. An
/// immutable value: safe to share between threads. No member allocates except <c>ToString</c>.
/// </remarks>
public readonly record struct ValidationAllowance(int MessageIdNumber, string Reason);
