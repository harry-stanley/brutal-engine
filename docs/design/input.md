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

| Event (`PlatformEventKind`, `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:252-276`) | Effect |
|-------|--------|
| `KeyDown`, not a repeat | From the input window and while focused: the scancode goes down, and the keycode reported now is stored with it |
| `KeyDown` with `IsRepeat` (`KeyEventData.IsRepeat`, `:102`) | No state change. For a key that is down, every `KeyRepeat` button bound to it gets one extra press edge (menu navigation). Otherwise ignored |
| `KeyUp` | From **any** window: the scancode goes up, and so does the keycode stored at its press (not the one in this event, which may differ after a layout switch) |
| `MouseButtonDown`, `MouseButtonUp` | Like keys: presses from the input window while focused, releases from any window (`MouseButtonData.Button`, `:163`) |
| `MouseMotion` | From the input window: position set to `X`, `Y`; `DeltaX`, `DeltaY` added to both views' delta accumulators (`:186-189`) |
| `MouseWheel` | From the input window: `X`, `Y` added to both views' wheel accumulators (`:206-208`); each whole notch crossed by a per-direction accumulator is one press and one release edge of the matching `MouseWheelStep` |
| `TextInput` | From the input window, when the top context is `TextEntry`: its bytes (`GetText`) are appended to `Text`; past `TextCapacityBytes` the rest of the frame's text is dropped at a code-point boundary and `TruncatedTextCount` grows |
| `WindowFocusLost` (`:271`) | For the input window: every held key and mouse button is released, as edges at this position in the order; then presses and motion are ignored until `WindowFocusGained` (`:270`) |
| `WindowMouseLeft`, `WindowMouseEntered` | `Mouse.IsInWindow` |
| Gamepad kinds | Section 6 |
| Every other kind | Ignored |

- **Which window.** With `InputOptions.Window` default, every window is the input window. Otherwise presses, motion and wheel count only from that window or from events with a default `WindowId`, which SDL reports when it knows no window (`docs/design/platform.md:239`). Releases always count, so a key pressed in the game window and released over a tool window never sticks.
- **Focus.** A new service assumes it is focused. Releasing on focus loss is Input's own rule, whatever SDL sends: a release of a control that is already up does nothing, so duplicates are harmless.
- **Relative mouse mode** (`docs/design/platform.md:170`) needs nothing from Input: motion events carry deltas in both modes, so `MouseDelta` bindings work either way, and `MousePosition` keeps the last reported position, which means little while the cursor is hidden. Positions and deltas are window coordinates; converting to pixels or content units is the platform note's formula (`docs/design/platform.md:243`). How SDL scales relative deltas is SDL's documentation, not observed here (section 11).
- **Text input** itself is switched on by the host: `WantsTextInput` is true while the top context is `TextEntry`, and the host calls `IWindow.StartTextInput`/`StopTextInput` when it differs from `IWindow.IsTextInputActive` (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:95`, `:97`, `:85`; section 8).

## 3. Fixed-step latching

Two views read the same action state over different intervals:

- **`Frame`**, for `Update` systems: the interval is the last `BeginFrame`'s batch. Its edges and counts are the batch's; its levels are the state after the batch; its deltas are the batch's sums.
- **`Fixed`**, for `FixedUpdate` systems: the interval is from the previous `BeginFixedStep` (or the service's creation or last `Rebase`) to this one. **Consumed by a fixed tick** means: `BeginFixedStep` moves every pending edge count and delta sum into the step's view and zeroes the pending ones, so each edge and each delta reaches exactly one fixed step.

Exactly, per button action: `BeginFrame` adds the batch's press and release edges to `pendingPresses` and `pendingReleases` (and the batch's mouse deltas and wheel amounts to the pending sums). `BeginFixedStep` sets the step's `PressCount = pendingPresses`, `WasPressed = pendingPresses > 0`, `WasReleased = pendingReleases > 0`, `IsPressed` = the level after the last batch, deltas = the pending sums, then zeroes the pending values. Axis levels (keys, sticks, positions) are the values after the last batch. Counters saturate at `int.MaxValue`.

| Case | `Frame` view | `Fixed` view |
|------|-------------|--------------|
| Press and release inside one frame, then one step | `WasPressed`, `WasReleased`, not `IsPressed`, count 1 | The same in that step |
| Press, then several steps in the frame | `WasPressed`, `IsPressed` | First step: `WasPressed`, `IsPressed`. Later steps: `IsPressed` only |
| Press and release in a frame with **zero** steps | As in the first row | Nothing yet: the edges stay pending, and the next frame's first step sees them (plus that frame's own) |
| Two frames, each a tap, with no step between | count 1 in each frame | The next step: count 2, not `IsPressed` |
| Held across frames | `IsPressed`; no edges after the first frame | `IsPressed`; `WasPressed` only in the step that consumed the press |
| Release then press again in one frame | `WasReleased`, `WasPressed`, `IsPressed` | The same in the step that consumes them |
| Mouse delta over a frame with zero steps | That frame's sum | Added to the next step's delta, never lost, never doubled |

**Why all edges go to the first step.** Within a frame, events are known only at its start, and their timestamps are the pump's translation time (`docs/design/platform.md:244`), not the moment the player acted, so they can't place an event between two steps of the same frame. Delivering every edge to the first step is the only rule that loses nothing and doubles nothing. "Pressed this step" is `WasPressed`; "held this step" is `IsPressed`; a gameplay jump reads `WasPressed` and a held charge reads `IsPressed`.

**`Rebase()`** zeroes every pending edge and delta and masks every control that is held now until it is released (as a context push does, section 4), so nothing that happened before the call reaches gameplay: the splash, a loading screen, the start of a recording (section 7). The frame view is untouched.

Misuse: `BeginFixedStep` before the first `BeginFrame` is allowed (it reports nothing); views have no other state machine to break.
