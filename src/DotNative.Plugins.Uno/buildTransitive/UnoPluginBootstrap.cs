#pragma warning disable CA2255 // Intentional adapter bootstrap, including library consumers.
// Included by the adapter NuGet: roots the adapter for trimming/AOT without reflection.
internal static class DotNativeUnoPluginBootstrap
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
#if ANDROID
        // Uno Skia selects the neutral adapter assembly even on Android. Compile
        // platform wiring in the consumer against its selected Uno renderer.
        global::DotNative.Plugins.Uno.UnoPluginHost.InstallPlatform(
            () =>
            {
                global::DotNative.Plugins.Android.AndroidPluginActivity.Install();
                var activity = global::Uno.UI.BaseActivity.Current
                    ?? global::DotNative.Plugins.Android.AndroidPluginActivity.Current
                    ?? throw new global::System.InvalidOperationException(
                        "Call PluginHost.Register() after the Uno Android activity is ready.");
                return new global::DotNative.Plugins.Android.AndroidPluginConnection(activity);
            },
            stop =>
            {
                var activity = global::DotNative.Plugins.Android.AndroidPluginConnection.Current!.Activity;
                global::System.Action<global::Android.App.Activity> destroyed = owner =>
                {
                    if (global::System.Object.ReferenceEquals(owner, activity)) stop();
                };
                global::DotNative.Plugins.Android.AndroidPluginActivity.Destroyed += destroyed;
                return () => global::DotNative.Plugins.Android.AndroidPluginActivity.Destroyed -= destroyed;
            });
#endif
        global::DotNative.Plugins.Uno.UnoPluginHost.Install();
    }
}
