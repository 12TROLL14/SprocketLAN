#!/bin/bash
# Builds SprocketLAN.dll with Roslyn against the game's own .NET 6 runtime + BepInEx core + interop assemblies (no NuGet).
# Set SPROCKET to your Sprocket install folder (the one containing BepInEx/ and dotnet/).
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
SPROCKET="${SPROCKET:?set SPROCKET to the Sprocket install folder}"
RT="$SPROCKET/dotnet"
CORE="$SPROCKET/BepInEx/core"
INTEROP="$SPROCKET/BepInEx/interop"
CSC="${CSC:-$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1)}"
REFS=""
for f in "$RT"/*.dll; do REFS="$REFS -r:$f"; done
for n in BepInEx.Core BepInEx.Unity.IL2CPP BepInEx.Unity.Common BepInEx.Preloader.Core 0Harmony Il2CppInterop.Runtime Il2CppInterop.Common Il2CppInterop.HarmonySupport; do REFS="$REFS -r:$CORE/$n.dll"; done
for f in "$INTEROP"/*.dll; do REFS="$REFS -r:$f"; done
mkdir -p "$HERE/bin"
dotnet "$CSC" -nologo -noconfig -nostdlib+ -target:library -langversion:10 -nullable:disable -optimize+ -unsafe+ -deterministic \
  -nowarn:CS0618,CS1701,CS1702,CS0436,CS8981 -out:"$HERE/bin/SprocketLAN.dll" $REFS $(find "$HERE/src" -name '*.cs')
echo BUILD OK
