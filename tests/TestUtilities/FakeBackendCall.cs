using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.TestUtilities;

/// <summary>One call a <see cref="FakeBackendDevice"/> received, as <see cref="FakeBackendDevice.Calls"/> records it.</summary>
/// <param name="Operation">The member called.</param>
/// <param name="Kind">The object kind the call named or created; <see langword="null"/> when it names none.</param>
/// <param name="Id">
/// The id the call named or returned; 0 when it names none or a creation failed (a scripted failure).
/// </param>
/// <param name="FrameSlot">The frame slot the call named; -1 when it names none.</param>
/// <remarks>An immutable value. Contract violations throw before anything is recorded.</remarks>
public readonly record struct FakeBackendCall(FakeBackendOperation Operation, BackendObjectKind? Kind, ulong Id, int FrameSlot);
