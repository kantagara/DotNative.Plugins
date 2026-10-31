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

### Application implementations for unsupported platforms

An application can supply `INativeService` without DI. Keep the returned registration
alive for the application's lifetime and dispose it on shutdown:

```csharp
var registration = PluginImplementations.Register<INativeService>(() => new DesktopNativeService());
var value = await NativeApi.ReadAsync();
// At application shutdown:
registration.Dispose();
```

The application may split its own `partial class DesktopNativeService : INativeService`
across shared and platform-specific files. A partial class cannot extend a type
from the installed NuGet assembly, so the interface is the extension boundary.
Only compile the matching platform implementation in each target. An override
that uses managed/HTTP APIs needs no native host; the built-in native implementation
still uses `PluginHost.Register()`. Factories run per static call, and the application
owns the lifetime and thread safety of the returned implementation. Duplicate active
registrations for the same interface are rejected.
