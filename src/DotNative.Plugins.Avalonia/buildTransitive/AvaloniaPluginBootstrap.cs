#pragma warning disable CA2255 // Intentional adapter bootstrap, including library consumers.
// Included by the adapter NuGet: roots the adapter for trimming/AOT without reflection.
internal static class DotNativeAvaloniaPluginBootstrap
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() =>
        global::DotNative.Plugins.Avalonia.AvaloniaPluginHost.Install();
}
