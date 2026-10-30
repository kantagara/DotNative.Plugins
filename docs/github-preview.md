# GitHub preview distribution

Preview `v0.1.0-preview.17` is distributed as GitHub Release assets, **not on nuget.org**. It includes ten `.nupkg` files (the SDK, three adapters, build integration, native probe, Firebase Remote Config, and template) and a SHA-256 manifest.

Prerequisites: Python 3, the GitHub `gh` CLI, .NET 10 and the platform toolchains. Android requires the .NET Android workload, Android SDK and JDK 17; iOS requires macOS, Xcode and the iOS workload. MAUI requires its corresponding workloads. The build does not install those toolchains for you.

```sh
git clone https://github.com/kantagara/DotNative.Plugins.git
cd DotNative.Plugins
python3 scripts/github-preview.py --download
```

The download script verifies every package against the release manifest before copying it into the local feed. It does not build the SDK. Third-party dependencies still restore from nuget.org.

## Truly isolated sample consumers

Each command creates a new directory with only the application's source and a fresh package cache. There are no SDK ProjectReferences, manual native registrations, prebuilt native libraries, or references back to this checkout. The script removes the starter's DotNative package references and executes `dotnet add package` for the adapter and native probe. Build subsequently restores the packages from the downloaded release. Source mapping restricts `DotNative.*` to that feed.

```sh
python3 scripts/github-preview.py --sample Avalonia --output /tmp/preview-avalonia
python3 scripts/github-preview.py --sample Uno --output /tmp/preview-uno
python3 scripts/github-preview.py --sample Maui --output /tmp/preview-maui
```

Use unused paths. Set `DOTNET` when selecting a specific SDK installation. Mobile Avalonia and Uno are available as `--sample AvaloniaMobile` and `--sample UnoMobile`.

On macOS, for example:

```sh
dotnet run --project /tmp/preview-avalonia/Samples/PluginHosts/Avalonia -r osx-arm64 -- --verify
dotnet run --project /tmp/preview-uno/Samples/PluginHosts/Uno -r osx-arm64 -- --verify
dotnet build /tmp/preview-maui/Samples/PluginHosts/Maui -f net10.0-ios -r iossimulator-arm64 -p:TargetFrameworks=net10.0-ios
```

For Android use `-f net10.0-android -r android-arm64 -p:TargetFrameworks=net10.0-android`; install the resulting signed debug APK and inspect its console. For iOS install and launch the `.app` in a simulator. The app starts its native checks when loaded. MAUI runs without Firebase credentials by default; its optional Firebase test still needs your configuration and an explicit package reference.

The sample's first native check calls `PluginHost.Register()` twice to test idempotence, then invokes static plugin APIs. The framework adapter creates the native connection. The probe compares native SHA256 to .NET's independent implementation, verifies a 20 KB reply, native errors and cancellation. Desktop samples verify disposal on close; mobile Avalonia/Uno also replace the root and verify a fresh native session. MAUI's basic sample does not claim to test host recreation.

## New plugin template

```sh
dotnet new install ./artifacts/standalone-plugins/packages/DotNative.Plugins.Templates.0.1.0-preview.17.nupkg
dotnet new dotnative-plugin -n Acme.NativeInfo
```

Configure the downloaded folder as a NuGet source for the generated project, alongside nuget.org. No DotNative CLI is needed. A plain `dotnet add package` without that source will not find these packages on nuget.org.

The GitHub `Verify GitHub preview consumers` workflow runs actual Avalonia and Uno applications under Xvfb on Linux x64 using downloaded release packages and isolated caches. A workflow being present is not proof it passed: consult the linked Actions result and the separate verification evidence.

## Official MAUI template acceptance test

`tests/native/test-maui-new-project.py --packages /path/to/downloaded/feed --output /tmp/new-maui` invokes the official `dotnet new maui`, adds the adapter and probe packages, and adds a Loaded handler with parameterless registration and a worker-thread native SHA256 assertion. It leaves the template's Android minimum API 21 unchanged. Build with `-p:EmbedAssembliesIntoApk=true` when installing the APK directly instead of using IDE fast deployment.

Preview.17 fixes the build SDK's previous hard-coded Android minimum of 23: the generated AAR now uses the application's declared minimum. An upstream plugin SDK may still require a higher minimum; that requirement is not bypassed. A successful build targeting API 21 is not a claim that runtime testing was performed on an API 21 device.

Use `scripts/verify-android.py` and `scripts/verify-ios.py` to install and launch built samples and require a particular PASS marker. They fail on reported errors or timeouts and save the application console for inspection.
