# DotNative.Firebase.RemoteConfig (experimental local package)

Native Firebase Remote Config, independent of any .NET UI framework. Android uses
Kotlin and Firebase Config; iOS/Mac Catalyst use Swift and Firebase Remote Config.
No Rust transport, Analytics, Messaging or Crashlytics SDK is included.

The package supplies native compilation, dependency resolution and registration
through transitive MSBuild rules. Android's minimum OS is raised to API 23 to
match Firebase 34.x. Apple builds require compatible .NET workloads and Xcode.

Install the framework adapter package and call `PluginHost.Register()` once on
the UI thread after the window is ready (DotNative: after `builder.Build()`). No per-plugin service registration or DI is required:

```csharp
using DotNative.Firebase.RemoteConfig;
using DotNative.Plugins;

PluginHost.Register();

await RemoteConfig.InitializeAsync(options);
await RemoteConfig.SetDefaultsAsync(new Dictionary<string, string> { ["welcome"] = "Hello" });
await RemoteConfig.FetchAsync();
var welcome = await RemoteConfig.GetStringAsync("welcome");
```

Static calls resolve the current host on each invocation and support worker
threads. `new FirebaseRemoteConfig(host.Channels)` remains available for explicit
instance usage. The automatic adapter disposes its host at application/window shutdown. Installing a
package cannot supply your Firebase configuration or initialize a host by itself.

Version 0.1.0-preview.16 is distributed in the GitHub preview release, not on nuget.org. It is not a production release.
Windows/Linux do not have an implementation in this package.
