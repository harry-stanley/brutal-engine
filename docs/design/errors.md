# Design: expected-failure results (`BrutalEngine.Core`)

## Purpose

Implements the error-handling policy in [Architecture 3.5](../03-architecture.md#35-error-handling):

- **Expected** runtime failures (missing asset, unavailable device, bad data, native status codes) are returned as values.
- **Programming errors** (bad arguments, wrong thread, stale handles) throw exceptions.

## Key types

| Type | Role |
|------|------|
| `ErrorKind` | Coarse category (`NotFound`, `InvalidData`, `Unsupported`, `Unavailable`, `OutOfMemory`, `Io`, `Timeout`, `Cancelled`, `Native`) for programmatic handling. `Unspecified` (0) is the default |
| `EngineError` | Immutable `{ Kind, Message, NativeCode }`. `EngineError.Native(code, message)` for failed native calls |
| `Result` | Success, or failure with an `EngineError` |
| `Result<T>` | A value, or failure with an `EngineError` |
| `ResultFailedException` | Thrown when a failed result's `Value` is read or `ThrowIfFailed()` is called |

## Rules

- **`default` is a failure.** `default(Result)` and `default(Result<T>)` are failures with `ErrorKind.Unspecified`, so a result that was never assigned can't pass for success.
- **Implicit conversions** let methods `return value;` or `return error;`. `Result<T>` has public constructors instead of static factories (CA1000: no static members on generic types).
- **Accessing the wrong side is a programming error:** `Value` on a failure throws `ResultFailedException` (carrying the error), and `Error` on a success throws `InvalidOperationException`. Use `TryGetValue` / `TryGetError` / `GetValueOrDefault` to branch without exceptions.
- **No allocation:** all types are structs. Creating an error allocates only if the caller builds a new message string. A test asserts zero allocation for creating and inspecting results with a pre-built error.
- **Named `EngineError`, not `Error`:** `Error` is a reserved keyword in Visual Basic (CA1716).

## Testing

`tests/BrutalEngine.Core.Tests/EngineErrorTests.cs` and `ResultTests.cs` cover:

- construction, defaults and equality (plus a property test that equal errors hash equally),
- `ToString`, and every accessor on both success and failure,
- implicit conversions, and the zero-allocation path.
