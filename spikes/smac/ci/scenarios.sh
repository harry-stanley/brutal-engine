#!/usr/bin/env bash
# Spike S1: run VkSmoke against the bundled loader + MoltenVK in several configurations.
# Env: APP, WORK, RESULTS. Mandatory scenarios fail the script; diagnostic ones are only recorded.
set -uo pipefail
mkdir -p "$WORK/logs"
fail=0

# Env vars are exported in the subshell rather than passed through /usr/bin/env: env is SIP-protected,
# so DYLD_* variables would be stripped before dotnet starts.
run() { # name mandatory(yes/no) "VAR=val VAR2=val" dotnet-args...
  local name=$1 mand=$2 envs=$3; shift 3
  local t=$SECONDS
  ( cd "$RUNNER_TEMP" && for kv in $envs; do export "$kv"; done && dotnet "$@" ) > "$WORK/logs/$name.log" 2>&1
  local rc=$?
  echo "::group::$name (exit $rc)"; cat "$WORK/logs/$name.log"; echo "::endgroup::"
  local loaded dev
  loaded=$(grep -m1 'Vulkan loader loaded from' "$WORK/logs/$name.log" | sed 's/.*from: //')
  dev=$(grep -m1 '  Device: ' "$WORK/logs/$name.log" | sed 's/.*Device: //')
  echo "| $name | $mand | $rc | ${loaded:--} | ${dev:--} | $((SECONDS - t)) s |" >> "$RESULTS"
  if [ "$mand" = yes ] && [ $rc -ne 0 ]; then fail=1; fi
}

{ echo; echo "| scenario | mandatory | exit | loader loaded from | device | time |"; echo "|---|---|---|---|---|---|"; } >> "$RESULTS"

# 1. Bundled loader found next to the app (cwd elsewhere), MoltenVK via VK_DRIVER_FILES.
run bundled-driverfiles yes "VK_DRIVER_FILES=$APP/MoltenVK_icd.json" "$APP/VkSmoke.dll"
# 2. Same, but the app sets VK_DRIVER_FILES itself via native setenv (no launcher script / env).
run bundled-selfsetenv yes "" "$APP/VkSmoke.dll" --set-driver-env
# 3. Same via VK_ADD_DRIVER_FILES.
run bundled-adddriverfiles yes "VK_ADD_DRIVER_FILES=$APP/MoltenVK_icd.json" "$APP/VkSmoke.dll"
# 4. Diagnostic: no driver env at all -> expect no MoltenVK found (nothing installed system-wide).
run bundled-nodriverenv no "" "$APP/VkSmoke.dll"
# 5. Diagnostic: loader debug output for scenario 1.
run bundled-loaderdebug no "VK_LOADER_DEBUG=error,warn,info,driver VK_DRIVER_FILES=$APP/MoltenVK_icd.json" "$APP/VkSmoke.dll"
# 6. Diagnostic: app dir WITHOUT libvulkan.1.dylib -> what does BRUTAL's resolver fall back to?
rm -rf "$WORK/app-noloader"; cp -R "$APP" "$WORK/app-noloader"; rm "$WORK/app-noloader/libvulkan.1.dylib"
run noloader-fallback no "VK_DRIVER_FILES=$APP/MoltenVK_icd.json" "$WORK/app-noloader/VkSmoke.dll"
# 7. Diagnostic: loader in a sub-folder only reachable through DYLD_LIBRARY_PATH (not app dir).
rm -rf "$WORK/app-sub"; cp -R "$WORK/app-noloader" "$WORK/app-sub"; mkdir -p "$WORK/app-sub/Frameworks"
cp "$APP/libvulkan.1.dylib" "$APP/libMoltenVK.dylib" "$WORK/app-sub/Frameworks/"
run frameworks-dyld no "DYLD_LIBRARY_PATH=$WORK/app-sub/Frameworks VK_DRIVER_FILES=$APP/MoltenVK_icd.json" "$WORK/app-sub/VkSmoke.dll"

if [ -n "${VVL_DIR:-}" ]; then
  # 8. Validation layers from a bundled folder via VK_ADD_LAYER_PATH, canary must be caught.
  run validation yes "VK_ADD_LAYER_PATH=$VVL_DIR VK_DRIVER_FILES=$APP/MoltenVK_icd.json" "$APP/VkSmoke.dll" --require-validation
fi

echo >> "$RESULTS"
exit $fail
