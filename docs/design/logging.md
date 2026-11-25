# Design: logging facade (`BrutalEngine.Core.Logging`)

## Purpose

One logging API for every engine module and game: category-filtered, UTF-8, and free of managed allocations for common argument types, whether a level is enabled or not ([Architecture 3.20](../03-architecture.md#320-diagnostics), [3.3](../03-architecture.md#33-memory)). Output goes to pluggable sinks. Concrete sinks (memory, console, rotated file, overlay) build on this in later PRs.

## Key types

| Type | Role |
|------|------|
| `Logger` | Owns the categories, the level filter and the sink list, and serialises dispatch. One per host; no global instance. |
| `LoggerOptions` | `MinimumLevel` (default `Information`), `Sinks` (required), `TimeProvider` (default `TimeProvider.System`), `MaxMessageBytes` (default 4096, valid 64 to 65536). |
| `LogCategory` | Small copyable handle to a named category of one logger. `Trace` ... `Critical` and `Log(level, ...)` take an interpolated string. `default` is a no-op category. |
| `LogLevel` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, and `None` (filter only: never enabled). |
| `ILogLevel` and `TraceLevel` ... `CriticalLevel`, `DynamicLevel` | Empty marker structs that give each logging method its own handler type, so the level is a compile-time constant. |
| `LogMessageHandler<TLevel>` | The interpolated string handler. Checks the filter first, then formats straight to UTF-8 in a pooled buffer. |
| `ILogSink` | `Write(scoped in LogEntry)` and `Flush()`. |
| `LogEntry` | `ref struct` view of one entry: level, category, timestamp, thread id, UTF-8 message, caller file and line. |

Why not `Brutal.Logging`: it is static global state (`LogSystem`, and a static, non-thread-safe category dictionary), it materialises a `string` per message, and it pulls in Microsoft.Extensions.Logging. Core also stays free of BRUTAL dependencies ([Architecture 3.1](../03-architecture.md#31-layering)). BRUTAL's own log categories are bridged into a `Logger` from the backend layer in M1. The handler-per-level pattern is borrowed from BRUTAL's `LogString<TLevel>`.

## Usage

```csharp
using Logger logger = new(new LoggerOptions { Sinks = [consoleSink] });
LogCategory render = logger.CreateCategory("Render");
render.Information($"Swapchain {width}x{height}, {frameTime:F2} ms");
render.Log(level, $"Dynamic level: {value}");
```

The compiler turns each call into: build the handler (which asks `render.IsEnabled(level)`), append each part only if enabled, then call the method with the handler by `ref`. The caller's file and line come from `[CallerFilePath]` and `[CallerLineNumber]` on each method.

**Dynamic level path.** `Log(LogLevel level, ref LogMessageHandler<DynamicLevel> message, ...)` passes `level` to the handler's five-argument constructor through `[InterpolatedStringHandlerArgument("", "level")]`. `DynamicLevel.Level` is `None`, so a `DynamicLevel` handler built without an explicit level is always disabled. Each logging method passes its own category and level when it completes the handler; a handler built by hand for another category or level throws `ArgumentException` (the compiler never does that).

## Rules

- **Categories:** `CreateCategory(name)` returns an equal category for the same name. Names match `[A-Za-z0-9._-]{1,64}` (otherwise `ArgumentException`). A logger holds at most 1024 categories; the next new name throws `InvalidOperationException`.
- **Filtering:** a level is enabled for a category if it is at or above the category's level and isn't `None`. The category's level is `MinimumLevel` until `SetLevel` overrides it for that category only. When disabled, no argument is formatted and no buffer is rented.
- **Formatting:** UTF-8, invariant culture. `IUtf8SpanFormattable` values (every numeric type, `char`, `Guid`, dates, ...) format directly into the buffer, with the optional format string. `bool`, `string`, `ReadOnlySpan<char>` and `ReadOnlySpan<byte>` (UTF-8) have their own overloads. Other types fall back to `ISpanFormattable`, then `IFormattable.ToString(format, invariant)`, then `ToString()`. The fallback may allocate, and so do enums and nullable values.
- **Valid UTF-8 always:** invalid UTF-16 (lone surrogates) and invalid UTF-8 input are replaced with U+FFFD.
- **Truncation:** a message longer than `MaxMessageBytes` is cut on a code-point boundary and ends with `…` (U+2026), and the result, ellipsis included, is at most `MaxMessageBytes` bytes. Later parts of the message are not formatted.
- **Dispatch:** every sink gets every enabled entry once, in the same order, one entry at a time. The timestamp comes from the logger's `TimeProvider`, taken while dispatch is serialised, so timestamps don't go backwards in delivery order.
- **Sink failures:** a sink that throws (anything except `OutOfMemoryException`) doesn't stop the other sinks or the caller. `SinkFailureCount` goes up by one per failed `Write` or `Flush`.
- **Ownership:** the logger doesn't own its sinks. `Dispose` flushes them, and the caller disposes them after the logger.
- **Lifecycle:** after `Dispose`, every `Logger` member throws `ObjectDisposedException` (a second `Dispose` is a no-op), but logging through a category is a silent no-op. Shutdown order then can't crash a late logger, e.g. a background thread that logs one more line.
- **`default(LogCategory)`** logs nothing, `IsEnabled` is false, and `Name` is empty. It never throws. Passing it to `Logger.SetLevel` or `Logger.IsEnabled` is misuse and throws `ArgumentException`, as does a category from another logger.
- **Re-entrancy:** sinks must not log through the logger that called them. Such entries are dropped instead of recursing.
- **No static mutable state** (enforced by the architecture tests). The handler's buffers come from `ArrayPool<byte>.Shared`, which is stateless from the engine's point of view.

"Structured" in 3.20 is met in M0 by fixed fields (level, category, time, thread, caller) plus a formatted message. **Open question:** whether to add key/value properties, and in what shape (named holes captured as typed values beside the message, or a separate property bag). Not implemented.

## Memory and threading

- Every `Logger` member is thread-safe. Dispatch, category creation, `SetLevel`, `Flush` and `Dispose` are serialised with a `System.Threading.Lock`. Level checks are lock-free reads of a volatile field.
- Enabled messages are formatted on the calling thread, outside the lock, into a buffer of `MaxMessageBytes` rented from `ArrayPool<byte>.Shared` and returned after dispatch. `LogEntry.Message` points into that buffer, so it is valid only during `Write`: a sink that keeps the message must copy it.
- Logging `int`, `long`, `float`, `double`, `bool`, `string` and spans allocates no managed memory after warm-up, enabled or disabled. The generic `AppendFormatted<T>` is compiled fully optimised (`AggressiveOptimization`), so the JIT removes the interface-check box for value types even before tiering. A long message whose value doesn't fit the remaining space rents a second, temporary buffer.
- Creating a category allocates (once per name). `SetLevel`, `IsEnabled` and `SinkFailureCount` don't.

## Failure modes

| Failure | Result |
|---------|--------|
| Invalid options (null sinks or a null sink, null time provider, `MaxMessageBytes` out of range, undefined `MinimumLevel`) | `ArgumentException` (or a subclass) from the constructor. |
| Invalid or too many category names | `ArgumentException` / `InvalidOperationException`. |
| Sink throws | Isolated, counted in `SinkFailureCount`. `OutOfMemoryException` propagates. |
| Message too long | Truncated with `…`. |
| Formatting a value throws (a user `ToString`) | The exception reaches the caller and nothing is logged. The rented buffer is left to the GC. |
| Use after `Dispose` | `ObjectDisposedException`, except logging, which is a no-op. |

## Testing

- Unit tests for every rule above: idempotent, validated and capped categories, filtering with per-category overrides, formatting (mixed parts, format strings under a `de-DE` current culture), truncation, per-sink delivery order and fields (with a fixed `TimeProvider`), sink isolation, and the lifecycle.
- Disabled logging is checked with a counting `IUtf8SpanFormattable` that must never be formatted.
- Property test (CsCheck): random strings, including surrogate pairs and lone surrogates, under random limits, always give valid UTF-8 no longer than the limit, and an untruncated message round-trips.
- Concurrency: 8 threads log 2,000 entries each; every entry arrives exactly once and whole, and no sink sees two writes at once.
- Allocation tests: 1,000 enabled and 1,000 disabled logs of the common types allocate 0 managed bytes.
- Mutation-checked: inverting the level comparison, or cutting truncated messages at a raw byte instead of a code-point boundary, fails the suite.
