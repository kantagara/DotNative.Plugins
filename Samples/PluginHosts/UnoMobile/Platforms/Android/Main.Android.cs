using Android.App;
using Android.Runtime;

namespace PluginUnoMobile;

[Application(Label = "Plugin Uno Mobile", HardwareAccelerated = true,
    Theme = "@style/PluginTheme")]
public class NativeApplication : Microsoft.UI.Xaml.NativeApplication
{
    public NativeApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(() => new App(), handle, ownership) { }
}
