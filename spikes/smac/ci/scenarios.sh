#!/usr/bin/env bash
# Spike S1: run VkSmoke against the bundled loader + MoltenVK in several configurations.
# Env: APP, APP_PORTABLE, WORK, RESULTS, optional VVL_DIR. Mandatory scenarios fail the script.
set -uo pipefail
mkdir -p "$WORK/logs"
fail=0
ICD="$APP/MoltenVK_icd.json"

# Env vars are exported in the subshell rather than passed through /usr/bin/env: env is SIP-protected,
# so DYLD_* variables would be stripped before dotnet starts.
run() { # name mandatory(yes/no) "VAR=val VAR2=val" dotnet-args...
  local name=$1 mand=$2 envs=$3; shift 3
  local t=$SECONDS
  ( cd "$RUNNER_TEMP" && for kv in $envs; do export "$kv"; done && dotnet "$@" ) > "$WORK/logs/$name.log" 2>&1
  local rc=$?
  echo "::group::$name (exit $rc)"; cat "$WORK/logs/$name.log"; echo "::endgroup::"
  local loaded dev px
  loaded=$(grep -m1 'Vulkan loader loaded from' "$WORK/logs/$name.log" | sed 's/.*from: //')
  dev=$(grep -m1 '  Device: ' "$WORK/logs/$name.log" | sed 's/.*Device: //')
  px=$(grep -m1 '^Readback' "$WORK/logs/$name.log" | sed 's/.*; //')
  [ -z "$loaded" ] && loaded=$(grep -m1 -o 'DllNotFoundException.*' "$WORK/logs/$name.log" | cut -c1-60)
  echo "| $name | $mand | $rc | ${loaded:--} | ${dev:--} | ${px:--} | $((SECONDS - t)) s |" >> "$RESULTS"
  if [ "$mand" = yes ] && [ $rc -ne 0 ]; then fail=1; fi
}

{ echo; echo "| scenario | mandatory | exit | loader loaded from | device | readback | time |"; echo "|---|---|---|---|---|---|---|"; } >> "$RESULTS"

# --- Loader resolution through BRUTAL's ImportResolver (bare-name NativeLibrary.TryLoad) ---
# R1. RID-specific publish, libvulkan.1.dylib beside the app (app dir should be a NATIVE_DLL_SEARCH_DIRECTORIES entry).
run rid-appdir yes "VK_DRIVER_FILES=$ICD" "$APP/VkSmoke.dll"
# R2. Portable build, libvulkan.1.dylib beside the app (failed in run 1: app dir is not searched).
run portable-appdir no "VK_DRIVER_FILES=$ICD" "$APP_PORTABLE/VkSmoke.dll"
# R3. Portable build, libvulkan.1.dylib in runtimes/osx-arm64/native/ (where a NuGet native asset would land).
rm -rf "$WORK/app-rtnative"; cp -R "$APP_PORTABLE" "$WORK/app-rtnative"
mv "$WORK/app-rtnative/libvulkan.1.dylib" "$WORK/app-rtnative/runtimes/osx-arm64/native/"
run portable-runtimesnative yes "VK_DRIVER_FILES=$ICD" "$WORK/app-rtnative/VkSmoke.dll"
# R4. Portable build, loader preloaded by absolute path before BRUTAL's static ctor.
run portable-preload no "VK_DRIVER_FILES=$ICD" "$APP_PORTABLE/VkSmoke.dll" --preload "$APP_PORTABLE/libvulkan.1.dylib"
# R5. Portable build, loader only in Frameworks/ reachable through DYLD_LIBRARY_PATH.
rm -rf "$WORK/app-sub"; cp -R "$APP_PORTABLE" "$WORK/app-sub"; mkdir -p "$WORK/app-sub/Frameworks"
mv "$WORK/app-sub/libvulkan.1.dylib" "$WORK/app-sub/libMoltenVK.dylib" "$WORK/app-sub/MoltenVK_icd.json" "$WORK/app-sub/Frameworks/"
run portable-frameworks-dyld no "DYLD_LIBRARY_PATH=$WORK/app-sub/Frameworks VK_DRIVER_FILES=$WORK/app-sub/Frameworks/MoltenVK_icd.json" "$WORK/app-sub/VkSmoke.dll"
# R6. RID publish without libvulkan.1.dylib: what does the resolver fall back to (VulkanEx links @rpath/libvulkan.1.dylib)?
rm -rf "$WORK/app-noloader"; cp -R "$APP" "$WORK/app-noloader"; rm "$WORK/app-noloader/libvulkan.1.dylib"
run rid-noloader no "VK_DRIVER_FILES=$ICD" "$WORK/app-noloader/VkSmoke.dll"

# --- Driver (ICD) discovery by the bundled loader, using the R1 layout ---
# D1. App sets VK_DRIVER_FILES itself via native setenv (no launcher script).
run rid-selfsetenv yes "" "$APP/VkSmoke.dll" --set-driver-env
# D2. VK_ADD_DRIVER_FILES.
run rid-adddriverfiles yes "VK_ADD_DRIVER_FILES=$ICD" "$APP/VkSmoke.dll"
# D3. No driver env at all (expected: no MoltenVK, nothing installed system-wide).
run rid-nodriverenv no "" "$APP/VkSmoke.dll"
# D4. Loader debug log for R1.
run rid-loaderdebug no "VK_LOADER_DEBUG=error,warn,info,driver VK_DRIVER_FILES=$ICD" "$APP/VkSmoke.dll"

if [ -n "${VVL_DIR:-}" ]; then
  # V1. Validation layer from a bundled folder via VK_ADD_LAYER_PATH; the canary error must be caught.
  run rid-validation yes "VK_ADD_LAYER_PATH=$VVL_DIR VK_DRIVER_FILES=$ICD" "$APP/VkSmoke.dll" --require-validation
fi

echo >> "$RESULTS"
exit $fail
