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
| `LogMessageHandler<TLevel>` | The interpolated string handler. Checks the filter first, then formats straight to UTF-8 in one of the logger's message buffers. |
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
- **Formatting:** UTF-8, invariant culture. `IUtf8SpanFormattable` values (every numeric type, `char`, `Guid`, dates, ...) format directly into the buffer, with the optional format string. `bool`, `string`, `ReadOnlySpan<char>` and `ReadOnlySpan<byte>` (UTF-8) have their own overloads. Any other value goes to a non-generic `AppendFormatted(object?, ...)` fallback, which tries `IUtf8SpanFormattable` on the boxed value, then `ISpanFormattable`, then `IFormattable.ToString(format, invariant)`, then `ToString()`. The fallback allocates: value types are boxed to reach it, including enums and nullable values such as `int?`. The compiler picks the overload from the declared type, so a struct that implements only `ISpanFormattable`, and a value of a generic type parameter that isn't constrained to `IUtf8SpanFormattable` (even when the type argument is `int`), also take the fallback. To stay allocation-free, implement `IUtf8SpanFormattable`, or constrain your generic `T` to it (`where T : IUtf8SpanFormattable`).
- **Valid UTF-8 always:** invalid UTF-16 (lone surrogates) and invalid UTF-8 input are replaced with U+FFFD.
- **Truncation:** a message longer than `MaxMessageBytes` is cut on a code-point boundary and ends with `…` (U+2026), and the result, ellipsis included, is at most `MaxMessageBytes` bytes. Later parts of the message are not formatted.
- **Dispatch:** every sink gets every enabled entry once, in the same order, one entry at a time. The timestamp comes from the logger's `TimeProvider`, taken while dispatch is serialised, so timestamps don't go backwards in delivery order.
- **Sink failures:** a sink that throws (anything except `OutOfMemoryException`) doesn't stop the other sinks or the caller. `SinkFailureCount` goes up by one per failed `Write` or `Flush`.
- **Ownership:** the logger doesn't own its sinks. `Dispose` flushes them, and the caller disposes them after the logger.
- **Lifecycle:** after `Dispose`, every `Logger` member throws `ObjectDisposedException` (a second `Dispose` is a no-op), but logging through a category is a silent no-op. Shutdown order then can't crash a late logger, e.g. a background thread that logs one more line.
- **`default(LogCategory)`** logs nothing, `IsEnabled` is false, and `Name` is empty. It never throws. Passing it to `Logger.SetLevel` or `Logger.IsEnabled` is misuse and throws `ArgumentException`, as does a category from another logger.
- **Re-entrancy:** sinks must not log through the logger that called them. Such entries are dropped instead of recursing.
- **No static mutable state** (enforced by the architecture tests). The message buffers are the logger's instance state (an internal `MessageBufferCache`). The fallback buffers and the long-message buffer come from `ArrayPool<byte>.Shared`, which is stateless from the engine's point of view.

"Structured" in 3.20 is met in M0 by fixed fields (level, category, time, thread, caller) plus a formatted message. **Open question:** whether to add key/value properties, and in what shape (named holes captured as typed values beside the message, or a separate property bag). Not implemented.

## Sinks

Concrete sinks live in `BrutalEngine.Core.Logging.Sinks`. Each one copies what it needs during `Write`, because `LogEntry.Message` is only valid for that call.

### Line format

Text sinks share one internal formatter (`LogLineFormatter`), so the console and the rotating file sink (#23) write identical lines:

```text
yyyy-MM-ddTHH:mm:ss.fffZ LEVEL [Category] message

2026-11-25T03:04:05.678Z INFO [Render] Swapchain 1920x1080, 0.42 ms
```

- **Timestamp:** UTC, with milliseconds truncated (not rounded), as the `fff` format does. It always takes 24 bytes.
- **LEVEL** is a fixed-width token: `TRCE DBUG INFO WARN EROR CRIT`. A sink given any other level (`None` or an undefined value) throws `ArgumentOutOfRangeException`. A logger never sends one, and it isolates and counts sink throws anyway.
- **Message:** written as received, newlines included. It is valid UTF-8 by the `LogEntry` contract. The category and the caller file are encoded to UTF-8, with lone surrogates replaced by U+FFFD.
- **Caller suffix** ` (file:line)` goes before the newline only when the sink's `IncludeCallerInfo` is set and the entry has a caller file.
- The formatter writes into a caller-provided `Span<byte>` sized by `GetMaxLength` (an upper bound: 3 bytes per UTF-16 unit, plus the fixed parts). It doesn't allocate.

### `ConsoleLogSink`

| Option (`ConsoleLogSinkOptions`) | Default | Meaning |
|------|---------|---------|
| `Output` | `null`: standard output, opened by the sink | Entries below `ErrorThreshold`. |
| `ErrorOutput` | `null`: standard error, opened by the sink | Entries at or above `ErrorThreshold`. May be the same stream as `Output`. |
| `ErrorThreshold` | `Warning` | `None` sends everything to `Output`. |
| `IncludeCallerInfo` | `false` | Appends ` (file:line)`. |

- **One whole line per entry:** the line is formatted into the sink's own buffer and written with a single `Stream.Write` call, under the sink's lock, so lines from concurrent writers (or from several loggers sharing the sink) never interleave.
- **Flushing:** after every `Error` or `Critical` entry (to whichever stream it went to), on `Flush()`, and on `Dispose()`. Lower levels rely on the stream's own buffering.
- **Ownership:** the sink disposes only the standard streams it opened itself, never streams it was given. After `Dispose`, `Write` and `Flush` throw `ObjectDisposedException` and a second `Dispose` does nothing. Stream exceptions propagate to the logger, which isolates and counts them.
- **Oversized lines:** `Write` throws `OverflowException` when no buffer can hold the line, because its worst-case length (`GetMaxLength`: the message's bytes, 3 bytes per character of the category and caller file, and the fixed parts, summed under `checked`) is more than `int.MaxValue`. That takes an oversized message (close to 2 GB), or a category or caller file hundreds of millions of characters long. A logger never passes such a message (it caps messages at `MaxMessageBytes`), and isolates and counts the exception like any other sink exception.
- **Memory:** the line buffer starts at 8 KiB and grows (at least doubling) only when a line is longer than any before. Steady-state writes allocate no managed memory, as long as the streams' own writes don't. Tests use injected streams, never `System.Console`.
- **Not done (by scope):** colours, and console encoding setup: the bytes are UTF-8 whatever the console's code page is. On Windows, when `Console.OutputEncoding` is UTF-16 (code page 1200) and the output isn't redirected, the standard stream writes through `WriteConsoleW`, so these UTF-8 bytes display wrongly. Hosts should use UTF-8 console output (the default code page or `Console.OutputEncoding = Encoding.UTF8`).

### `MemoryLogSink`

The recent log tail, kept in memory for crash reports ([Architecture 3.5](../03-architecture.md#35-error-handling)) and the debug overlay.

- **`MemoryLogSink(int capacityEntries, int capacityBytes)`** allocates all its storage up front: a ring of `capacityEntries` metadata slots and a byte ring of `capacityBytes` for the messages. It never grows. `capacityEntries` must be at least 1 and `capacityBytes` at least 64 (the logger's smallest `MaxMessageBytes`); otherwise `ArgumentOutOfRangeException`.
- **Overwriting:** a new entry first drops the oldest entries until both the entry count and the message bytes fit. Each dropped entry adds one to `DroppedCount`. Messages may wrap around the end of the byte ring.
- **Truncation:** a message longer than `capacityBytes` is cut on a code-point boundary and ends with `…` (U+2026), at most `capacityBytes` bytes in all. As in the logger, this relies on the message being valid UTF-8, which the `LogEntry` contract guarantees. It isn't a drop.
- **Reading, by maintainer decision:** `int CopyTo(Span<MemoryLogRecord> destination, Memory<byte> messageBuffer)`. It copies the newest entries that fit both buffers, oldest first, and returns how many it copied. It stops at the first entry, going back from the newest, that doesn't fit, so the result is always the most recent entries with no gaps. Each `MemoryLogRecord.Message` is a slice of the caller's `messageBuffer`, so records outlive later writes and don't reference the sink. Buffers of at least `CapacityEntries` records and `CapacityBytes` bytes always receive everything. Options considered and not chosen: `CopyTo(Span<MemoryLogRecord>)` allocating one array per call for the messages, and a struct enumerator that holds the sink's lock while the caller iterates. That would block every logger using the sink and would need same-thread re-entrancy detection.
- **Threading:** `Write`, `CopyTo` and `DroppedCount` take the sink's lock, so a copy never sees a half-written entry, and the sink can be shared by several loggers. `Flush` does nothing. The sink holds no unmanaged resources and isn't disposable.
- **Memory:** `Write`, `CopyTo` and `DroppedCount` allocate nothing. Category and caller-file strings are kept by reference: a logger passes the category's own name and a compile-time literal.

### `RotatingFileLogSink`

The persistent log: lines in the shared format, in files in a directory the host provides.

| Option (`RotatingFileLogSinkOptions`) | Default | Meaning |
|------|---------|---------|
| `Directory` | required | Fully qualified path (otherwise `ArgumentException`). Created if missing. |
| `FileName` | `engine.log` | A bare file name: no directory part, not `.` or `..`, no invalid characters. |
| `MaxFileBytes` | 10 MiB | At least 64 KiB. Rotate before a line would take a non-empty file past it. |
| `MaxFiles` | 5 | 1 to 100, counting the current file, so `MaxFiles - 1` archives. |
| `FlushLevel` | `Warning` | Entries at or above it are flushed as soon as they're written. `None` flushes only on `Flush()`/`Dispose()`. |
| `IncludeCallerInfo` | `false` | Appends ` (file:line)`. |

Invalid options throw (`ArgumentNullException`, `ArgumentException` or `ArgumentOutOfRangeException`) before the sink touches the disk.

- **No VFS, by maintainer decision:** the sink takes an absolute directory and uses `System.IO` directly. Logging must work before the VFS exists (M1 Platform) and while the engine is crashing. In M1 the host gets the directory from the platform's user paths.
- **Files:** the current file is `engine.log`; archives are `engine.1.log` (the newest) to `engine.<MaxFiles - 1>.log`. A name without an extension gets `game.1`, `game.2` and so on.
- **Startup rotation:** construction rotates an existing `engine.log` away, so each run starts a fresh file. It also deletes archives numbered from `MaxFiles` up to 100, left by an earlier run with a larger `MaxFiles`. Other files in the directory are never touched.
- **Size rotation:** when a line would take a non-empty file past `MaxFileBytes`, the sink closes the file, shifts `engine.<n>.log` to `n + 1` (oldest first) for the run of archives starting at 1, up to the first free slot, deletes the archive now beyond `MaxFiles - 1`, renames `engine.log` to `engine.1.log` (or deletes it when `MaxFiles` is 1) and opens a new `engine.log`. The file is closed before any rename, because Windows can't rename a file another handle holds open without `FileShare.Delete`. If a rename in the middle of the shift fails (which faults the sink), the oldest archive can be left at `engine.<MaxFiles>.log`, beyond the limit, until the next run's startup rotation trims it (a faulted sink doesn't rotate again). No line is lost: every archive is still on disk.
- **Oversized lines:** a line longer than `MaxFileBytes` is written whole, alone in a fresh file. An empty file takes any line, so size rotation never creates an empty archive. Startup rotation, by choice, archives an existing `engine.log` even when it's empty (a run that logged nothing): each run keeps its own file.
- **Writing:** one buffered `FileStream` (16 KiB buffer), opened with `FileShare.Read` so the log can be tailed. It's flushed after each entry at or above `FlushLevel`, and on `Flush()` and `Dispose()`. A flush is `FileStream.Flush()`, to the operating system, never `Flush(true)` (fsync): flushed lines survive a process crash but not a power loss. An fsync on every warning would cost too much.
- **Failures:** only misuse throws: invalid options, an out-of-range level, or use after `Dispose`. I/O never throws. The first `IOException` or `UnauthorizedAccessException`, at any point (creating the directory, rotating, opening, writing, flushing or closing), faults the sink permanently:
  - `EngineError? Fault` (kind `Io`, with the operation, the path and the exception's message) keeps the first failure. It stays readable after `Dispose`.
  - The file is closed at once. `Dispose` releases the handle even when its final flush throws.
  - Every later entry is dropped and counted in `long DroppedCount`. So is the entry being written when the failure happened, since it can't be known to have reached the file. Lines buffered before a failed `Flush()` aren't counted: they may or may not have reached the file.
  - `Flush` and `Dispose` do nothing more, and the sink never recovers, even if the cause goes away. The next run's sink starts afresh.
  - So a logger never sees the file sink fail (`SinkFailureCount` doesn't move). Hosts check `Fault` to report it, for example on the overlay or in a crash report.
  - Any other exception from a rotation propagates after the old file was closed. In practice that means only `OutOfMemoryException`, which a logger lets through: options validation rules out the file APIs' argument exceptions. The sink then has no file and no fault, and drops and counts later entries.
- **Why permanent:** retrying after an I/O failure would hit a broken disk or a held file again on every line, and the cost and the noise grow with the log rate. Archives only move into a free slot, so a retry wouldn't lose one, but it would still never be known to succeed. A fault is rare, and a new sink is cheap.
- **Threading and lifecycle:** every member takes the sink's lock, so lines never interleave and rotation never races a write. The sink can be shared by several loggers. After `Dispose`, `Write` and `Flush` throw `ObjectDisposedException`, and a second `Dispose` does nothing.
- **Memory:** writes that don't rotate allocate nothing, apart from the line buffer (8 KiB to start) growing for a line longer than any before. That includes writes that flush, `Flush()`, `Fault` and `DroppedCount`. Rotation allocates file names and a new stream, once per `MaxFileBytes` written, so it isn't steady state. Faulting allocates the error message once.

## Memory and threading

- Every `Logger` member is thread-safe. Dispatch, category creation, `SetLevel`, `Flush` and `Dispose` are serialised with a `System.Threading.Lock`. Level checks are lock-free reads of a volatile field.
- Enabled messages are formatted on the calling thread, outside the lock, into a buffer of `MaxMessageBytes` rented from the logger and returned after dispatch. `LogEntry.Message` points into that buffer, so it is valid only during `Write`: a sink that keeps the message must copy it.
- **Message buffers:** each logger keeps up to 8 buffers of exactly `MaxMessageBytes`, in slots that each own one buffer for good. A slot creates its buffer the first time it's rented, and the buffer is never given to another pool, so a logger used from one thread holds one buffer. Renting claims the first free slot with a compare-and-swap, and returning finds the slot by the buffer's identity and releases the claim: lock-free, allocation-free, and each buffer has one renter at a time. When all 8 are claimed (more than 8 threads inside a log call at once), the call rents from `ArrayPool<byte>.Shared` instead, and that buffer goes back there. A cached buffer returned twice throws `InvalidOperationException`. Why not the shared pool: it trims itself after every gen2 collection, and when the memory load is at least 90% of the GC's high-load threshold the trim drops every thread's cached arrays, so the next log call allocated a new buffer. `Dispose` has nothing to free: the buffers are managed arrays, collected with the logger, and a call racing `Dispose` still returns its buffer.
- Logging `int`, `long`, `float`, `double`, `bool`, `string` and spans allocates no managed memory after warm-up, enabled or disabled, also after a gen2 collection has trimmed `ArrayPool<byte>.Shared`, for up to 8 threads logging through one logger at once. The same holds for any custom `IUtf8SpanFormattable` value type, such as the engine's maths types. `AppendFormatted<T>` is constrained to `IUtf8SpanFormattable` and calls `TryFormat` as a constrained call, so a value type is never boxed, whatever the JIT optimises (unoptimised and coverage-instrumented code included). It doesn't depend on the JIT removing a box, which proved fragile. A long message whose value doesn't fit the remaining space rents a second, temporary buffer from `ArrayPool<byte>.Shared` (a rare path, so it isn't cached).
- Creating a category allocates (once per name). `SetLevel`, `IsEnabled` and `SinkFailureCount` don't.

## Failure modes

| Failure | Result |
|---------|--------|
| Invalid options (null sinks or a null sink, null time provider, `MaxMessageBytes` out of range, undefined `MinimumLevel`) | `ArgumentException` (or a subclass) from the constructor. |
| Invalid or too many category names | `ArgumentException` / `InvalidOperationException`. |
| Sink throws | Isolated, counted in `SinkFailureCount`. `OutOfMemoryException` propagates. |
| Message too long | Truncated with `…`. |
| Formatting a value throws (a user `ToString` or `TryFormat`) | The exception reaches the caller and nothing is logged. The buffer goes back to the logger, so a failing formatter can't use up its buffers. |
| The `TimeProvider` throws | The exception reaches the caller and the entry isn't logged; later entries are unaffected. Like formatting code (and unlike a sink), it's caller-supplied configuration, so it isn't isolated. |
| Use after `Dispose` | `ObjectDisposedException`, except logging, which is a no-op. |

## Testing

- Unit tests for every rule above: idempotent, validated and capped categories, filtering with per-category overrides, formatting (mixed parts, format strings under a `de-DE` current culture), truncation, per-sink delivery order and fields (with a fixed `TimeProvider`), sink isolation, and the lifecycle.
- Disabled logging is checked with a counting `IUtf8SpanFormattable` that must never be formatted.
- Property test (CsCheck): random strings, including surrogate pairs and lone surrogates, under random limits, always give valid UTF-8 no longer than the limit, and an untruncated message round-trips.
- Concurrency: 8 threads log 2,000 entries each; every entry arrives exactly once and whole, and no sink sees two writes at once.
- Allocation tests: 1,000 enabled and 1,000 disabled logs of the common types, and 1,000 logs of a custom `IUtf8SpanFormattable` struct, allocate 0 managed bytes; so do `SetLevel`, `IsEnabled` and `SinkFailureCount`. They also pass under Windows dynamic coverage instrumentation, as CI runs them. Two more run after the shared pool has been trimmed between warm-up and the window (the test clears its thread's cached arrays as a high-pressure trim does, then empties the pool of that size): plain logging, and logging after more failing formatters than the logger has buffers (through `TryFormat` and through the `ToString` fallback). Mutation-checked: renting from the shared pool again, or not returning the buffer when either formatting path throws, fails them.
- Message buffer cache: reuse, distinct buffers while held, the fallback when every slot is claimed, fallback buffers going back to the fallback pool and cached ones never, a double return, invalid arguments, steady-state rent and return allocating 0 bytes, and 8 threads sharing 4 slots, where no buffer is ever held or written by two threads at once. Mutation-checked: freeing a slot whatever buffer is returned, a check-then-set claim instead of a compare-and-swap, and returning without releasing the claim each fail the suite.
- Mutation-checked: inverting the level comparison, or cutting truncated messages at a raw byte instead of a code-point boundary, fails the suite.
- Sinks: the line format is pinned byte for byte (every level token, UTC conversion, millisecond truncation, the caller suffix and its omission, lone surrogates), plus a CsCheck property that random categories and caller files fit `GetMaxLength` and give valid UTF-8. The console sink's routing, flushing, ownership and lifecycle are tested on recording streams; 8 threads writing 500 lines each produce only whole lines, with no overlapping `Write` calls; and steady-state console writes allocate 0 bytes, also under Windows dynamic coverage. Mutation-checked: swapping the routing comparison, or dropping the Error flush, fails the suite.
- Memory sink: overwriting by entry count and by bytes (with the ring wrapping), drop counting, truncation checked on the raw bytes for 2-, 3- and 4-byte code points, and `CopyTo` with buffers too small (newest without gaps) and with every field kept. A CsCheck property runs random writes and small copies against a `List`-based reference model, with its own rune-by-rune truncation. 8 writers and a concurrent reader find only whole entries, each writer's entries stay in order, and kept plus dropped equals written. Writes and copies that overwrite, truncate and wrap allocate 0 bytes, also under Windows dynamic coverage. Mutation-checked: an off-by-one in the ring wrap or the tail slot, cutting at a raw byte, or not counting drops, fails the suite.
- Rotating file sink: each test uses its own temp directory, and no test holds a handle across a rotation. The tests cover startup rotation (including `MaxFiles = 1`, stale archives and names without an extension), size rotation at the exact boundary, retention, oversized lines, the flush policy (each file is read with `FileShare.ReadWrite` while the sink holds it), `Dispose` releasing the file, and every invalid option. A CsCheck property writes random line lengths, sometimes over the maximum, under random `MaxFiles`, and compares every file with a reference model of the rotation rule. It also checks that the kept files are exactly the newest whole lines. 8 threads writing 500 lines each across several rotations produce only whole lines, all present, with no file over the maximum. Faults are forced on every OS without relying on permissions that root or an administrator would bypass: a file given as the directory, a non-empty directory where the archive must go, and (through an internal stream-opening seam) a real log file whose writes, flushes or final flush fail. They check `Fault`, `DroppedCount`, that the file is released, that the fault is permanent, and that through a logger nothing is thrown or counted as a sink failure. Two rotation failures in a row keep the older archives: the first faults the sink, and the second write is dropped and counted. Startup with archives beyond a smaller `MaxFiles` rotates and trims. Steady-state writes (with caller info, flushing at `Warning`, plus `Flush()` and `DroppedCount`) allocate 0 bytes, also under Windows dynamic coverage. Mutation-checked: not deleting archives beyond the limit, not resetting the byte counter after rotation, not counting the newline, shifting without overwriting, rotating an empty file, an off-by-one flush level, writing without the lock, swallowing an I/O exception without faulting (in the constructor, `Write`, `Flush` or `Dispose`), and not counting drops each fail the suite.
