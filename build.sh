#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
MSBUILD="C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe"

if [ ! -f "$MSBUILD" ]; then
    echo "MSBuild not found: $MSBUILD" >&2
    exit 1
fi

"$MSBUILD" "$ROOT/CliTray.csproj" -t:Rebuild -p:Configuration=Release -nologo -v:minimal

echo "built: $ROOT/bin/Release/CliTray.exe"
