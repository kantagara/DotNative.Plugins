#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:?Pass a runtime identifier such as linux-arm64 or osx-arm64}"
dotnet_command="${DOTNET:-dotnet}"
for framework in Avalonia Uno; do
  "$dotnet_command" build "Samples/PluginHosts/$framework" -r "$rid"
  "$dotnet_command" run --no-build --project "Samples/PluginHosts/$framework" -r "$rid" -- --verify
done
