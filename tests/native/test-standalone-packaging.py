#!/usr/bin/env python3
"""Inspect packed artifacts without launching a UI, simulator, or native process."""
from pathlib import Path
from zipfile import ZipFile
import argparse
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument("packages", nargs="?", type=Path, default=root / "artifacts/standalone-plugins/packages")
parser.add_argument("--android-apk", type=Path)
parser.add_argument("--apple-library", type=Path)
args = parser.parse_args()
packages = args.packages
version = "0.1.0-preview.18"


def archive(name):
    package_version = "0.1.0-preview.18" if name == "DotNative.Plugins.Build" else version
    return ZipFile(packages / f"{name}.{package_version}.nupkg")


def dependencies(name):
    with archive(name) as package:
        manifest = next(n for n in package.namelist() if n.endswith(".nuspec"))
        tree = ET.fromstring(package.read(manifest))
        return {node.attrib["id"] for node in tree.iter() if node.tag.endswith("}dependency")}


assert dependencies("DotNative.Plugins.Core") == set(), "Core acquired a framework dependency"
for host in ("Maui", "Avalonia", "Uno", "Android", "Native"):
    assert "DotNative" not in dependencies(f"DotNative.Plugins.{host}"), host
assert {"DotNative.Plugins.Core", "DotNative.Plugins.Native", "DotNative.Plugins.Build"} <= dependencies("DotNative.Plugins.NativeProbe")
with archive("DotNative.Plugins.Build") as package:
    names = set(package.namelist())
    for path in (
        "tools/Build/DotNative.Plugins.Build.dll",
        "tools/NativeBuild/DotNative.NativeBuild.dll",
        "tools/AndroidProject/gradlew",
        "tools/AndroidProject/gradle/wrapper/gradle-wrapper.jar",
        "tools/PluginSdk/apple/PluginChannels.swift",
        "tools/PluginSdk/android/PluginChannels.kt",
        "tools/PluginSdk/android/PluginResultActivity.kt",
        "tools/PluginSdk/android/PluginResultFragment.kt",
        "tools/PluginSdk/apple/PluginTransport.swift",
        "tools/PluginSdk/cpp/PluginTransport.cpp",
    ):
        assert path in names, f"Missing package input: {path}"
    assert "tools/PluginSdk/apple/PlatformViews.swift" not in names
    assert not any("runtimes/" in n for n in names), "Plugins must not package a Rust runtime"
    assert not any(n.endswith((".rs", ".a", ".so", ".lib")) or "Cargo" in n for n in names), "Unexpected native runtime payload"
    kotlin = package.read("tools/PluginSdk/android/PluginChannels.kt").decode()
    assert "external fun registerNative" not in kotlin
    assert "external fun nativeReply" not in kotlin
# Android now shares the automatic platform-factory registry in the managed
# Native package. That dependency must not introduce native/Rust payloads.
assert "DotNative.Plugins.Native" in dependencies("DotNative.Plugins.Android")
with archive("DotNative.Plugins.Native") as package:
    assert not any(n.endswith((".rs", ".so", ".a", ".dylib")) or "Cargo" in n for n in package.namelist())
assert not (root / "native/core/src/plugins.rs").exists()
assert not (root / "native/plugin-runtime/Cargo.toml").exists()
print("PASS standalone NuGet dependencies, tools, platform transport sources, no Rust payload and renderer exclusion")

if args.android_apk:
    with ZipFile(args.android_apk) as apk:
        assert not any(n.endswith(("/libdotnative_plugins.so", "/libcore.so", "/libdotnative_plugin_bridge.so")) for n in apk.namelist())
    print("PASS standalone Android APK has no DotNative Rust transport or renderer")
if args.apple_library:
    symbols = subprocess.check_output(["nm", str(args.apple_library)], text=True)
    assert "_dotnative_send_plugin" in symbols
    assert "rust_eh_personality" not in symbols and "__rust_alloc" not in symbols
    print("PASS Apple transport exports present without Rust runtime symbols")

with archive("DotNative.Plugins.Templates") as package:
    files = package.namelist()
    assert "content/DotNative.Plugin/.template.config/template.json" in files
    assert "content/DotNative.Plugin/example-maui/App.cs" in files
    assert not any("DotNative.Cli" in name or name.endswith((".rs", "Cargo.toml", "dotnative.local.props")) for name in files)
print("PASS dotnet-new template package: native sources, examples, no CLI or Rust dependency")

for adapter in ("Maui", "Avalonia", "Uno"):
    with archive("DotNative.Plugins." + adapter) as package:
        assert f"buildTransitive/DotNative.Plugins.{adapter}.targets" in package.namelist()
        assert f"buildTransitive/{adapter}PluginBootstrap.cs" in package.namelist()
print("PASS automatic adapters: packaged bootstrap targets and source")
