# PluginTemplate

A native service plugin with a static C# API. No Rust and no UI framework dependency.

- `NativeApi.ReadAsync()` calls Kotlin on Android, Swift on Apple, and C++ on Windows/Linux.
- `NativeService` is an optional instance API for applications that prefer DI.
- `Platform/` contains native implementations.
- `buildTransitive/` exports native files and dependencies for NuGet and direct ProjectReference consumers.
- `example-maui/` connects a MAUI Android/iOS host once and calls the static API.
- `example-standalone/` runs the native code in a desktop console application.

Configure a NuGet source containing the SDK preview packages before building.
Download preview packages from https://github.com/kantagara/DotNative.Plugins/releases and add that folder as a NuGet source alongside nuget.org. They are not published to nuget.org.

```sh
dotnet run --project example-standalone
dotnet build example-maui/MauiDemo.csproj -f net10.0-android
dotnet build example-maui/MauiDemo.csproj -f net10.0-ios
dotnet pack -c Release
```

Host apps reference this project directly, or install its packed NuGet. Create a
host once by calling PluginHost.Register() on the UI thread after the first window
is ready (for example, MAUI page.Loaded). The installed framework adapter selects
the native connection and owns shutdown. DotNative calls the same API after Build().
No individual plugin registration and no DI are required. Pure managed/HTTP APIs
can run without a native host; native calls require the platform connection.

Static calls resolve the current scoped host or registered default on every call.
Do not cache its channels in static fields. Worker-thread calls are dispatched to
the host UI thread. A call before registration or after shutdown reports an error.

Add Android SDK dependencies in a Gradle file referenced by DotNativeAndroidGradle.
Add Apple SDK archives/frameworks in a manifest referenced by
DotNativeAppleDependencyManifest. Keep those declarations in buildTransitive.
