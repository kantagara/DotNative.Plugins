#!/bin/bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"
dotnet_command="${DOTNET:-dotnet}"
for project in src/DotNative.Plugins.Core src/DotNative.Plugins.Native \
  src/DotNative.Plugins.Maui src/DotNative.Plugins.Avalonia src/DotNative.Plugins.Uno \
  src/DotNative.Plugins.Android packaging/DotNative.Plugins.Build tests/StandalonePlugins.NativeProbe plugins/DotNative.Firebase.RemoteConfig templates/DotNative.Plugins.Templates.csproj; do
  "$dotnet_command" pack "$project" -c Release -o artifacts/standalone-plugins/packages -p:RestoreAdditionalProjectSources="$repo/artifacts/standalone-plugins/packages"
 done
