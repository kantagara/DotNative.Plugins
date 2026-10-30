# DotNative.Plugins

Native service plugins for .NET applications. Write Kotlin for Android, Swift for Apple platforms, and C++ for Windows/Linux. Expose one C# API and use it from MAUI, Avalonia, Uno, or DotNative.

**Preview source release. Packages are built locally; the commands below do not assume a public NuGet release.** Native view embedding is outside the current scope. Firebase Remote Config is one implemented plugin, not the entire Firebase SDK.

## Application code

Reference the plugin package and the adapter for your framework. Once the application's window/root view is ready, on its UI thread:

```csharp
using DotNative.Plugins;

PluginHost.Register(); // No arguments or DI container.
var digest = await StandalonePlugins.Probe.HashAsync(bytes);
```

The probe is a verification plugin that calls CryptoKit, Java MessageDigest, BCrypt, or OpenSSL. Real application plugins expose their own static C# facade.

MAUI uses a page's `Loaded` event, Avalonia uses an opened window or attached root view, and Uno uses the active window. Registration is idempotent for the active host. Adapters own native transport creation and teardown. Register again after replacing the owning window/root view. Background workers may call static plugin methods after registration. Registering multiple frameworks in one process is rejected rather than guessed.

## Create a plugin without the DotNative CLI

After building the local packages:

```sh
dotnet new install ./artifacts/standalone-plugins/packages/DotNative.Plugins.Templates.0.1.0-preview.15.nupkg
dotnet new dotnative-plugin -n Acme.NativeInfo
```

The template contains the C# API, Kotlin/Swift/C++ implementations, native dependency declarations, NuGet build assets, and consumer examples. The template source lives in `templates/DotNative.Plugin`; it has no dependency on the DotNative CLI.

Consumers can use a direct `ProjectReference` during development or the packed NuGet. The generated README explains both paths. IntelliSense is the normal C# API surface; native sources use their language tooling.

## Native dependencies

`DotNative.Plugins.Build` imports transitive MSBuild targets. Android sources and Maven dependencies are assembled with Gradle and merged into the .NET Android application. Apple sources and pinned SDK archive manifests are compiled/linked into the application. Windows/Linux compile the C++ backend with the platform toolchain. Applications still need the appropriate platform SDKs and any service configuration, permissions, entitlements, or signing required by that plugin.

There is no Rust toolchain, Rust source, or DotNative renderer dependency in this repository. Android uses the .NET Android bridge to Kotlin; Apple and desktop use a small native ABI.

## Build from source

The complete package build currently runs on macOS with .NET 10, the Android/iOS/Mac Catalyst workloads, Xcode, Android SDK, and JDK 17 installed:

```sh
./scripts/pack-standalone-plugins.sh
python3 tests/native/test-standalone-packaging.py
python3 tests/native/test-plugin-scaffold.py --template
```

Set `DOTNET` to a specific SDK executable when needed. Native consumers use only NuGet packages from the generated local feed. Each sample defines its feed/cache relative to this checkout. Desktop consumers additionally need the relevant C++ compiler; Linux's SHA256 probe needs OpenSSL development headers.

## Examples and verification

- `Samples/PluginHosts/Avalonia` and `Uno`: actual desktop applications with `--verify` mode.
- `Samples/PluginHosts/AvaloniaMobile` and `UnoMobile`: Android/iOS applications.
- `Samples/PluginHosts/Maui`: native probe plus Firebase Remote Config. Supply your own local options file with `-p:FirebaseTestConfig=/path/to/firebase-test.json`; no credentials are committed.
- `plugins/DotNative.Firebase.RemoteConfig`: focused Android/Apple Firebase implementation.
- `tests/StandalonePlugins.NativeProbe`: native transport verification plugin.

See [verification results](docs/verification.md) for the precise platform, architecture, runtime checks, and remaining gaps. Passing one architecture does not establish support for every architecture or every plugin.

DotNative's UI-framework adapter lives in the DotNative host itself; this repository provides its shared plugin contracts. No private UI framework source or renderer is required by the other adapters.

## License

MIT. Third-party SDKs retain their respective licenses; see `THIRD_PARTY_NOTICES`.
