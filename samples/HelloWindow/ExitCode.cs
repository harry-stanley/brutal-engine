namespace HelloWindow;

/// <summary>Process exit codes. CI asserts on these exact values, which match tests/BrutalEngine.Aot.Smoke.</summary>
internal enum ExitCode
{
    /// <summary>The run completed and validation (if enabled) reported nothing.</summary>
    Pass = 0,

    /// <summary>The Khronos validation layer reported at least one error or warning.</summary>
    ValidationMessages = 2,

    /// <summary>Setup failed: no suitable device, a missing layer or driver, the wrong loader, or a native error.</summary>
    SetupFailure = 3,

    /// <summary>A native call failed after setup, or the run did not do what its options required.</summary>
    RuntimeFailure = 4,

    /// <summary>Bad command line.</summary>
    Usage = 64,
}
