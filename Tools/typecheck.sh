#!/usr/bin/env bash
# Typecheck Dropshock's runtime scripts (the Assembly-CSharp gameplay code)
# outside Unity, using Roslyn.
#
# Usage:   ./typecheck.sh [langversion]      e.g. ./typecheck.sh 9.0   (default)
#                                                 ./typecheck.sh 8.0
#
# READ THIS BEFORE TRUSTING THE OUTPUT
# ------------------------------------
# This is a *partial* typecheck, not a Unity build. Three known limits:
#
#  1. Unity reference assemblies are 2021.3.33 (from NuGet), NOT the project's
#     6000.5.6f1. Unity's real managed DLLs only exist inside the editor install,
#     which is on the Windows machine (see the HintPaths in Assembly-CSharp.csproj).
#     APIs added after 2021.3 therefore appear "missing". Those specific errors are
#     filtered below and reported separately — nothing is silently swallowed.
#
#  2. Assets/Editor/ is NOT compiled. It needs UnityEditor.dll, which is not
#     redistributable and is not on NuGet in any form.
#
#  3. Unity's own codegen (Fusion's ILWeaver, [Networked] property rewriting) does
#     not run. This checks C# validity, not Fusion weaving correctness.
#
# A clean run here means "the C# is well-formed". It does not mean the project
# builds in Unity. It is a fast pre-check, not a substitute for the editor.

set -uo pipefail

LANGVER="${1:-9.0}"
# Repo root: two levels up from this script (Tools/typecheck.sh), or override.
PROJ="${DROPSHOCK_PROJ:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
# Where the Unity reference assemblies were unpacked (see setup notes below).
TOOLS="${DROPSHOCK_TOOLS:-$HOME/tools}"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# The container's localtime is unresolvable; without this every dotnet
# invocation dies in TimeZoneInfo's static constructor.
export TZ=UTC
# No libicu in this image, and no root to install it. Without this csc FailFasts
# in GlobalizationMode's static constructor before parsing a single file — it
# writes nothing to stdout that looks like a compiler error, so a script that
# only greps for ': error' will report success on a compile that never ran.
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

CSC="$(ls -d "$HOME"/.dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1)"
NETSTD="$HOME/.dotnet/packs/NETStandard.Library.Ref/2.1.0/ref/netstandard2.1"

REFS=()
for d in "$NETSTD"/*.dll; do REFS+=("-r:$d"); done
for d in "$TOOLS"/refs/*.dll; do REFS+=("-r:$d"); done
# Every managed DLL shipped under Assets/ — Fusion, Photon Realtime, and the
# SimpleKCC addon, which is a separate download from the Fusion SDK and lives in
# its own folder (see ARCHITECTURE.md, manual setup step 6).
#
# Plugins/ is excluded because it holds native binaries (NanoSockets, per-platform
# .so/.dll). Handing csc a native PE is a hard CS0009 that aborts the whole
# compile, so this filter is load-bearing, not tidiness.
while IFS= read -r d; do REFS+=("-r:$d"); done < <(
  find "$PROJ/Assets" -name '*.dll' \
    ! -name '*.Editor.dll' \
    ! -path '*/Plugins/*' 2>/dev/null
)
# Input System, taken from Unity's own build artifacts in Library/.
IS="$(find "$PROJ/Library" -name 'Unity.InputSystem.dll' 2>/dev/null | head -1)"
[ -n "$IS" ] && REFS+=("-r:$IS")

# Package assemblies Unity compiled from source rather than shipping as DLLs —
# Fusion.Unity is the one that matters (NetworkSceneManagerDefault, NetworkRunner
# integration). Assembly-CSharp itself is excluded: it is this compile's own
# output, and referencing it would duplicate every type we are checking.
while IFS= read -r d; do REFS+=("-r:$d"); done < <(
  find "$PROJ/Library/ScriptAssemblies" -name '*.dll' \
    ! -name 'Assembly-CSharp*' \
    ! -name '*Editor*' 2>/dev/null
)

mapfile -t CORE_SRC < <(find "$PROJ/Assets/Scripts/Core" -name '*.cs')
mapfile -t NET_SRC  < <(find "$PROJ/Assets/Scripts/Net"  -name '*.cs')

echo "langversion : $LANGVER"
echo "assemblies  : Dropshock.Core (${#CORE_SRC[@]} files) -> Dropshock.Net (${#NET_SRC[@]} files)"
echo "references  : ${#REFS[@]}"
echo

# Compiled as two assemblies, in dependency order, rather than one flat pass.
# That is the only thing here that actually validates the asmdef split: Core is
# built with NO reference to Net, so any Core -> Net dependency is a hard error
# rather than something Unity discovers later.
RAW="$(dotnet "$CSC" \
  -nologo -target:library -nostdlib+ -noconfig \
  -langversion:"$LANGVER" \
  -define:UNITY_EDITOR\;UNITY_2021_1_OR_NEWER\;FUSION_ENABLE_MPPM \
  -out:"$TOOLS/Dropshock.Core.dll" \
  "${REFS[@]}" "${CORE_SRC[@]}" 2>&1)"
CSC_EXIT=$?

if [ "$CSC_EXIT" -eq 0 ] || echo "$RAW" | grep -qE ': error '; then
  RAW="$RAW
$(dotnet "$CSC" \
  -nologo -target:library -nostdlib+ -noconfig \
  -langversion:"$LANGVER" \
  -define:UNITY_EDITOR\;UNITY_2021_1_OR_NEWER\;FUSION_ENABLE_MPPM \
  -out:"$TOOLS/Dropshock.Net.dll" \
  -r:"$TOOLS/Dropshock.Core.dll" \
  "${REFS[@]}" "${NET_SRC[@]}" 2>&1)"
  [ $? -ne 0 ] && CSC_EXIT=1
fi

# Trust the exit code, never the absence of matching text. csc can die before it
# parses anything (missing ICU, bad response file, unreadable ref) and emit
# nothing that looks like a compiler diagnostic. Greping alone reports OK.
if [ "$CSC_EXIT" -ne 0 ] && ! echo "$RAW" | grep -qE ': error '; then
  echo "--- COMPILER FAILED TO RUN (exit $CSC_EXIT) ---"
  echo "$RAW" | head -20 | sed 's/^/  /'
  echo
  echo "FAIL: this is a toolchain fault, not a code fault."
  exit 2
fi

# Errors caused purely by the reference assemblies predating Unity 6.
STALE='FindFirstObjectByType|FindObjectsByType|FindObjectsSortMode'

REAL="$(echo "$RAW"   | grep -E ': error ' | grep -Ev "$STALE")"
KNOWN="$(echo "$RAW"  | grep -E ': error ' | grep -E  "$STALE")"

if [ -n "$KNOWN" ]; then
  echo "--- suppressed: Unity 6 APIs absent from 2021.3 reference assemblies ---"
  echo "$KNOWN" | sed 's/^/  /'
  echo
fi

if [ -n "$REAL" ]; then
  echo "--- REAL ERRORS ---"
  echo "$REAL" | sed 's/^/  /'
  echo
  echo "FAIL: $(echo "$REAL" | wc -l) error(s)"
  exit 1
fi

echo "OK: no real errors at C# $LANGVER"
