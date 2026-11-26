# Design: input (`BrutalEngine.Input`)

## Purpose

`BrutalEngine.Input` turns platform events into **actions** a game reads by id: buttons, 1D axes and 2D axes, bound to keys, mouse controls and (later) gamepads, with composites, deadzones and processors, stacked contexts, runtime rebinding saved to `user://`, fixed-step latching so no press between ticks is lost, and recording and replay ([Architecture 3.10](../03-architecture.md#310-input-brutalengineinput), `docs/03-architecture.md:158-165`; [roadmap M2](../09-roadmap.md#m2-2d-essentials-03), `docs/09-roadmap.md:71`). It is an L3 library (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:29`): it references Core and Platform, and Assets through one listed edge (section 9). No SDL or other binding type and no World type appears in its public surface.

This note is the API sketch for Input's code issues (section 12). Signatures are the contract; XML docs, `PublicAPI.Unshipped.txt` entries, argument validation and tests follow the usual rules ([Coding standards](../07-coding-standards.md#public-api-design)). It references, and doesn't redefine, the [platform note](platform.md) (events, windows, the VFS, `ThreadAffinity`), the [runtime note](runtime.md) (modules, the frame loop) and the asset database note, `docs/design/assets.md` (#282, written in parallel), whose loader contract it uses by name only.

- Decision (maintainer, 2026-11-27): **polled action state.** Systems read actions through an object the host passes in (section 1). ECS events are designed later, with Physics2D. World is frozen until the 0.2.0 release, so nothing here needs a World change.
- Decision (maintainer, 2026-11-27): **host-agnostic.** Input names the calls a host makes each frame and each fixed step (section 8). Until Runtime gains built-in modules after the 0.2.0 release, a game wraps them in its own `EngineModule`.
- Decision (maintainer, 2026-11-27): **gamepad events come to Platform and Platform.Sdl3 after the 0.2.0 release**, then Input's gamepad bindings follow. Section 6 designs those bindings against a device model and names exactly what Platform must add.
- Decision (maintainer, 2026-11-27): **input maps are assets.** Input owns the input-map data model and its JSON form and registers a loader with the asset database through a listed same-layer edge `Input -> Assets` (section 9).

**The project (ADR-0016).** `src/BrutalEngine.Input/BrutalEngine.Input.csproj` declares exactly `<BrutalEngineMilestone>2</BrutalEngineMilestone>` and never sets `IsPackable` or `BrutalEngineReleaseMilestone` ([ADR-0016](../adr/0016-trunk-based-milestones.md), decision 3), so the 0.2.0 release doesn't pack it. Input's CHANGELOG entries go under `## Next: 0.3.0 (not in the next release)` (decision 5), never `[Unreleased]`. It references no BRUTAL package and no new NuGet package: JSON is the BCL's `System.Text.Json` with source generation (`docs/02-tech-stack.md:64`), hashing is the BCL's `SHA256`.

## 1. Public types

### Actions and their ids

```csharp
namespace BrutalEngine.Input;

public enum ActionKind : byte { Button = 0, Axis1D, Axis2D }

public readonly record struct ActionId          // untyped: rebinding, conflicts, user bindings
{
    public ActionKind Kind { get; }
    public bool IsDefault { get; }              // default(ActionId) is invalid
}

public readonly record struct ButtonAction { public ActionId Id { get; } }
public readonly record struct Axis1DAction { public ActionId Id { get; } }
public readonly record struct Axis2DAction { public ActionId Id { get; } }

public readonly record struct InputContextId
{
    public bool IsDefault { get; }
}
```

- **No strings in the hot path.** A game resolves names once, at setup: `ButtonAction jump = input.GetButton("jump");`. The typed ids make reading a button as an axis a compile error.
- **Layout.** An `ActionId` holds a 16-bit slot, the issuing service's 16-bit token and the kind (8 bytes). `InputContextId` holds a slot and the token. The token comes from a process-wide `static int` counter advanced with `Interlocked.Increment` (the static-state rule allows primitives), skipping 0 on wrap, so `default` never matches a service. Passing a default id, or one from another service, throws `ArgumentException`.
- **Ids never go stale.** A service's action table is append-only: `SetMap` keeps every name it has seen in its slot, appends new names, and leaves names the new map lacks in place, reading idle (not pressed, zero). So hot-reloading a map (section 9) never invalidates an id. A name that comes back with a different kind is an `InvalidOperationException` from `SetMap`, because typed ids already handed out would read the wrong kind.
- Immutable values, any thread, no allocation.

### Controls

```csharp
public enum ControlKind : byte
{
    None = 0,
    Key,                // Code: Scancode, the physical key
    Keycode,            // Code: Keycode.Value, the key in the current layout
    MouseButton,        // Code: MouseButton (1-5)
    MouseDelta,         // Code: AxisComponent; window coordinates moved since the last read
    MouseWheel,         // Code: AxisComponent; wheel amount since the last read
    MouseWheelStep,     // Code: 0 X+, 1 X-, 2 Y+, 3 Y-; one press and release per whole notch
    MousePosition,      // Code: AxisComponent; window coordinates
    // Appended by the gamepad issue (section 6): GamepadButton, GamepadAxis, GamepadHalfAxis
}

public enum AxisComponent : byte { X = 0, Y }

public readonly record struct InputControl
{
    public ControlKind Kind { get; }
    public uint Code { get; }
    public bool IsButtonLike { get; }       // Key, Keycode, MouseButton, MouseWheelStep (and GamepadButton, GamepadHalfAxis)
    public bool IsDelta { get; }            // MouseDelta, MouseWheel
    public static InputControl ForKey(Scancode scancode);
    public static InputControl ForKeycode(Keycode keycode);
    public static InputControl ForMouseButton(MouseButton button);
    public static InputControl ForMouseDelta(AxisComponent component);
    public static InputControl ForMouseWheel(AxisComponent component);
    public static InputControl ForMouseWheelStep(AxisComponent component, bool negative);
    public static InputControl ForMousePosition(AxisComponent component);
    public static bool TryParse(ReadOnlySpan<char> text, out InputControl control);
    public override string ToString();       // the text form below; allocates
}
```

`Scancode`, `Keycode` and `MouseButton` are Platform's (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:110`, `:106`, `:163`). Factories throw `ArgumentOutOfRangeException` for `Scancode.Unknown` (`:541`), an undefined value, `Keycode` 0 or `MouseButton.None`. `default(InputControl)` (kind `None`) means "unbound".

**Keys bind by scancode by default.** A scancode is the physical key, so the default WASD block stays where the hand expects it on AZERTY and Dvorak layouts, and a binding saved on one layout means the same key on another. Keycode bindings exist for mnemonic shortcuts that must follow the letter (`I` for inventory); they match the keycode SDL reported at the key's press, so a layout switch while a key is held still releases it (section 2). Rebinding captures scancodes unless the request asks for keycodes (section 5).

**Text form** (JSON files, section 9 and section 5), matched with `TryParse` and written by `ToString`:

| Kind | Text | Examples |
|------|------|----------|
| `Key` | `key:<Scancode member name>` | `key:W`, `key:Space`, `key:Lctrl` |
| `Keycode` | `keycode:<one character>` for a character key, `keycode:0x<8 hex digits>` otherwise | `keycode:i`, `keycode:0x4000003A` |
| `MouseButton` | `mouse:<MouseButton member name>` | `mouse:Left`, `mouse:X1` |
| `MouseDelta`, `MouseWheel`, `MousePosition` | `mouse-delta:X`, `mouse-wheel:Y`, `mouse-position:X` | |
| `MouseWheelStep` | `mouse-wheel:Y+`, `mouse-wheel:Y-` (and `X+`, `X-`) | |

Member names match `Enum.GetNames<T>()` exactly, ignoring case (never `Enum.TryParse`, which accepts numbers and comma lists); `Unknown` and `None` are rejected, and `ToString` writes the declared casing. A `keycode:` character must be one Unicode scalar that `Keycode.IsCharacter` accepts; ASCII upper-case letters fold to lower case, because SDL reports letter keys as lower-case code points (`docs/design/platform.md:249`). `TryParse` allocates nothing; it runs at load time.

### Bindings and processors

```csharp
public enum CompositeKind : byte
{
    None = 0,
    Axis1D,     // parts Negative, Positive (button-like); value Positive - Negative
    Axis2D,     // parts Up, Down, Left, Right (button-like); value (Right - Left, Down - Up)
    Vector2,    // parts X, Y (analog or delta, not mixed); value (X, Y)
}

public enum CompositePart : byte { None = 0, Negative, Positive, Up, Down, Left, Right, X, Y }

public readonly record struct CompositeControl(CompositePart Part, InputControl Control);

public sealed record InputBinding
{
    public InputControl Control { get; init; }                          // simple binding; default for a composite
    public CompositeKind Composite { get; init; }
    public IReadOnlyList<CompositeControl> Parts { get; init; } = [];
    public IReadOnlyList<InputControl> Modifiers { get; init; } = [];   // button-like; all held, or the binding reads 0
    public IReadOnlyList<InputProcessor> Processors { get; init; } = [];
}

public enum ProcessorKind : byte { None = 0, Deadzone, Scale, Invert, Normalize }

public readonly record struct InputProcessor
{
    public ProcessorKind Kind { get; }
    public float X { get; }      // Deadzone: lower bound; Scale: x factor; Invert: 1 inverts X
    public float Y { get; }      // Deadzone: upper bound; Scale: y factor; Invert: 1 inverts Y
    public static InputProcessor Deadzone(float lower, float upper);
    public static InputProcessor Scale(float x, float y);
    public static InputProcessor Invert(bool x, bool y);
    public static InputProcessor Normalize();
}
```

**2D convention.** Every 2D value uses window coordinates' directions, +X right and **+Y down**: mouse positions and deltas (`docs/design/platform.md:243`), SDL's stick axes (section 6), and so `Axis2D` composites too (`Down - Up`). Nothing is flipped behind the game's back; a y-up game inverts once with `Invert(false, true)` on the action.

**Values.** A button-like control reads 0 or 1. `MouseDelta` and `MouseWheel` read the amount accumulated since the view's last sample (section 3); `MousePosition` reads the last position. A binding's value is its control's value (a scalar), or its composite's (a scalar for `Axis1D`, a `Vector2` otherwise); it is 0 when any modifier isn't held; then its own processors run in order. An action takes the value of the binding with the **largest magnitude** (ties: the lower index), so a key and a stick bound to one action never add up to double speed, and then runs its own processors. A button is pressed while that magnitude is at least `PressThreshold`; `GetMagnitude` reads it.

**Processors**, exactly (`m` is `|v|` for a scalar, the length for a vector):

| Kind | Scalar `v` | Vector `v` | Valid arguments |
|------|-----------|-----------|-----------------|
| `Deadzone(lower, upper)` | 0 if `m < lower`, else `sign(v) * min(1, (m - lower) / (upper - lower))` | radial: the same factor applied to the length, direction kept | `0 <= lower < upper <= 1` |
| `Scale(x, y)` | `v * x` | `(v.X * x, v.Y * y)` | finite |
| `Invert(x, y)` | `-v` when `x` | negate the chosen components | |
| `Normalize()` | clamp to `[-1, 1]` | `v / m` when `m > 1`, else unchanged (diagonal keys give length 1; a stick inside the circle keeps its magnitude) | |

The arithmetic is IEEE single precision in this order, with `MathF.Sqrt` for lengths, so it is deterministic (section 7). Factories throw `ArgumentOutOfRangeException` outside the valid arguments.

### Actions, contexts and the map

```csharp
public sealed record InputActionDefinition
{
    public required string Name { get; init; }                       // [a-z0-9][a-z0-9._-]{0,63}, unique in the map
    public required ActionKind Kind { get; init; }
    public IReadOnlyList<InputBinding> Bindings { get; init; } = [];  // at most 16
    public IReadOnlyList<InputProcessor> Processors { get; init; } = [];
    public bool PassThrough { get; init; }                           // false: consumes its controls (section 4)
    public float PressThreshold { get; init; } = 0.5f;               // Button: magnitude that counts as pressed, (0, 1]
    public bool KeyRepeat { get; init; }                             // Button: OS key repeats add presses (section 2)
}

public sealed record InputContextDefinition
{
    public required string Name { get; init; }                       // same grammar, unique in the map
    public IReadOnlyList<InputActionDefinition> Actions { get; init; } = [];
    public bool TextEntry { get; init; }                             // section 4
    public bool BlocksLowerContexts { get; init; }                   // modal: contexts below see nothing
}

public sealed record InputMapDefinition
{
    public required string Name { get; init; }                       // same grammar; names the user-bindings file
    public IReadOnlyList<InputContextDefinition> Contexts { get; init; } = [];  // 1..64
}

public sealed class InputMap                                         // immutable, validated
{
    public static Result<InputMap> Create(InputMapDefinition definition);
    public static Result<InputMap> FromJson(ReadOnlySpan<byte> utf8Json);   // section 9
    public InputMapDefinition Definition { get; }
    public string Name { get; }
    public int ActionCount { get; }
    public ReadOnlySpan<byte> ContentHash { get; }                   // SHA-256 of ToJson(): identifies the map (section 7)
    public byte[] ToJson();                                          // canonical UTF-8 JSON (section 9); a new array
}
```

`InputMap.Create` checks every rule below and fails with `ErrorKind.InvalidData`, naming the context, the action, the binding index and the rule, because a map is data from a file. `null` arguments throw.

- Names follow the grammar; action names are unique across the whole map (so `GetButton("jump")` is unambiguous), context names among contexts. Limits: 64 contexts, 4,096 actions, 16 bindings per action, 4 modifiers per binding, 8 processors per list.
- **By action kind:** a `Button` takes simple button-like or analog bindings (pressed at `PressThreshold`), never `MouseDelta`, `MouseWheel` or `MousePosition`; an `Axis1D` takes simple bindings or `Axis1D` composites; an `Axis2D` takes `Axis2D` or `Vector2` composites only. `KeyRepeat` is valid on buttons only.
- **Composites:** each part of the kind exactly once and no other; `Axis1D` and `Axis2D` parts button-like; `Vector2` parts not button-like and all delta or all level. `Control` is default exactly when `Composite` isn't `None`.
- **Controls and processors:** kinds and codes defined and in range (`Scancode.Unknown`, `Keycode` 0 and `MouseButton.None` rejected); modifiers button-like; processor kinds defined and not `None`, arguments valid.

`InputMap` and the definition records are immutable and thread-safe, so an asset loader may build a map on a worker thread (section 9). Building a map allocates (tables, the canonical JSON and its hash); it never runs in a frame.

### The service and what systems read

```csharp
public sealed record InputOptions
{
    public required InputMap Map { get; init; }
    public WindowId Window { get; init; }                  // the input window; default: every window counts (section 2)
    public IReadOnlyList<string>? InitialContexts { get; init; }   // bottom first; null: the map's first context
    public int Players { get; init; } = 1;                 // 1..8 (section 6)
    public int TextCapacityBytes { get; init; } = 8192;    // committed text kept per frame, 1024..1048576
    public Logger? Logger { get; init; }                   // category "Input": user-binding and replay warnings
    public IPlatform? Platform { get; init; }              // for rumble only (section 6); null: rumble is Unavailable
    public GamepadAssignment GamepadAssignment { get; init; } = GamepadAssignment.FirstFreePlayer;
    public bool GamepadsWhileUnfocused { get; init; }      // section 6
}

public sealed class InputService
{
    public InputService(InputOptions options);
    public InputMap Map { get; }
    public void SetMap(InputMap map);                      // section 9: hot reload; user overrides kept where they apply

    public ButtonAction GetButton(string name);            // ArgumentException: unknown name or other kind
    public Axis1DAction GetAxis1D(string name);
    public Axis2DAction GetAxis2D(string name);
    public bool TryGetAction(string name, out ActionId action);
    public string GetName(ActionId action);
    public InputContextId GetContext(string name);

    public void BeginFrame(PlatformEventBatch events, TimeSpan frameDelta);   // section 2
    public void BeginFixedStep();                          // section 3
    public void Rebase();                                  // section 3
    public void MarkBatchesSkipped();                      // section 2: the host pumped a batch it didn't pass in
    public long FrameCount { get; }
    public long FixedStepCount { get; }

    public InputView Frame { get; }                        // player 0's per-frame view
    public InputView Fixed { get; }                        // player 0's per-fixed-step view
    public InputPlayer GetPlayer(int index);

    public void PushContext(InputContextId context);       // section 4
    public void PopContext(InputContextId expectedTop);
    public bool IsContextActive(InputContextId context);
    public int ContextDepth { get; }
    public bool WantsTextInput { get; }
    public ReadOnlySpan<byte> Text { get; }                // this frame's committed UTF-8 text, when the top context is TextEntry
    public long TruncatedTextCount { get; }

    public bool IsKeyDown(Scancode scancode);              // raw device state, before contexts: tools and overlays
    public MouseSnapshot Mouse { get; }

    public RebindOperation StartRebind(RebindRequest request);   // section 5
    public void SetBinding(BindingSlot slot, InputControl control);
    public void ResetBinding(BindingSlot slot);
    public void ResetBindings();
    public UserBindings GetUserBindings();
    public UserBindingsReport ApplyUserBindings(UserBindings bindings);

    public void AssignKeyboardAndMouse(int player);        // section 6
    public void AssignGamepad(GamepadId gamepad, int player);
    public void UnassignGamepad(GamepadId gamepad);
    public bool IsGamepadButtonDown(GamepadId gamepad, GamepadButton button);
    public ReadOnlySpan<GamepadChange> GamepadChanges { get; }
    public Result Rumble(int player, GamepadRumble rumble);

    public InputRecorder StartRecording(InputRecorderOptions options);   // section 7
}

public sealed class InputView                              // what systems read
{
    public InputViewKind Kind { get; }
    public int Player { get; }
    public bool IsPressed(ButtonAction action);            // held now
    public bool WasPressed(ButtonAction action);           // at least one press edge in this view's interval
    public bool WasReleased(ButtonAction action);
    public int GetPressCount(ButtonAction action);         // press edges in the interval
    public float GetMagnitude(ButtonAction action);        // the combined value before the threshold: triggers
    public float GetValue(Axis1DAction action);
    public Vector2 GetValue(Axis2DAction action);
}

public enum InputViewKind : byte { Frame = 0, FixedStep }

public sealed class InputPlayer
{
    public int Index { get; }
    public InputView Frame { get; }
    public InputView Fixed { get; }
    public bool HasKeyboardAndMouse { get; }
    public GamepadId Gamepad { get; }                      // default: none
}

public readonly record struct MouseSnapshot(Vector2 Position, MouseButtons Buttons, bool IsInWindow);
```

- **How systems get it.** The game passes the view into the systems it constructs: `new PlayerMovementSystem(input.Fixed)`. A view is a long-lived object; reading it allocates nothing. Use `Fixed` in `FixedUpdate` systems and `Frame` in `Update` systems (section 3).
- **Ownership.** The game (or, after the 0.2.0 release, Runtime) creates and owns the service. It holds no native resource, so it isn't `IDisposable`; its storage is managed arrays of primitives sized at `SetMap`, which the GC never scans, so no `INativeAllocator` is involved. Views and players belong to the service and live as long as it does.
- `GamepadId`, `GamepadButton`, `GamepadAssignment`, `GamepadChange` and `GamepadRumble` are section 6's; the members that use them arrive with the gamepad issues (section 12).
- `Vector2` is `System.Numerics.Vector2`, as for every public maths type.

## 2. Sampling

`BeginFrame(events, frameDelta)` reads one pump's `PlatformEventBatch` (`IPlatform.PumpEvents` and `IPlatform.Events`, `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:68`, `:63`; `PlatformEventBatch.Events` and `GetText`, `:225`, `:226`). `frameDelta` is recorded (section 7) and used for nothing else. The batch is valid until the next pump (`docs/design/platform.md:285`); Input copies what it keeps (state and text) and never stores the batch.

**Algorithm.** The frame view's edges, press counts and text are cleared. Then every event is applied **in batch order**, the OS order that latching relies on (`docs/design/platform.md:243-244`), never sorted by timestamp. After an event changes a control, every action bound to that control (a control-to-actions index built at `SetMap`) is re-evaluated through its context's visibility (section 4) and modifiers. A pressed-state change from false to true is a **press edge**, true to false a **release edge**; each edge adds to the frame view's counters and to the fixed view's pending counters (section 3). Axis actions are evaluated once, after the last event.

| Event (`PlatformEventKind`, `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:252-282`) | Effect |
|-------|--------|
| `KeyDown`, not a repeat | From the input window and while focused: the scancode goes down, and the keycode reported now is stored with it |
| `KeyDown` with `IsRepeat` (`KeyEventData.IsRepeat`, `:102`) | No state change. For a key that is down and not masked (section 3), every `KeyRepeat` button bound to it gets one extra press edge (menu navigation). Otherwise ignored |
| `KeyUp` | From **any** window: the scancode goes up, and so does the keycode stored at its press (not the one in this event, which may differ after a layout switch) |
| `MouseButtonDown`, `MouseButtonUp` | Like keys: presses from the input window while focused, releases from any window (`MouseButtonData.Button`, `:163`) |
| `MouseMotion` | From the input window: position set to `X`, `Y`; `DeltaX`, `DeltaY` added to both views' delta accumulators (`:186-189`) |
| `MouseWheel` | From the input window: `X`, `Y` added to both views' wheel accumulators (`:206-208`); each whole notch crossed by a per-direction accumulator is one press and one release edge of the matching `MouseWheelStep` |
| `TextInput` | From the input window, when the top context is `TextEntry`: its bytes (`GetText`) are appended to `Text`; past `TextCapacityBytes` the rest of the frame's text is dropped at a code-point boundary and `TruncatedTextCount` grows |
| `WindowFocusLost` (`:271`) | For the input window: every held key and mouse button is released, as edges at this position in the order; then presses and motion are ignored until `WindowFocusGained` (`:270`) |
| `WindowFocusGained` (`:270`), `WindowRestored` (`:281`) | For the input window: every held key and mouse button is released the same way (nothing happens when none is held); after `WindowFocusGained`, presses and motion count again |
| `WindowMouseLeft`, `WindowMouseEntered` | `Mouse.IsInWindow` |
| Gamepad kinds | Section 6 |
| Every other kind | Ignored |

- **Which window.** With `InputOptions.Window` default, every window is the input window. Otherwise presses, motion and wheel count only from that window or from events with a default `WindowId`, which SDL reports when it knows no window (`docs/design/platform.md:239`). Releases always count, so a key pressed in the game window and released over a tool window never sticks.
- **Focus.** A new service assumes it is focused. Releasing on focus loss is Input's own rule, whatever SDL sends: a release of a control that is already up does nothing, so duplicates are harmless.
- **Batches the host never passes in.** The host contract (section 8) is that every pumped batch reaches `BeginFrame` before the next pump. A batch that doesn't is lost to Input, together with any `KeyUp`, `MouseButtonUp` or `WindowFocusLost` in it, so the controls it would have released still read held. As defence in depth, Input doesn't rely on the contract alone to keep controls from sticking. Releasing on `WindowFocusGained` and `WindowRestored` means a control held when the window was minimised or lost focus reads up once the window is back, even when every batch in between was dropped, provided the batch that brings the window back reaches Input (in Runtime's loop it does: the minimised check follows the pump, so the pump that finds the window restored feeds that frame's `BeginFrame`). A key still physically held at that point must be pressed again: a hold can be missed, but a control never sticks. A host that drops a batch for any other reason calls `MarkBatchesSkipped()`: the next `BeginFrame` then starts by releasing every held key and mouse button (and gamepad control, section 6) as edges, before its first event. It is recorded (section 7), so a replay matches.
- **Relative mouse mode** (`docs/design/platform.md:170`) needs nothing from Input: motion events carry deltas in both modes, so `MouseDelta` bindings work either way, and `MousePosition` keeps the last reported position, which means little while the cursor is hidden. Positions and deltas are window coordinates; converting to pixels or content units is the platform note's formula (`docs/design/platform.md:243`). How SDL scales relative deltas is SDL's documentation, not observed here (section 11).
- **Text input** itself is switched on by the host: `WantsTextInput` is true while the top context is `TextEntry`, and the host calls `IWindow.StartTextInput`/`StopTextInput` when it differs from `IWindow.IsTextInputActive` (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:95`, `:97`, `:85`; section 8).

## 3. Fixed-step latching

Two views read the same action state over different intervals:

- **`Frame`**, for `Update` systems: the interval is the last `BeginFrame`'s batch. Its edges and counts are the batch's; its levels are the state after the batch; its deltas are the batch's sums.
- **`Fixed`**, for `FixedUpdate` systems: the interval is from the previous `BeginFixedStep` (or the service's creation or the last `Rebase`) to this one. **Consumed by a fixed tick** means: `BeginFixedStep` moves every pending edge count and delta sum into the step's view and zeroes the pending ones, so each edge and each delta reaches exactly one fixed step.

Exactly, per button action: `BeginFrame` adds the batch's press and release edges to `pendingPresses` and `pendingReleases` (and the batch's mouse deltas and wheel amounts to the pending sums). `BeginFixedStep` sets the step's `PressCount = pendingPresses`, `WasPressed = pendingPresses > 0`, `WasReleased = pendingReleases > 0`, `IsPressed` = the current level, deltas = the pending sums, then zeroes the pending values. The current level, of buttons and axes alike (keys, sticks, positions), is the value after the last batch, or after a later `Rebase`, which re-evaluates it at the call (below). Counters saturate at `int.MaxValue`.

| Case | `Frame` view | `Fixed` view |
|------|-------------|--------------|
| Press and release inside one frame, then one step | `WasPressed`, `WasReleased`, not `IsPressed`, count 1 | The same in that step |
| Press, then several steps in the frame | `WasPressed`, `IsPressed` | First step: `WasPressed`, `IsPressed`. Later steps: `IsPressed` only |
| Press and release in a frame with **zero** steps | As in the first row | Nothing yet: the edges stay pending, and the next frame's first step sees them (plus that frame's own) |
| Two frames, each a tap, with no step between | count 1 in each frame | The next step: count 2, not `IsPressed` |
| Held across frames | `IsPressed`; no edges after the first frame | `IsPressed`; `WasPressed` only in the step that consumed the press |
| Release then press again in one frame | `WasReleased`, `WasPressed`, `IsPressed` | The same in the step that consumes them |
| Mouse delta over a frame with zero steps | That frame's sum | Added to the next step's delta, never lost, never doubled |
| `Rebase` while the key of a button is held, then steps in that frame | Cleared at the call: no edges, not `IsPressed` | Not `IsPressed` and no `WasReleased` in every step; the key's later release adds no edge |
| A press and release still pending, then `Rebase` | No edges | The next step: nothing |
| Repeats of a key held across `Rebase`, bound to a `KeyRepeat` button | No presses | No presses |
| Half a wheel notch before `Rebase`, half after | No `MouseWheelStep` press | None |
| A key held across `Rebase`, released, then pressed again | The new press is an ordinary press edge | Likewise |

**Why all edges go to the first step.** Within a frame, events are known only at its start, and their timestamps are the pump's translation time (`docs/design/platform.md:244`), not the moment the player acted, so they can't place an event between two steps of the same frame. Delivering every edge to the first step is the only rule that loses nothing and doubles nothing. "Pressed this step" is `WasPressed`; "held this step" is `IsPressed`; a gameplay jump reads `WasPressed` and a held charge reads `IsPressed`.

**`Rebase()`** makes the moment of the call the start of gameplay's input: after the splash, a loading screen, at the start of a recording (section 7). It takes effect at the call, makes no edge then or later, and allocates nothing:

- Pending fixed edges, the pending delta and wheel sums and the wheel-step remainders (section 2) are zeroed, and both views are cleared to an interval with no events: no edges, zero counts and deltas, empty `Text`.
- Every digital control that is down (keys, keycodes, mouse buttons, and gamepad buttons, section 6) is **masked**: it drives no action, its OS key repeats add no presses, and its release only unmasks it, with no edge anywhere. Raw device state (`IsKeyDown`, `Mouse`) still reports it. A context change (section 4) doesn't unmask it.
- Every action is re-evaluated at the call without the masked controls, and both views' levels (`IsPressed`, `GetMagnitude`, axis values) read that result until the next event changes it. So a fixed step later in the same frame reads `IsPressed` false for a jump held over the splash, not the level the batch left. The re-evaluation makes no edge.
- A `Button` action still pressed after masking (only an analog binding can do that: a trigger, a half axis) reads not pressed, with no edges, until its value falls below `PressThreshold`; the next crossing is an ordinary press. Analog levels (positions, stick axes) aren't masked: an axis action reads the stick where it is.

Misuse: `BeginFixedStep` before the first `BeginFrame` is allowed (it reports nothing); views have no other state machine to break.

## 4. Contexts

A **context** is a named set of actions (gameplay, menu, chat). Active contexts form a stack; only actions of contexts in the stack can change state, and an action outside the stack reads idle.

```csharp
input.PushContext(menu);               // InvalidOperationException if already in the stack, or 16 deep
input.PopContext(menu);                // InvalidOperationException unless menu is the top
```

- **Initial stack:** `InputOptions.InitialContexts` (bottom first), or the map's first context. `SetMap` keeps the stack, dropping contexts the new map lacks.
- **Changes apply at the call.** The current views keep what they sampled; the next `BeginFrame` evaluates actions through the new stack. A pending fixed edge of an action whose context was just covered is still delivered: it happened while the context was active.

**Consume or pass through, per action.** Visibility is computed whenever the stack or the bindings change (into arrays allocated at `SetMap`, so a push allocates nothing), walking from the top down with a set of blocked controls, empty at the top:

1. A context sees every control that isn't blocked.
2. Its actions that aren't `PassThrough` then block every control their bindings use: simple controls and composite parts. Modifiers don't block, so a menu's `Ctrl+S` leaves `Ctrl` to gameplay.
3. A `TextEntry` context blocks every `Key` and `Keycode` control except those its own actions bind (Enter to send, Escape to close), so typing never moves the character.
4. A context with `BlocksLowerContexts` blocks everything: contexts below it see nothing (a pause menu).

Consumption is **by binding, not by press**: while a menu that binds `Space` is above gameplay, gameplay never sees `Space`, even when the menu's action is idle. That keeps the rule static and testable.

**Text entry.** `Text` holds the frame's committed text only while the top context is `TextEntry`; editing (cursor, selection, IME composition) belongs to M3's UI text fields. `WantsTextInput` tells the host to start OS text input (section 2).

**Held controls across a change.** When a push or pop makes a control **newly visible** to a context while the control is held (the `Escape` that closed the menu, the click that opened it), that context ignores the control until it is released, so it never triggers there on the way in. Where visibility doesn't change, a held control keeps working: pushing a pass-through HUD context doesn't stop a held move key. Where a control becomes hidden, the action loses it, and a button that only that control held reports a release edge at the next `BeginFrame`.

## 5. Rebinding and user bindings

```csharp
public readonly record struct BindingSlot(ActionId Action, int Binding, CompositePart Part);  // Part None for a simple binding

public sealed record RebindRequest
{
    public required BindingSlot Slot { get; init; }          // Binding == the action's count appends a binding (up to 16)
    public RebindDevices Devices { get; init; } = RebindDevices.KeyboardAndMouse | RebindDevices.Gamepad;
    public RebindKeyMode KeyMode { get; init; } = RebindKeyMode.Scancode;
    public IReadOnlyList<InputControl> CancelControls { get; init; } = [InputControl.ForKey(Scancode.Escape)];
    public IReadOnlyList<InputControl> ExcludedControls { get; init; } = [];
    public float AxisThreshold { get; init; } = 0.5f;        // gamepad axes, (0, 1)
}

[Flags] public enum RebindDevices : byte { None = 0, KeyboardAndMouse = 1, Gamepad = 2 }
public enum RebindKeyMode : byte { Scancode = 0, Keycode }
public enum RebindState : byte { Listening = 0, Completed, Cancelled, Applied }
public enum RebindResolution : byte { KeepConflicts = 0, UnbindConflicts, Swap }

public sealed class RebindOperation
{
    public RebindRequest Request { get; }
    public RebindState State { get; }
    public InputControl Control { get; }                      // when Completed: the captured control
    public IReadOnlyList<BindingSlot> Conflicts { get; }      // when Completed: other slots of the context bound to it
    public void Cancel();                                     // Listening or Completed -> Cancelled
    public void Apply(RebindResolution resolution);           // Completed -> Applied
}
```

**The flow.** `StartRebind` validates the request (`ArgumentException` for a slot that doesn't exist or a part that doesn't match the binding) and returns an operation in `Listening`; a second one while one listens is an `InvalidOperationException`. While it listens, **every action reads idle**, and controls held when it started are ignored until released (the click on "Rebind" isn't captured). During `BeginFrame`, the first qualifying press completes it:

- a `CancelControls` press cancels it; `ExcludedControls` are ignored;
- for a button-like slot: a key (`Key` or `Keycode` by `KeyMode`), a mouse button, a wheel step, a gamepad button, or a gamepad axis crossing `AxisThreshold` from rest as a half axis; for a `Vector2` part or a simple `Axis1D` binding, a gamepad axis crossing it as a full axis. Mouse motion is never captured by listening; menus offer it through `SetBinding`.

The completing press is consumed: it is ignored until released, so it doesn't fire the action the moment the operation completes. `Conflicts` lists the other slots bound to the captured control in the same context, the action's own other bindings included. `Apply` sets the slot and, for `UnbindConflicts`, unbinds the conflicts; for `Swap`, gives the first conflict (in action order) the slot's previous control and unbinds the rest. `SetBinding(slot, control)` does the same without listening; it throws `ArgumentException` for a control the slot can't take (section 1's rules). Every change is recorded as an override of the map's default, so `ResetBinding` and `ResetBindings` restore defaults and a map update still brings new defaults for slots the player never touched. An appended slot (`Binding` equal to the action's binding count) is a simple binding, so its `Part` must be `None`. `SetMap` cancels a listening or completed operation.

**The user-bindings file**, `user://input/<map name>.json` by default:

```json
{
  "version": 1,
  "map": "player",
  "overrides": [
    { "action": "jump", "binding": 0, "control": "key:Space" },
    { "action": "move", "binding": 0, "part": "Up", "control": "key:Up" },
    { "action": "fire", "binding": 1, "control": null }
  ]
}
```

```csharp
public readonly record struct BindingOverride(string Action, int Binding, CompositePart Part, InputControl Control); // default Control: unbound

public sealed class UserBindings                         // immutable; thread-safe
{
    public string MapName { get; }
    public IReadOnlyList<BindingOverride> Overrides { get; }
    public static Result<UserBindings> FromJson(ReadOnlySpan<byte> utf8Json);
    public byte[] ToJson();
}

public sealed record UserBindingsReport(int Applied, IReadOnlyList<string> Ignored);

public static class UserBindingsFile
{
    public static VfsPath GetDefaultPath(InputMap map);  // user://input/<name>.json
    public static Result<UserBindingsReport> Load(InputService input, VirtualFileSystem files, VfsPath path);
    public static Result Save(InputService input, VirtualFileSystem files, VfsPath path);
}
```

- **Serialisation** is source-generated `System.Text.Json` over internal DTOs (`[JsonSerializable]` on an internal `JsonSerializerContext`): no reflection, NativeAOT-safe. Comments and trailing commas are rejected; unknown properties are skipped, so a later build of the game can add a property within version 1 without older builds refusing the file (input maps, which come with the game, are stricter: section 9). Files are at most 1 MiB and 4,096 overrides.
- **Versioned.** `version` 1 is the only version. A higher one fails with `ErrorKind.Unsupported` ("written by a newer version of the game"); a missing or lower one with `InvalidData`. A future version 2 reads version 1 files through a migration step in `FromJson`; every version bump adds one.
- **A bad file never stops the game.** `Load` returns a failure and leaves the service unchanged for a corrupt file (`InvalidData`), a newer version (`Unsupported`), a file for another map (`InvalidData`) or an I/O error (the VFS's kinds, `docs/design/platform.md:425-433`); a missing file is a success with nothing applied. Inside a good file, each entry that names an unknown action, a binding index above 15, a part that doesn't match the binding, a second entry for the same slot, or a control the slot can't take is skipped and listed in `Ignored`; the rest apply. An index past the map's bindings adds a simple binding there (any gap stays unbound). Overrides are keyed by action name, binding index and part, so `SetMap` re-applies them to the new map and drops (and logs) those that no longer fit. `Load` logs a `Warning` for any failure or ignored entry; the game continues with the defaults. The file is only rewritten by `Save`, through the VFS's atomic replace (`WriteAllBytes`, `docs/design/platform.md:395`), so a crash never leaves half a file. Saving after a newer-version file was refused overwrites it; that is the player's choice made by rebinding.
- `VirtualFileSystem` and `VfsPath` are Platform's, both on `main`: `VfsPath` at `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:605`, `VirtualFileSystem` at `:613`, with the `ReadAllBytes(VfsPath)` that `Load` uses at `:622` and the `WriteAllBytes(VfsPath, ReadOnlySpan<byte>)` that `Save` uses at `:625`. The pure `FromJson`/`ToJson` pair works without either, which is what the tests use.

## 6. Gamepads

Nothing in this section can run until Platform delivers gamepad events (decision above); it is the contract for that Platform issue and for Input's gamepad issue.

### The device model

- A **gamepad** is an SDL gamepad: a controller SDL maps to the standard layout. Joysticks without a mapping, touchpads, sensors and lights are out of scope.
- A gamepad is identified by a **`GamepadId`**, SDL's joystick instance id, which SDL documents as unique for as long as the device stays connected and not reused within a process (SDL's documentation; not observed here). A reconnected pad is a new id.
- Per gamepad, Input keeps 26 buttons (a bit set) and 6 axes (floats), zeroed when it is added. At most 16 gamepads are tracked; more are ignored and logged once.

### Players and assignment

- `InputOptions.Players` players (1..8), each with its own action state and its own `Frame` and `Fixed` views. The context stack is shared: one screen, one menu, one UI focus.
- **Keyboard and mouse** belong to player 0 unless `AssignKeyboardAndMouse` moves them.
- **Gamepads** follow `GamepadAssignment`: `FirstFreePlayer` (the default) assigns a gamepad, when it is added, to the lowest-index player without one, or leaves it unassigned when every player has one; `Manual` assigns none, and the game calls `AssignGamepad` (a "press Start to join" screen reads `IsGamepadButtonDown` for unassigned pads). Assigning a pad that another player holds moves it; its held controls are released for the old player.
- An action reads only its player's devices. Bindings belong to the map and the user-bindings file, so every player shares them; per-player bindings would be a later, additive option (a player index on `BindingSlot`).

```csharp
public enum GamepadAssignment : byte { FirstFreePlayer = 0, Manual }
public enum GamepadChangeKind : byte { Added = 0, Removed, Remapped, Assigned, Unassigned }
public readonly record struct GamepadChange(GamepadId Gamepad, GamepadChangeKind Kind, int Player);  // Player -1: none
```

- **Hot-plug.** `GamepadAdded` adds the device and assigns it by the policy. `GamepadRemoved` releases every held control of that pad (as edges at this position in the order) and unassigns it; the player keeps an empty slot, so the game can show "controller disconnected" from the `Removed` change. `GamepadRemapped` releases everything on that pad, because its layout changed. `GamepadChanges` lists the frame's changes in order, until the next `BeginFrame`.
- **Focus.** While the game is unfocused, gamepad presses are ignored unless `GamepadsWhileUnfocused`; releases always count.
- **Controls** appended to `ControlKind` by the gamepad issue: `GamepadButton` (code: `GamepadButton`), `GamepadAxis` (code: `GamepadAxis`; sticks -1..1, triggers 0..1) and `GamepadHalfAxis` (code: axis × 2, plus 1 for the negative half; 0..1, button-like). Text forms `gamepad:South`, `gamepad-axis:LeftX`, `gamepad-axis:LeftY-`. Factories `ForGamepadButton(GamepadButton)`, `ForGamepadAxis(GamepadAxis)`, `ForGamepadHalfAxis(GamepadAxis, bool negative)`. Until that issue lands, a map naming a gamepad control fails validation as an unknown control. Sticks bind as a `Vector2` composite of their two axes, with a radial `Deadzone` (`0.125`, `0.925` is a reasonable start).

**Rumble.** `Rumble(player, rumble)` forwards to `IPlatform.RumbleGamepad` for the player's pad. A player without a pad is `ErrorKind.NotFound`; `InputOptions.Platform` null is `Unavailable`; the platform's own failures pass through. Main thread; allocation-free on success.

### What Platform adds after the 0.2.0 release

All in `BrutalEngine.Platform` and `BrutalEngine.Platform.Sdl3`, appended so nothing is renumbered:

```csharp
namespace BrutalEngine.Platform;

public readonly record struct GamepadId(uint Value);                  // 0 = invalid; SDL_JoystickID

public enum GamepadButton : byte    // SDL_GamepadButton values 0-25; SDL's Invalid (-1) is never translated
{
    South = 0, East, West, North, Back, Guide, Start, LeftStick, RightStick, LeftShoulder, RightShoulder,
    DpadUp, DpadDown, DpadLeft, DpadRight, Misc1, RightPaddle1, LeftPaddle1, RightPaddle2, LeftPaddle2,
    Touchpad, Misc2, Misc3, Misc4, Misc5, Misc6,
}

public enum GamepadAxis : byte { LeftX = 0, LeftY, RightX, RightY, LeftTrigger, RightTrigger }   // SDL_GamepadAxis 0-5

public enum GamepadType : byte      // SDL_GamepadType 0-11
{
    Unknown = 0, Standard, Xbox360, XboxOne, Ps3, Ps4, Ps5, NintendoSwitchPro,
    NintendoSwitchJoyconLeft, NintendoSwitchJoyconRight, NintendoSwitchJoyconPair, Gamecube,
}

public readonly record struct GamepadDeviceData(GamepadId Gamepad);                           // payload 16-19
public readonly record struct GamepadButtonData(GamepadId Gamepad, GamepadButton Button);     // 16-19, 20
public readonly record struct GamepadAxisData(GamepadId Gamepad, GamepadAxis Axis, float Value); // 16-19, 20, 24-27

public readonly record struct GamepadRumble(float LowFrequency, float HighFrequency, TimeSpan Duration); // 0..1, 0..1, 0..49.7 days

public sealed record GamepadInfo
{
    public required GamepadId Id { get; init; }
    public required string Name { get; init; }
    public GamepadType Type { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public bool HasRumble { get; init; }
}
```

- **Event kinds** appended after `DisplayCurrentModeChanged = 29` (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:253-256`): `GamepadAdded = 30`, `GamepadRemoved = 31`, `GamepadRemapped = 32` (payload `GamepadDevice`), `GamepadButtonDown = 33`, `GamepadButtonUp = 34` (payload `GamepadButton`), `GamepadAxisMotion = 35` (payload `GamepadAxis`). They carry a default `WindowId`, like the `Display*` kinds; the factories `CreateGamepadDevice`, `CreateGamepadButton` and `CreateGamepadAxis` reject anything else, and the views `PlatformEvent.GamepadDevice`, `GamepadButton` and `GamepadAxis` throw for other kinds, as the existing views do (a property named like its enum, as `MouseButton` is). SDL's sources: `SDL_EVENT_GAMEPAD_AXIS_MOTION` to `SDL_EVENT_GAMEPAD_REMAPPED` (`Brutal.Sdl/Generated/Enums/SdlEventType.gen.cs:81-86`) and their structs (`Brutal.Sdl/Generated/Sdl.Structs.gen.cs:491-525`); the enums' values are SDL's (`Brutal.Sdl/Generated/Enums/SdlGamepadButton.gen.cs:10-36`, `Brutal.Sdl/Generated/Enums/SdlGamepadAxis.gen.cs:10-16`, `Brutal.Sdl/Generated/Enums/SdlGamepadType.gen.cs:9-21`), and a Platform.Sdl3 test asserts each member equals SDL's of the same name.
- **Axis values** are normalised by the backend: sticks `value / 32767` clamped to `[-1, 1]`, triggers `value / 32767` in `[0, 1]`, from SDL's `short` (`Brutal.Sdl/Generated/Sdl.Structs.gen.cs:501`). SDL documents stick values as negative up and left, positive down and right; the backend keeps that direction, which is Input's +Y-down convention (section 1).
- **`IPlatform` members:** `IReadOnlyList<GamepadInfo> Gamepads { get; }`, a snapshot rebuilt (allocating) when a gamepad event is pumped, like `Displays`; and `Result RumbleGamepad(GamepadId gamepad, GamepadRumble rumble)`, main thread, `NotFound` for an unknown pad, `Unsupported` without rumble, `Native` for SDL's failure.
- **Platform.Sdl3:** initialises `SDL_INIT_GAMEPAD` with video (`Sdl.Constants.InitGamepad`, `Brutal.Sdl/Generated/Sdl.Constants.gen.cs:378`), opens each added pad with `SDL_OpenGamepad` and closes it on removal or `Dispose` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:8301`, `:8886`), and rumbles with `SDL_RumbleGamepad`, whose duration is a `uint` of milliseconds (`:8826-8831`), so longer durations are clamped. It enables SDL's background joystick events so releases arrive while unfocused, and Input applies its focus rule.
- **No variable-length payload.** The platform note expected gamepads to need one, like text (`docs/design/platform.md:607`). Keeping device names in the `Gamepads` snapshot rather than in events makes every gamepad event fit the fixed 24-byte payload, so the event struct and buffer don't change.

## 7. Record and replay

```csharp
public sealed record InputRecorderOptions
{
    public int CapacityBytes { get; init; } = 16 * 1024 * 1024;   // 64 KiB..1 GiB, allocated at StartRecording
}

public sealed class InputRecorder
{
    public bool IsRecording { get; }
    public bool IsTruncated { get; }          // the buffer filled; frames after that weren't recorded
    public long FrameCount { get; }
    public void Stop();
    public InputRecording ToRecording();       // copies; allocates
}

public sealed class InputRecording            // immutable; thread-safe
{
    public static Result<InputRecording> Read(ReadOnlySpan<byte> bytes);
    public byte[] ToBytes();
    public long FrameCount { get; }
    public ReadOnlySpan<byte> MapHash { get; }
}

public sealed class InputReplayer
{
    public static Result<InputReplayer> Create(InputService input, InputRecording recording);
    public bool IsFinished { get; }
    public long FrameIndex { get; }
    public ReplayFrame NextFrame();            // InvalidOperationException when finished
}

public readonly ref struct ReplayFrame
{
    public PlatformEventBatch Events { get; }  // valid until the next NextFrame
    public int FixedSteps { get; }
    public TimeSpan FrameDelta { get; }
}
```

**What is recorded:** from `StartRecording` on, at each `BeginFrame`, the batch's events of the kinds Input applies (section 2's table: keys, text, mouse, focus, restore, mouse enter and leave, gamepad kinds once they exist), `frameDelta` and whether `MarkBatchesSkipped` came before it; and, when the next `BeginFrame` comes, how many `BeginFixedStep` calls the frame made. `StartRecording` stores the **start state** and then calls `Rebase()` (section 3): the map's `ContentHash`; the options replay depends on (`Players`, `TextCapacityBytes`, `GamepadAssignment`, `GamepadsWhileUnfocused`); the user bindings; the context stack and the keyboard-and-mouse player; the device state (focus, `IsInWindow`, the mouse position, each held key with the keycode stored at its press, the held mouse buttons); and each tracked gamepad's player, buttons and axes. Pending edges, sums, wheel-step remainders and the views aren't stored: `Rebase` clears them, and the masks it sets follow from the stored state. `StartRecording` while a rebind operation is listening or completed is an `InvalidOperationException`, because the operation's masks aren't recorded. Recording appends into the buffer allocated at the start, so a recorded frame allocates nothing; when the buffer is full, or a frame makes more than 65,535 fixed steps, recording stops and `IsTruncated` is set. `Stop` closes the last frame with its step count. One recorder at a time (`InvalidOperationException`).

**The file format**, version 1, little-endian, floats as IEEE bits:

| Part | Layout |
|------|--------|
| Header, 64 bytes | 0: ASCII `BEINPREC`; 8: `u16` format version (1); 10: `u16` flags (0); 12: `u32` start-block length `S`; 16: 32-byte map `ContentHash`; 48: `u64` frame count; 56: 8 bytes reserved (0) |
| Start block, `S` bytes, packed in this order | `u8` context count, then each context's name as `u8` length + UTF-8; `u32` length + the user bindings as section 5's JSON; `u8` player count; `u8` the keyboard-and-mouse player; `u8` `GamepadAssignment`; `u8` flags (bit 0 focused, bit 1 in window, bit 2 `GamepadsWhileUnfocused`; the others 0); `u32` `TextCapacityBytes`; `f32` mouse x, y; `u16` held-key count, then 8 bytes each (`u16` scancode, `u16` 0, `u32` the keycode stored at its press); `u8` held mouse buttons as `MouseButtons` bits (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:175-181`); `u8` gamepad count (at most 16), then 40 bytes each (`u32` gamepad id, `u8` player or 255 for none, 3 bytes 0, `u32` held buttons as bits 0-25, `u32` 0, 6 `f32` axes); zero padding to a multiple of 8. Until the gamepad issues land the gamepad count is 0, so the format doesn't change then |
| Each frame | A 24-byte frame header: 0 `u32` event count `E` (at most 65,536); 4 `u16` fixed steps; 6 `u16` flags (bit 0: batches were skipped before this frame; the other bits 0); 8 `u32` text bytes `T` (at most 1 MiB); 12 `u32` 0; 16 `i64` frame delta in `TimeSpan` ticks. Then `E` 40-byte event records, `T` bytes of UTF-8 text, and zero padding to a multiple of 8 |
| Event record, 40 bytes | 0: `u16` `PlatformEventKind`; 2: `u16` 0; 4: `u32` window: 0 for the input window or no window, 1 for any other; 8: `i64` timestamp (kept for diagnostics; Input doesn't use it); 16-39: payload, zero unless listed. Keys: 16 `u16` scancode, 18 `u16` modifiers, 20 `u32` keycode, 24 `u8` repeat. Text: 16 `u32` offset into the frame's text, 20 `u32` length. Motion: 16-28 `f32` x, y, delta x, delta y; 32 `u8` buttons. Buttons: 16 `u8` button, 17 `u8` clicks, 20-24 `f32` x, y. Wheel: 16-28 `f32` x, y, mouse x, mouse y. Gamepads: 16 `u32` id, 20 `u8` button or axis, 24 `f32` value |
| Trailer, 32 bytes | SHA-256 of everything before it |

`Read` checks, before allocating anything it sizes from the data: the length (at least header plus trailer), the magic, the version (2 or above: `Unsupported`, "recorded by a newer engine"; 0: `InvalidData`), the trailer hash, then every length against the bytes left, every count against the limits above, the frame count against the frames present, kinds against the recorded set, enum values and flag bits, text ranges inside their frame and valid UTF-8, and names against the map grammar. Any failure is `InvalidData` naming the offset.

**Replay.** `InputReplayer.Create` checks the recording's map hash against `input.Map.ContentHash` (a mismatch is `InvalidData`: "recorded with a different input map") and the recorded options against the service's (`InvalidData` naming the first that differs), and throws `InvalidOperationException` while a rebind operation is listening or completed. It then applies the recorded user bindings, sets the context stack and the keyboard-and-mouse player, replaces the device state with the recorded one (focus, in-window, position, held keys with their keycodes, held buttons, and each recorded gamepad with its player, buttons and axes) without edges or `GamepadChanges` entries, calls `Rebase()`, and allocates one `PlatformEventBuffer` sized for the largest recorded frame (`new PlatformEventBuffer(int, int)`, `Add`, `AddText`, `AsBatch`, `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:239`, `:230-232`; events rebuilt with the `PlatformEvent.Create*` factories, `:734-743`). Window 0 becomes the replaying service's `InputOptions.Window` (default when that is default) and window 1 the smallest id other than it (1 or 2), so window filtering replays the same way. A test harness's loop then takes its timing from the recording:

```csharp
while (!replayer.IsFinished)
{
    ReplayFrame frame = replayer.NextFrame();
    input.BeginFrame(frame.Events, frame.FrameDelta);
    for (int i = 0; i < frame.FixedSteps; i++) { input.BeginFixedStep(); world.RunSchedule(ScheduleLabel.FixedUpdate, fixedDelta); }
    world.RunSchedule(ScheduleLabel.Update, frame.FrameDelta);
}
```

**Determinism.** With the same map content and options, the recorded start state, the recorded events, step counts and skipped-batch flags, and the same calls from game code (pushes, pops, rebinding), these reads return the same values as in the recorded run, from the first recorded frame on: every `InputView` read, `Text`, `Mouse`, `IsKeyDown`, `WantsTextInput`, `IsContextActive`, `IsGamepadButtonDown` and `GamepadChanges`. The service's counters (`FrameCount`, `FixedStepCount`, `TruncatedTextCount`) count from its own creation and aren't replayed. The state is a pure function of that input (no clock, no hash order, fixed evaluation order, IEEE single-precision processors). On one build and architecture that is bit for bit; across win-x64 and linux-x64 the operations used are correctly rounded, so the values should match, and the replay tests run on both. The rest of the game's determinism (fixed-step simulation, [world note](world.md#7-schedules)) is the game's.

**Uses.** Tests replay committed recordings, or build them in code, against a reference model (section 11). Bug repros: a development build saves `recorder.ToRecording().ToBytes()` through the VFS (where and when is the game's choice), and a developer replays it in a harness that drives its own loop as above. Replay inside a Runtime game needs the host to take its step counts from the replayer, which `FixedStepClock` can't do in M1; that waits for Runtime's built-in modules after the 0.2.0 release.

## 8. Host integration

Per frame, on the main thread, in this order:

1. `IPlatform.PumpEvents()`.
2. `input.BeginFrame(platform.Events, frameDelta)`, before any schedule (`docs/03-architecture.md:59-60`). **Host contract:** every pumped batch reaches `BeginFrame` before the next pump, including frames that run no schedule (a minimised window, the splash). A host that can't pass one in calls `input.MarkBatchesSkipped()` before its next `BeginFrame` (section 2).
3. If `input.WantsTextInput != window.IsTextInputActive`, start or stop text input on the window.
4. For each fixed step: `input.BeginFixedStep()`, then `FixedUpdate`.
5. `Update`, `PostUpdate`, rendering: read `input.Frame`.

After the splash, a loading screen, or anything else gameplay shouldn't react to, call `input.Rebase()` once.

**With Runtime today.** Runtime calls every module's `BeginFrame` after the pump and before `FixedUpdate` (`docs/design/runtime.md:307`, `:312`), with the engine context in the frame (`:335-341`; `EngineContext.Platform`, `:187`), but it has no per-step hook, so the step call goes into the first `FixedUpdate` system. As the Runtime note stands, a minimised window's loop pumps, waits and `continue`s before `modules.BeginFrame` (`:309-310`), so those batches reach no module. Runtime's minimised path is planned to deliver them, to meet the host contract: the host calls the modules' `BeginFrame` with that batch (no fixed or update steps, no rendering) and then waits. That is a change to the Runtime note and its implementation, owned by Runtime's work. Section 2's release on `WindowRestored` and `WindowFocusGained` stays as defence in depth, so held controls never stick, whichever host runs Input. Both wrappers are game code:

```csharp
sealed class GameInputModule(InputMap map) : EngineModule(new ModuleInfo { Name = "game.input" })
{
    private HostPhase _lastPhase;
    public InputService? Input { get; private set; }

    public override Result Start(EngineContext context)
    {
        Input = new InputService(new InputOptions { Map = map, Logger = context.Logger });
        return Result.Success;
    }

    public override void BeginFrame(in FrameContext frame)
    {
        InputService input = Input!;
        input.BeginFrame(frame.Engine.Platform.Events, frame.Timing.Delta);
        if (frame.Phase == HostPhase.Running && _lastPhase != HostPhase.Running) input.Rebase();   // drop the splash's input
        _lastPhase = frame.Phase;

        IWindow? window = frame.Engine.Window;
        if (window is not null && input.WantsTextInput != window.IsTextInputActive)
        {
            Result changed = input.WantsTextInput ? window.StartTextInput() : window.StopTextInput();
            if (changed.IsFailure) { /* the game logs changed.Error and carries on */ }
        }
    }
}

sealed class InputFixedStepSystem(InputService input) : ISystem
{
    public void Configure(SystemBuilder builder) { }
    public void Run(in SystemContext context) => input.BeginFixedStep();
}
```

`EngineModule`, `ModuleInfo` and `FrameContext` are Runtime's (`docs/design/runtime.md:235-237`, `:228`, `:335-341`); `ISystem`, `SystemBuilder` and `SystemContext` are World's (`docs/design/world.md:313-317`). Input references neither: the game does. The game adds `InputFixedStepSystem` to `FixedUpdate` (`new SystemOptions { Name = "input.fixed-step" }`) **before any other system** with no ordering of its own; schedules break ties by insertion order (`docs/design/world.md:376`), so it runs first, and systems that read `input.Fixed` add `After = ["input.fixed-step"]` where it matters. The game registers the module in `IGame.ConfigureModules` with a map built in code or read from `res://` there; the service exists from the module's `Start`, before `IGame.Initialize`, where the game reads `context.Modules.Get<GameInputModule>().Input` and constructs its systems with the views.

**After Runtime gains built-in modules** (after the 0.2.0 release), Runtime owns an `InputService`, makes the same calls (step 4 from its own loop, so no system is needed), exposes it on `EngineContext`, and applies `Rebase` when the splash ends. The calls and their order are this section's; that Runtime change is its own issue.

## 9. The input-map asset

An input map is a JSON asset; `InputMap.FromJson` reads it and `InputMap.ToJson` writes its **canonical** form, which `ContentHash` hashes, so it is pinned exactly: `Utf8JsonWriter` with `Indented = true`, `IndentSize = 2` and `NewLine = "\n"`; properties in the order the records of section 1 declare them, `version` and `name` first; properties at their default value omitted; numbers in the writer's shortest round-trip form; no trailing newline. The example below is hand-formatted; the gamepad bindings in it validate once I28 lands (section 12).

```json
{
  "version": 1,
  "name": "player",
  "contexts": [
    {
      "name": "gameplay",
      "actions": [
        { "name": "jump", "kind": "button", "bindings": [ { "control": "key:Space" }, { "control": "gamepad:South" } ] },
        { "name": "move", "kind": "axis2d", "processors": [ { "kind": "normalize" } ],
          "bindings": [
            { "composite": "axis2d", "up": "key:W", "down": "key:S", "left": "key:A", "right": "key:D" },
            { "composite": "vector2", "x": "gamepad-axis:LeftX", "y": "gamepad-axis:LeftY",
              "processors": [ { "kind": "deadzone", "lower": 0.125, "upper": 0.925 } ] } ] },
        { "name": "zoom", "kind": "axis1d", "bindings": [ { "control": "mouse-wheel:Y", "modifiers": [ "key:Lctrl" ] } ] }
      ]
    },
    { "name": "chat", "textEntry": true, "actions": [ { "name": "send", "kind": "button", "bindings": [ { "control": "key:Return" } ] } ] }
  ]
}
```

- **Mapping:** each JSON object is the record of section 1 with camel-case names (`passThrough`, `pressThreshold`, `keyRepeat`, `textEntry`, `blocksLowerContexts`); `kind` values `button`, `axis1d`, `axis2d`; composites name their parts (`negative`/`positive`, `up`/`down`/`left`/`right`, `x`/`y`); processors are `{ "kind": "deadzone", "lower", "upper" }`, `{ "kind": "scale", "x", "y" }`, `{ "kind": "invert", "x": bool, "y": bool }`, `{ "kind": "normalize" }`; controls use the text form of section 1.
- **Reading** uses source-generated `System.Text.Json` into internal DTOs, then `InputMap.Create`'s validation. `version` 1 only (higher: `Unsupported`; missing or lower: `InvalidData`); unknown properties are `InvalidData` (a typo in a binding must not silently do nothing); at most 1 MiB. `FromJson` is pure and thread-safe.
- **Cooked form** is the canonical JSON: maps are a few KiB and load once, so a binary format would add a second reader for no gain. The cooker validates source maps with `FromJson` and writes `ToJson`'s bytes; that importer is the asset pipeline note's (#283) to register.

```csharp
public sealed class InputMapLoader       // stateless; thread-safe; implements the loader contract of #282
{
    public InputMapLoader();
    // plus the members of #282's loader contract, each forwarding the cooked bytes to InputMap.FromJson
}
```

- **The loader.** Input provides a public `InputMapLoader` that implements the loader contract of `docs/design/assets.md` (#282): given an input map's cooked bytes it returns `InputMap.FromJson(bytes)`, failing with that result's error; it has no dependencies on other assets. Its members, its registration call and the thread it runs on are #282's contract; it is safe on any thread because `FromJson` is. The game registers it with the database and passes the loaded map to `InputService` (`Map` in the options, or `SetMap` on hot reload, which keeps ids valid, section 1). If #282's contract can't express this loader as written, that note reports it, rather than this one redefining the contract.
- **The layer edge.** The issue that adds `InputMapLoader` (section 12) adds the listed same-layer edge to `EngineLayers` (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:30` lists Assets at L3; the only L3 edge today is Rendering2D → Text, `:62`) and to the edge table in [architecture tests](architecture-tests.md#the-layer-map):

| Edge | Reason |
|------|--------|
| Input → Assets | Input registers its input-map loader with the asset database ([input](input.md#9-the-input-map-asset)) |

Input's earlier issues don't reference Assets, so they don't wait for it.

## 10. Threading, ownership, allocation and failures

**Threads.** `InputService`, `InputView`, `InputPlayer`, `RebindOperation`, `InputRecorder` and `InputReplayer` are main thread only: the service captures Core's `ThreadAffinity` (`src/BrutalEngine.Core/PublicAPI.Unshipped.txt:257`) at construction, and **every** member of these types calls `ThrowIfNotCurrentThread` in every build (`WrongThreadException` naming the API; [platform note section 9](platform.md#9-threading), stricter than `docs/03-architecture.md:87`'s debug-only assertion, as in Platform, World and Runtime). A view read is one integer comparison more. The ids, `InputControl`, `InputProcessor`, the definition records, `InputMap`, `UserBindings` and `InputRecording` are immutable and thread-safe. `UserBindingsFile` runs on the calling thread and is main thread only because it touches the service.

| Type | Threads | Ownership | Allocation |
|------|---------|-----------|------------|
| `ActionId`, the typed ids, `InputContextId`, `InputControl`, `InputProcessor`, `CompositeControl`, `BindingSlot`, `BindingOverride`, `MouseSnapshot`, `GamepadChange`, every enum | any (immutable values) | values | none, except `InputControl.ToString` |
| `InputBinding`, the three definition records, `InputOptions`, `RebindRequest`, `InputRecorderOptions`, `UserBindingsReport` | any (immutable; read once by the call that takes them) | the caller's | at creation |
| `InputMap`, `UserBindings`, `InputRecording` | any (immutable) | shared; no disposal | at creation, `ToJson`, `ToBytes` |
| `InputMapLoader` | any (stateless) | the asset database holds it once registered | as `FromJson` |
| `InputService`, `InputView`, `InputPlayer` | main thread | the game owns the service; views and players are the service's | the list below |
| `RebindOperation`, `InputRecorder`, `InputReplayer`, `ReplayFrame` | main thread | the service's; `ReplayFrame` is valid until the next `NextFrame` | at their creation |
| `UserBindingsFile` | main thread (it reads and writes the service) | stateless | always (I/O) |
| Platform additions (section 6) | as the platform note's equivalents: payload types any thread, `IPlatform` members main thread | `GamepadInfo` snapshot owned by the platform | `Gamepads` rebuilds allocate |

**Ownership.** The game owns the service and the map it passes; `InputMap` is shared and immutable, so one map can serve several services. Input never owns the `IPlatform`, the `VirtualFileSystem` or the batch it is given. A recorder and a replayer belong to their service and stop being usable when another starts.

**Allocation.** Steady-state frames allocate **nothing**: `BeginFrame` over every event kind, `BeginFixedStep`, `Rebase`, `MarkBatchesSkipped`, every view read, `PushContext`/`PopContext` (visibility arrays are sized at `SetMap`), `Text`, `Mouse`, `IsKeyDown`, `GamepadChanges`, `Rumble` on success, recording within capacity, and `NextFrame`. These allocate, and never belong in a measured frame (Runtime's `MarkFrameNonSteady`): constructing the service; `SetMap`; name lookups that fail (the exception); building, parsing and writing maps; `StartRebind` and its completion (the conflict list) and `Apply`; `SetBinding`, `ResetBinding(s)`; user-bindings load, save, `GetUserBindings` and `ApplyUserBindings`; `StartRecording`, `ToRecording`, `ToBytes`, `Read` and `InputReplayer.Create`; and failures (their messages). Device state for the 16 trackable gamepads is allocated with the service, so hot-plug allocates nothing in Input (the platform's `Gamepads` snapshot does).

**Failure modes.**

| Failure | Result |
|---------|--------|
| A map that breaks a rule of section 1, or bad map JSON | `InputMap.Create`/`FromJson` fails: `InvalidData` naming the place and rule; a newer map version: `Unsupported` |
| Corrupt, foreign or newer user-bindings file; an I/O error | `UserBindingsFile.Load` fails (`InvalidData`, `Unsupported`, the VFS's kinds), service unchanged, `Warning` logged, the game continues on defaults; a missing file is success |
| Bad entries in a good user-bindings file | Skipped and listed in `UserBindingsReport.Ignored`, `Warning` logged |
| Corrupt or newer recording; replay against another map or other options | `InputRecording.Read`/`InputReplayer.Create` fail: `InvalidData` or `Unsupported` |
| Recorder buffer full | Recording stops, `IsTruncated` set; never an exception in a frame |
| More text in a frame than `TextCapacityBytes` | Cut at a code-point boundary, counted in `TruncatedTextCount` |
| Rumble without a pad, platform or support | `NotFound`, `Unavailable`, `Unsupported` |
| Wrong thread | `WrongThreadException` |
| Default or foreign ids; unknown names; a name reused with another kind in `SetMap`; invalid arguments | `ArgumentException`, `InvalidOperationException` (`SetMap`) |
| Context misuse (push twice, pop the wrong top, deeper than 16); a second rebind or recorder; recording or replay while a rebind is listening or completed; `Apply` outside `Completed`; `NextFrame` after the end | `InvalidOperationException` |

## 11. Testing

All pure, in `tests/BrutalEngine.Input.Tests`, on Windows and Linux CI. Batches are built without SDL through Platform's public buffer and factories (section 7's citations), and the clock is never read.

- **Unit tests** for every rule: id resolution, staleness across `SetMap` and the kind-change exception; control text forms (every kind, case folding, every rejection: numbers, comma lists, `Unknown`, `None`, upper-case keycode letters folded); every validation rule of `InputMap.Create` with a row each side of every limit; each processor's formula at and around its bounds; composites (both keys of an axis held give 0, diagonals, modifiers); sampling (repeats, focus loss releasing in order, releases from other windows, default-window events, keycode release after a layout change, wheel notches from fractional amounts); skipped batches (a key and a button held, a minimise batch carrying their releases and `WindowFocusLost` never passed in, then a batch with `WindowRestored`: both read up after it with one release edge each and no press; the same with only `WindowFocusGained`; `MarkBatchesSkipped` releasing before the next batch's first event; a key held through the restore needs a new press); contexts (consumption by binding, modifiers not consuming, `TextEntry` blocking keys except its own, `BlocksLowerContexts`, newly visible held controls masked, pass-through HUD keeping a held key, a covered action's release edge, pending fixed edges delivered after covering, misuse); rebinding (each state change, the held-at-start mask, the consumed completing press, cancel and excluded controls, each resolution including `Swap` with several conflicts, illegal controls); user-bindings JSON (round trip, each version rule, a foreign map, every skipped-entry kind, size limits); recording format (round trip, every `Read` rejection, the hash and map checks).
- **Property tests (CsCheck)** against a reference model written separately in the test project: random per-frame event lists and step counts (zero steps over-sampled, press and release in one frame over-sampled); after every step, the fixed view's press count equals the model's edges since the previous step, every edge reaches exactly one step, the sum of fixed deltas equals the sum of frame deltas, and the frame view equals the model per frame. Random context stacks and bindings: an action sees a control exactly when the model's top-down walk says so. Random `Rebase` calls, over-sampled while controls are held and between `BeginFrame` and the frame's steps: no edge from before the call reaches any view after it, a masked control makes no edge until it is released and pressed again, and every level after the call equals the model's with the masked controls removed. Random `Axis2D` inputs through `Normalize` never exceed length 1.
- **Allocation tests** (`GC.GetAllocatedBytesForCurrentThread`, background GC off in the project as Core does): 1,000 frames that each carry **every** event kind Input applies, read through each kind's payload, plus `BeginFixedStep`, every view read, a push and a pop, and recording, allocate 0 bytes; also under the Windows dynamic coverage CI runs.
- **Replay determinism:** record a scripted run, replay it on a fresh service, and compare every read the determinism claim lists, in every frame and step, bit for bit; the same for a recording started mid-session, after mouse motion, with the window unfocused, a key (bound by keycode, after a layout change) and a mouse button held, half a wheel notch accumulated and a context pushed; a replaying service created with other options is rejected; a committed version-1 recording fixture keeps replaying identically, so a format change can't slip in.
- **Gamepads** (with PG1-PG4 and I28-I31): synthetic gamepad events through the same buffer cover hot-plug, assignment, focus and stick deadzones; Platform.Sdl3's translation is table-tested over synthetic `SdlEvent`s. A **real gamepad** (connection, button and axis directions, rumble, background events, SDL's id rule) is unverified until a device is available, and is listed as not verified in those PRs.
- **Manual** (release QA): keyboard layouts (AZERTY scancodes, keycode shortcuts), relative mouse mode deltas, focus loss with keys held on Windows and Linux desktops.
- **Architecture:** `BrutalEngine.Input` joins `tests/BrutalEngine.Architecture.Tests` (layering, binding surface, static state).

Mutation self-checks, for example: deliver edges to every step instead of the first; drop the pending carry over zero-step frames; give a masked control's release an edge after `Rebase`; release only from the input window; let modifiers consume; skip the newly-visible mask; skip the trailer hash check.

## 12. Issue split

Every row is counted the same way: source with every XML doc section, in units measured on `main`; one `PublicAPI.Unshipped.txt` line per public type and member; tests about 1:1 with the source (project scaffolding has none of its own).

| Unit | Lines | Measured on `main` |
|------|------:|--------------------|
| E*n*: an enum of *n* members | 11 + 4*n* | `src/BrutalEngine.Platform/MouseButton.cs`: 35 lines for 6 members |
| V: a positional record struct of up to 4 fields | 35 | `src/BrutalEngine.Platform/MouseWheelData.cs`: 35 |
| W: a typed id over one value, with an internal constructor | 25 | `src/BrutalEngine.Platform/WindowId.cs`: 15, plus about 10 for the constructor |
| ID: a slot-and-token id | 105 | `src/BrutalEngine.World/Entity.cs`: 105 |
| D: a data member (property, factory, small method) | 13 | `src/BrutalEngine.Platform/Vfs/VfsPath.cs`: 188 for 14 members (`src/BrutalEngine.Platform/WindowOptions.cs`: 81 for 7 properties) |
| L: a member with an algorithm or I/O behind it | 27 | `src/BrutalEngine.Platform/Vfs/VirtualFileSystem.cs`: 330 for 12 members |
| R: one validation, format or behaviour rule | 26 | `src/BrutalEngine.Platform/Vfs/VfsPathParser.cs`: 340 for the 13 failure kinds of `VfsPathError` |
| T: an internal DTO property | 5 | `src/BrutalEngine.Core/Logging/LogCategoryState.cs`: 21 for 4 members |
| Project scaffolding | 35 | `src/BrutalEngine.World/BrutalEngine.World.csproj`: 11 and `tests/BrutalEngine.World.Tests/BrutalEngine.World.Tests.csproj`: 17, plus `PublicAPI.Shipped.txt`, the solution, the Architecture.Tests reference and CHANGELOG, about 7 |

In the Scope column each term is its lines, then its unit in parentheses; Src is the sum of those lines, and Counted is Src + API + Tests. The Input issues share one project and its `PublicAPI.Unshipped.txt`, so they run one at a time in this order unless noted. Each row lands at about 400-600 counted lines, under the ~800-line PR cap.

| Issue | Blocked by | Scope: lines (unit) | Src | API | Tests | Counted |
|-------|------------|---------------------|----:|----:|------:|--------:|
| I1 feat(input): add the Input project with action ids | this note | project scaffolding 35; `ActionKind` 23 (E3); `ActionId` 105 (ID); `ButtonAction`, `Axis1DAction`, `Axis2DAction` 75 (3 W) | 238 | 13 | 203 | 454 |
| I2 feat(input): add input controls | I1 | `ControlKind` without gamepad members 43 (E8); `AxisComponent` 19 (E2); `InputControl` kinds, codes and factories 143 (11 D) | 205 | 24 | 205 | 434 |
| I3 feat(input): parse and write control text | I2 | `InputControl.TryParse` and `ToString`, the text form 260 (10 R: five prefixes, exact names ignoring case, `Unknown`/`None` rejected, one keycode scalar, case folding, the hex form) | 260 | 2 | 260 | 522 |
| I4 feat(input): add bindings and composites | I3 | `CompositeKind` 27 (E4); `CompositePart` 47 (E9); `CompositeControl` 35 (V); `InputBinding` 65 (5 D); `ProcessorKind` 31 (E5) | 205 | 30 | 205 | 440 |
| I5 feat(input): add processors and map definitions | I4 | `InputProcessor` 91 (7 D); `InputActionDefinition` 91 (7 D); `InputContextDefinition` 52 (4 D); `InputMapDefinition` 26 (2 D) | 260 | 24 | 260 | 544 |
| I6 feat(input): validate input map names and limits | I5 | `InputMap` shell: `Create` 27 (L), `Definition`, `Name`, `ActionCount` 39 (3 D); rules 130 (5 R: name grammar, unique action names, unique context names, 1-64 contexts, 4,096 actions) | 196 | 5 | 196 | 397 |
| I7 feat(input): validate input actions | I6 | rules 208 (8 R: 16 bindings, 4 modifiers, 8 processors per list, `PressThreshold`, `KeyRepeat` on buttons only, what a `Button`, an `Axis1D` and an `Axis2D` take) | 208 | 0 | 208 | 416 |
| I8 feat(input): validate input bindings | I7 | rules 208 (8 R: each part once, button-like `Axis1D`/`Axis2D` parts, `Vector2` parts, `Control` default exactly for composites, control kinds and codes, button-like modifiers, processor kinds, processor arguments) | 208 | 0 | 208 | 416 |
| I9 feat(input): read input maps from JSON | I8 | DTO properties 155 (31 T); the source-generated context 35 (V); `InputMap.FromJson` 27 (L); rules 52 (2 R: versions, 1 MiB) | 269 | 1 | 269 | 539 |
| I10 feat(input): write canonical input-map JSON | I9 | mapping rules 104 (4 R: unknown properties, kind names, part names, processor shapes); `ToJson` 27 (L); canonical-form rules 104 (4 R: property order, defaults omitted, numbers, layout); `ContentHash` 13 (D) | 248 | 2 | 248 | 498 |
| I11 feat(input): add the input service and name lookups | I10 | `InputOptions` without player and gamepad settings 65 (5 D); the constructor, `SetMap`, `GetButton`, `GetAxis1D`, `GetAxis2D`, `TryGetAction`, `GetName` 189 (7 L); `Map` 13 (D) | 267 | 15 | 267 | 549 |
| I12 feat(input): sample keyboard and mouse state | I11 | `BeginFrame` 27 (L); the sampling rows 182 (7 R: key down, key up, mouse buttons, motion, wheel amounts, which window, focus loss); `IsKeyDown` 27 (L); `Mouse` 13 (D); `MouseSnapshot` 35 (V) | 284 | 7 | 284 | 575 |
| I13 feat(input): evaluate button actions in the frame view | I12 | `InputView` 117 (9 D); `InputViewKind` 19 (E2); `Frame`, `FrameCount` 26 (2 D); the control-to-actions index 27 (L); rules 104 (4 R: edges, largest magnitude, `PressThreshold`, modifiers) | 293 | 15 | 293 | 601 |
| I14 feat(input): evaluate composites, processors, repeats and focus | I13 | rules 260 (10 R: three composites, four processors, key repeats, wheel steps, release on focus gain and restore); `MarkBatchesSkipped` 27 (L) | 287 | 1 | 287 | 575 |
| I15 feat(input): latch edges for fixed steps | I14 | `BeginFixedStep` 27 (L); `Fixed`, `FixedStepCount` 26 (2 D); latching rules 78 (3 R); `Rebase` 27 (L); its rules 130 (5 R); plus the reference-model property tests and the allocation test | 288 | 4 | 288 | 580 |
| I16 feat(input): stack input contexts | I15 | `InputContextId` 105 (ID); `GetContext`, `PushContext`, `PopContext` 81 (3 L); `IsContextActive`, `ContextDepth` 26 (2 D); rules 52 (2 R: the initial stack, contexts across `SetMap`) | 264 | 7 | 264 | 535 |
| I17 feat(input): consume controls and enter text | I16 | visibility rules 208 (8 R, section 4); `Text`, `TruncatedTextCount`, `WantsTextInput` 39 (3 D); the `TextInput` row 26 (R) | 273 | 3 | 273 | 549 |
| I18 feat(input): add rebind requests | I17 | `BindingSlot` 35 (V); `RebindRequest` 78 (6 D); `RebindDevices` 23 (E3); `RebindKeyMode` 19 (E2); `RebindState` 27 (E4); `RebindResolution` 23 (E3) | 205 | 27 | 205 | 437 |
| I19 feat(input): listen for rebinds | I18 | `StartRebind` 27 (L); `RebindOperation` 52 (4 D) and `Cancel`, `Apply` 54 (2 L); rules 104 (4 R: actions idle, the held-at-start mask, what completes it, cancel and excluded controls) | 237 | 8 | 237 | 482 |
| I20 feat(input): resolve rebind conflicts and record overrides | I19 | `SetBinding`, `ResetBinding`, `ResetBindings` 81 (3 L); rules 130 (5 R: conflicts, the three resolutions, overrides of defaults, appended slots, `SetMap` cancelling) | 211 | 3 | 211 | 425 |
| I21 feat(input): read and write user bindings | I20 | `BindingOverride` 35 (V); `UserBindings` 80 (2 D, 2 L); `UserBindingsReport` 35 (V); DTO properties 30 (6 T); the source-generated context 35 (V); rules 78 (3 R: versions, unknown properties, limits) | 293 | 13 | 293 | 599 |
| I22 feat(input): load and save user bindings | I21 | `GetUserBindings`, `ApplyUserBindings`, `UserBindingsFile.GetDefaultPath`, `Load`, `Save` 135 (5 L); rules 130 (5 R: skipped entries, indices past the map, re-applying on `SetMap`, failures that never stop the game, warnings) | 265 | 6 | 265 | 536 |
| I23 feat(input): write input recordings | I22 | `InputRecording.ToBytes` 27 (L); `FrameCount`, `MapHash` 26 (2 D); layout rules 130 (5 R: header, start block, frame header, event record, trailer) | 183 | 4 | 183 | 370 |
| I24 feat(input): read input recordings | I23 | `InputRecording.Read` 27 (L); its checks 156 (6 R) | 183 | 1 | 183 | 367 |
| I25 feat(input): record input | I24 | `InputRecorderOptions` 13 (D); `InputRecorder` 39 (3 D) and `Stop`, `ToRecording` 54 (2 L); `StartRecording` 27 (L); rules 130 (5 R: recorded kinds and flags, step counts, the start state, truncation, one recorder) | 263 | 9 | 263 | 535 |
| I26 feat(input): replay input | I25 | `InputReplayer.Create`, `NextFrame` 54 (2 L); `IsFinished`, `FrameIndex` 26 (2 D); `ReplayFrame` 39 (3 D); rules 104 (4 R: hash and options checks, restoring the state, window mapping, the determinism tests and committed fixture) | 223 | 9 | 223 | 455 |
| I27 feat(input): load input maps through the asset database | I26 (same project); #282 merged and the Assets loader contract on `main` | `InputMapLoader` 108 (4 L: the constructor and about three contract members); the `Input -> Assets` edge in `EngineLayers` and [architecture tests](architecture-tests.md#the-layer-map) 3 | 111 | 5 | 108 | 224 |
| PG1 feat(platform): add gamepad ids and enums | the 0.2.0 release (ADR-0016's freeze) | `GamepadId` 25 (W); `GamepadButton` 115 (E26); `GamepadAxis` 35 (E6); `GamepadType` 59 (E12) | 234 | 49 | 234 | 517 |
| PG2 feat(platform): add gamepad event kinds and payloads | PG1 | `GamepadDeviceData`, `GamepadButtonData`, `GamepadAxisData` 105 (3 V); six `PlatformEventKind` members 24; three factories 81 (3 L); three views 39 (3 D) | 249 | 21 | 249 | 519 |
| PG3 feat(platform): open and rumble gamepads | PG2 | `GamepadRumble` 35 (V); `GamepadInfo` 78 (6 D); `IPlatform.Gamepads`, `RumbleGamepad` 54 (2 L); Platform.Sdl3's gamepad init 26 (R) and open and close 27 (L) | 220 | 13 | 220 | 453 |
| PG4 feat(platform): translate gamepad events | PG3 | Platform.Sdl3's translation 27 (L); rules 130 (5 R: device, button and axis events, axis normalisation, background events); tests also mirror the three enums' values against SDL's (+78) | 157 | 0 | 235 | 392 |
| I28 feat(input): bind gamepad controls | I26; PG4 | three `ControlKind` members 12; three factories 39 (3 D); rules 156 (6 R: three text forms, validation, device state, evaluation); `IsGamepadButtonDown` 27 (L) | 234 | 7 | 234 | 475 |
| I29 feat(input): add players | I28 | `Players` 13 (D); `InputPlayer` 65 (5 D); `GetPlayer`, `AssignKeyboardAndMouse` 54 (2 L); rules 78 (3 R: per-player state, the gamepad focus rule, recording gamepad kinds and state) | 210 | 9 | 210 | 429 |
| I30 feat(input): assign and hot-plug gamepads | I29 | `GamepadAssignment` 19 (E2); `GamepadChangeKind` 31 (E5); `GamepadChange` 35 (V); two options 26 (2 D); `AssignGamepad`, `UnassignGamepad` 54 (2 L); `GamepadChanges` 13 (D); rules 78 (3 R: the assignment policy, moving a pad, hot-plug) | 256 | 18 | 256 | 530 |
| I31 feat(input): rumble gamepads | I30; PG3 | `InputOptions.Platform` 13 (D); `Rumble` 27 (L); its failures 26 (R) | 66 | 2 | 66 | 134 |

Rows below 400 are kept where merging two would pass 600: I6 (with I7, 813), I23 and I24 (writing and reading the format, 737 together) and PG4 (with PG3, 845). I27 waits on #282's contract, so it stays apart from the JSON work; I31 is the only part that needs a real device to verify, so it is kept apart too. The rows come to about 16,500 counted lines: Input about 14,600, Platform's gamepad work about 1,900. Each Input issue's CHANGELOG entry goes under `## Next: 0.3.0 (not in the next release)`.

## Consistency with Architecture 3.10 and the platform note

| They say | This note | Why |
|----------|-----------|-----|
| `input.BeginFrame(events)` (docs/03 3.2) | `BeginFrame(events, frameDelta)` plus `BeginFixedStep()` per step | Latching needs the step boundary; the delta is recorded for replay |
| "Latched until consumed by a fixed tick" (3.10) | Every pending edge goes to the next step (section 3) | The pump's timestamps can't place events between steps of one frame |
| Thread checks "asserted in debug builds" (3.2) | Checked in every build | As Platform, World and Runtime |
| Gamepads need a variable-length payload (platform note's consistency table, `docs/design/platform.md:607`) | Fixed payloads; names in the `Gamepads` snapshot | Section 6 |
| Gamepads in M2 (roadmap) | Designed here; code after Platform's gamepad events, after the 0.2.0 release | Decision above |

Nothing here deviates from an ADR.

## Not verified

- The note is design only; nothing in it has run.
- SDL behaviour cited from SDL's documentation rather than observed: gamepad instance ids not being reused, stick directions, background joystick events, relative-mode delta scaling, and which events SDL sends around focus changes, minimising and restoring. Input's own rules (release on focus loss, focus gain and restore, idempotent releases) don't depend on the last one. I12, PG4 and I28 verify what CI can with synthetic events; real-device behaviour needs a gamepad, which the reference machine doesn't have yet.
- Cross-architecture bit equality of replays beyond win-x64 and linux-x64 (macOS is best-effort with no CI, [ADR-0015](../adr/0015-tested-platforms-windows-linux.md)).
- The asset database's loader contract is #282's, not yet written; section 9 depends only on its name.
