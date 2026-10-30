# Runtime verification — 2026-10-30

These are actual application executions consuming locally packed NuGet packages. The mobile applications call only parameterless `PluginHost.Register()`; they do not construct connections or manually register native plugin entry points. MSBuild compiles and links the native implementations from package assets.

| Framework | Runtime tested | Native implementation | Lifecycle result |
|---|---|---|---|
| Avalonia 11.3.10 | Windows 11 ARM64, interactive VM session | C++ / BCrypt | Window close disposes automatic host |
| Uno 6.3.62, Skia | Windows 11 ARM64, interactive VM session | C++ / BCrypt | Window close disposes automatic host |
| Avalonia 11.3.10 | Debian 12 ARM64, Docker + Xvfb | C++ / OpenSSL | Window close disposes automatic host |
| Uno 6.3.62, Skia | Debian 12 ARM64, Docker + Xvfb | C++ / OpenSSL | Window close disposes automatic host |
| Avalonia 11.3.10 | Android 16 ARM64, 16 KB emulator | Kotlin / Java MessageDigest | Root detach disposes pending calls; new root registers and invokes native code |
| Uno 6.3.62, Skia | Android 16 ARM64, 16 KB emulator | Kotlin / Java MessageDigest | Root unload disposes pending calls; new root registers and invokes native code |
| Avalonia 11.3.10 | iOS 26.5 ARM64 simulator | Swift / CryptoKit | Root detach disposes pending calls; new root registers and invokes native code |
| Uno 6.3.62, Skia | iOS 26.5 ARM64 simulator | Swift / CryptoKit | Root unload disposes pending calls; new root registers and invokes native code |

Successful assertions are recorded in [evidence](evidence). These text files contain the PASS lines from local runs; the sample source contains the assertions and can reproduce them. They are not a hosted CI attestation.

The mobile probe checks idempotent no-argument registration, worker-thread SHA256 against .NET's independent SHA256 result, a 20 KB binary echo, native and missing-channel errors, cancellation, pending-call disposal, and a new native session after replacing the root. Desktop probes also exercise explicit-host shutdown/restart and default-host disposal on actual window close.

The Uno Android run found and fixed a real integration defect: Uno Skia chooses the neutral adapter assembly. Android wiring now compiles through the NuGet bootstrap in the consumer, against its chosen renderer, and supplies the platform connection and Activity-destruction lifetime hook. The Uno adapter now observes root unloading as well as window closure. Mobile Uno uses one Window instance; its platform does not allow constructing a second Window, so the restart test replaces that window's root content.

The final Uno SDK is `0.1.0-preview.15`. Windows/Linux and Uno mobile were rerun against it. Avalonia mobile ran against preview.14; its adapter implementation is unchanged in preview.15. No broader compatibility claim is inferred from these runs.

## Other checks

- 45 managed checks passed in the original development checkout, including static access, worker dispatch, scope precedence, codec/events, cancellation and disposal.
- The clean standalone checkout builds all packages without private DotNative source or its CLI. Its independently built Uno Android APK was also installed and run: native calls and automatic root lifecycle restart passed, with no build warnings. APK inspection confirmed no DotNative Rust transport or renderer payload.
- The standalone NuGet native test executes CryptoKit and checks transport/event behavior.
- `dotnet new` creates a plugin in an isolated package cache. Both a direct ProjectReference and its packed NuGet execute its real Swift implementation.
- Package inspection checks transitive bootstrap files, native sources, dependencies, and absence of Rust or renderer payloads.
- MAUI Android/iOS Firebase Remote Config and the DotNative/macOS host were verified in earlier integration runs; those are not newly claimed executions in this matrix. The repository contains the MAUI test app, but users must supply their own test configuration.

## Limits and known warnings

This is a preview, not a claim that every framework/platform/plugin combination works perfectly. Physical iPhones/Android devices, Windows/Linux x64, Uno's native mobile renderer, exhaustive Activity recreation/backgrounding, permission/activity-result callbacks, store signing, and Release/AOT combinations are not established by this matrix. Native view embedding is not included.

Avalonia's Android SkiaSharp 2.88.9 dependency emits an Android 16 KB alignment warning; this emulator run succeeded, but that warning remains relevant to distribution. Avalonia and Uno iOS dependencies emit trimming warnings, including Uno generated binding metadata warnings. The focused Firebase package's stable-numbered local test version also produces NU5104 because it references preview SDK packages. These warnings are not silently treated as production release readiness.

## Reproduce

Build packages using `scripts/pack-standalone-plugins.sh`. Run `scripts/verify-desktop.sh linux-arm64` in a Linux graphical session (or with DISPLAY pointing to Xvfb). Use `osx-arm64` on macOS. On Windows, use a Visual Studio C++ developer environment and build/run the desktop samples with `-r win-arm64 -- --verify`.

For Android, build the mobile project with `-f net10.0-android -r android-arm64 -p:TargetFrameworks=net10.0-android`, install its signed debug APK, and inspect that application's logcat output. Assemblies are embedded so installing the APK does not depend on IDE fast deployment. For iOS, build with `-f net10.0-ios -r iossimulator-arm64 -p:TargetFrameworks=net10.0-ios`, install in a simulator, and capture its console. Both mobile applications run their assertions on load.
