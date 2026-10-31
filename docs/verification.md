# Runtime verification

## Local preview.18 candidate — migration in progress

These results use locally packed NuGet packages, not a published GitHub release.
Avalonia on a physical iPhone 16 Pro running iOS 27.0 passed SecureStorage
Keychain CRUD, Unicode and persistence across host recreation; camera permission
request/check; two real accelerometer samples and subscription disposal;
FilePicker presentation/cancellation; and two host lifecycle cycles.
[Captured assertions](evidence/preview18-local/avalonia-ios27-device.txt).
File selection/read and Camera capture are not covered by this run.

The first physical launch crashed before plugin tests: UIKit rejected the legacy
application lifecycle (`UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption`).
The Avalonia sample now declares a scene delegate and attaches Avalonia's existing
window to its `UIWindowScene`. Its .NET scene delegate implements its own `Window`
property; the binding's base model setter throws. The sample also tracks the source
Info.plist as an incremental manifest input, since the installed iOS SDK tracked
the path but otherwise reused stale manifest contents after edits.
The corrected application completed both runtime cycles on the same phone.

Follow-up physical runs also passed the same four-plugin checks in
[MAUI](evidence/preview18-local/maui-ios27-device.txt) and
[Uno](evidence/preview18-local/uno-ios27-device.txt). Uno required scene adoption
too: its sample obtains the backing UIKit window through Uno's public
`WindowHelper.GetNativeWindow` API, since its root controller is installed later
than `WillConnect`. Uno completed two root lifecycle cycles; MAUI's run covered
one host lifetime.

The same local candidate also passed real document selection and complete native
stream reads of a generated 200704-byte UTF-8 fixture, then disposal and rejection
of reopening the disposed file:

| Host | Actual runtime | Evidence |
|---|---|---|
| MAUI | Physical iPhone, iOS 27.0 | [Assertions](evidence/preview18-local/maui-ios27-file-selection.txt) |
| Avalonia | iOS 26.5 simulator, two host cycles | [Assertions](evidence/preview18-local/avalonia-ios-simulator-selection.txt) |
| Uno | iOS 26.5 simulator, two host cycles | [Assertions](evidence/preview18-local/uno-ios-simulator-selection.txt) |
| MAUI | Android 16 ARM64 emulator | [Assertions](evidence/preview18-local/maui-android-selection.txt) |
| Avalonia | Android 16 ARM64 emulator, two host cycles | [Assertions](evidence/preview18-local/avalonia-android-selection.txt) |
| Uno | Android 16 ARM64 emulator, two host cycles | [Assertions](evidence/preview18-local/uno-android-selection.txt) |

These runs found two cancellation defects. Android now owns external activity
results through a transparent, unexported proxy Activity so cancellation actually
closes the picker; verification checks the host returns to the foreground.
iOS now waits for presentation and dismissal to finish before completing
cancellation, allowing the next picker to open without racing UIKit transitions.
Simulator sensor unavailability is reported as a limitation, not a sensor pass.
The physical runs above are the evidence for real accelerometer delivery.
Windows 11 ARM64 VM runs also passed SecureStorage, native dialog cancellation,
complete fixture reads, stream/file disposal and automatic host disposal on window
close in [Avalonia](evidence/preview18-local/avalonia-windows-selection.txt) and
[Uno](evidence/preview18-local/uno-windows-selection.txt). The Windows dialog now
runs on its own STA thread and receives cancellation through a message window;
it no longer blocks the framework dispatcher. These runs do not establish
Windows implementations of Permissions or Sensors. A subsequent final-package run exposed
an early-cancellation race during shell dialog initialization. The Windows backend
now retries `Close` while the dialog is not ready. Targeted final runs passed native
storage, picker cancellation, subsequent native calls and window-close disposal in
[Avalonia](evidence/preview18-local/avalonia-windows-final.txt) and
[Uno](evidence/preview18-local/uno-windows-final.txt). These final runs did not repeat
file selection; the selection evidence above is from the earlier candidate.

[DotNative Android](evidence/preview18-local/dotnative-android.txt) also passed the
four-plugin check through package references. This exposed a framework-side bug:
managed continuations were only drained by display frames, which pause while a
system picker covers the host. The Android host now wakes its UI queue for managed
work and native replies. Its build also reconciles Maven classes and versions with
.NET Android bindings to avoid duplicate classes.

Linux runs passed SecureStorage through GNOME Secret Service and FilePicker through
`xdg-desktop-portal` with the GTK backend in both
[Avalonia](evidence/preview18-local/avalonia-linux-selection.txt) and
[Uno](evidence/preview18-local/uno-linux-selection.txt). These were real applications
on Debian 12 ARM64 with Xvfb/Openbox; the generated fixture was selected through
noVNC, not a mocked portal. Complete native reads, cancellation and host disposal
passed. The plugin uses GIO on a worker thread and no longer casts a framework
window pointer to `GtkWindow*`. Linux builds require `pkg-config` and GIO development
headers; running requires a session bus, a file chooser portal backend and an
unlocked Secret Service for storage. The build package resolves declared
`DotNativePkgConfig` modules.

The locked-keyring regression initially passed once, then hung on repetition.
Review found that the managed `SecretSchema` declaration omitted native reserved
fields. The backend now uses `secret_schema_newv` to allocate the complete native
schema and `secret_password_free` to release password results. Native calls receive
a `GCancellable`, with token registration disposed before the native handle.
After the fix, six consecutive locked-keyring runs cancelled at approximately two
seconds. The final package also passed Unicode CRUD, update/enumeration/delete and
another cancellation at 2009 ms
([assertions](evidence/preview18-local/linux-storage-cancellation.txt)).
`tests/SecretServiceCancellation` reproduces this only in an isolated Linux
container with its own session bus/login keyring and
`DOTNATIVE_ISOLATED_KEYRING_TEST=1`. Unlock that disposable collection afterward
and run with `cleanup`, or discard the container. Never point it at a user session.

macOS ARM64 runs passed native Keychain CRUD, FilePicker cancellation, complete
fixture reads, stream/file disposal and host disposal in
[Avalonia](evidence/preview18-local/avalonia-macos-selection.txt) and
[Uno](evidence/preview18-local/uno-macos-selection.txt). The picker cancellation
acknowledgement now waits for the native panel completion callback. For UI control,
the test executables were launched through local `.app` wrappers; Uno's wrapper
needed the packaged font folders under the output `Resources` directory. The same
Uno executable launched directly with `dotnet` passed startup without that copy.
This is test-launcher packaging, not additional plugin registration. These runs
cover one desktop host lifetime, not persistence across host recreation.

Desktop Permissions/Sensors, other framework/OS combinations, the remaining
plugins and release distribution are still pending.

## GitHub preview.17 — 2026-10-30

**These executions consumed packages downloaded from the public [GitHub release](https://github.com/kantagara/DotNative.Plugins/releases/tag/v0.1.0-preview.17). Nothing was published to nuget.org.** Each consumer started in a new directory with a separate empty package cache. No consumer referenced the SDK source checkout. DotNative package SHA-256 values in the official MAUI consumer cache were compared with the release manifest and matched.

| Application | Platform actually run | Result |
|---|---|---|
| Official `dotnet new maui` app, plus two `dotnet add package` commands | Android 16 ARM64 emulator | Parameterless registration and worker-thread native Kotlin SHA256 passed |
| Same official MAUI app | iOS 26.5 ARM64 simulator | Parameterless registration and worker-thread native Swift/CryptoKit SHA256 passed |
| Avalonia sample | macOS ARM64, Windows 11 ARM64 VM | Native calls, 20 KB replies, errors/cancellation, automatic host disposal on window close passed |
| Uno Skia sample | macOS ARM64, Windows 11 ARM64 VM | Native calls, 20 KB replies, errors/cancellation, automatic host disposal on window close passed |
| Avalonia sample | Linux x64, GitHub Actions + Xvfb | Native OpenSSL calls and automatic disposal passed |
| Uno Skia sample | Linux x64, GitHub Actions + Xvfb | Native OpenSSL calls and automatic disposal passed |
| Avalonia mobile sample | Android 16 ARM64 emulator, iOS 26.5 ARM64 simulator | Native calls and automatic host disposal/re-registration after root replacement passed |
| Uno mobile sample | Android 16 ARM64 emulator, iOS 26.5 ARM64 simulator | Native calls and automatic host disposal/re-registration after root replacement passed |
| Downloaded `dotnet new` plugin template | macOS ARM64 | Generated plugin executed real Swift through both ProjectReference and packed NuGet |

[Hosted CI run: both jobs succeeded](https://github.com/kantagara/DotNative.Plugins/actions/runs/41074470023). [Captured assertions](evidence/preview17/) contain only PASS lines, with machine-local paths omitted. Local runs are not presented as hosted CI runs.

The monorepo's MAUI/Avalonia/Uno samples now use only automatic registration; desktop and MAUI sample startup no longer creates native connections manually. The basic MAUI sample also ran on Android/iOS against preview.16 without Firebase configuration. The stronger official-template acceptance test above used the final preview.17.

### Regression found by the fresh-project test

Preview.16's native Gradle build imposed API 23 even when the official MAUI template requested API 21. Preview.17 passes the consumer's minimum into Gradle and declares API 21 for the Android adapters. The official application's manifest and generated native Gradle project both retained API 21, and its Android build completed without warnings. No manifest override or manual increase of the app minimum was used. This proves the default project's integration; runtime execution was on Android 16, not an Android API 21 device.

### Scope

Native probe checks use real MessageDigest/CryptoKit/BCrypt/OpenSSL implementations, not mocked transports. The official MAUI fixture verifies startup and a worker-thread native hash; it does not claim full MAUI lifecycle coverage. Mobile Avalonia/Uno verify root replacement and pending-call disposal. Physical devices, exhaustive Activity/background/permission/result handling, Uno's native mobile renderer, and all Release/AOT combinations remain unverified. Firebase network calls were not rerun for this release; the Remote Config package is included but still requires the user's service configuration and upstream SDK requirements.

Avalonia Android still emits its upstream SkiaSharp 16 KB alignment warning. Avalonia/Uno iOS dependencies still emit trimming warnings. Successful emulator/simulator runs do not erase those distribution concerns. MAUI official-template Android and iOS builds completed with zero warnings.

See [reproduction instructions](github-preview.md), including fresh consumer creation and the Android/iOS launch-and-assert scripts.

---

## Earlier development-checkout verification — 2026-10-30

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
