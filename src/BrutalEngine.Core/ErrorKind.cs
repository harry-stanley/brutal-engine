namespace BrutalEngine.Core;

/// <summary>Broad category of an expected runtime failure, for programmatic handling.</summary>
/// <remarks>
/// The kind says <em>what sort</em> of failure happened, so callers can react (retry, fall back, report)
/// without parsing messages. Details go in <see cref="EngineError.Message"/> and, for native libraries,
/// <see cref="EngineError.NativeCode"/>.
/// </remarks>
public enum ErrorKind
{
    /// <summary>
    /// No category was given. Also the kind of a default-initialised <see cref="EngineError"/>, which
    /// usually means a result was never assigned.
    /// </summary>
    Unspecified = 0,

    /// <summary>A requested item (file, asset, device, entry) does not exist.</summary>
    NotFound,

    /// <summary>Input data is malformed, corrupt or of an incompatible version.</summary>
    InvalidData,

    /// <summary>The operation or format is not supported on this platform, device or build.</summary>
    Unsupported,

    /// <summary>A required resource (device, service, connection) exists but cannot be used right now.</summary>
    Unavailable,

    /// <summary>Memory or another capacity limit was exhausted.</summary>
    OutOfMemory,

    /// <summary>An input/output operation failed.</summary>
    Io,

    /// <summary>The operation did not complete in time.</summary>
    Timeout,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>A native library reported a failure. <see cref="EngineError.NativeCode"/> holds its code.</summary>
    Native,
}
