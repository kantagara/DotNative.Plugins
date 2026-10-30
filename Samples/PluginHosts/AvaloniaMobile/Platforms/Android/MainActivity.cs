using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace PluginAvaloniaMobile;

[Activity(MainLauncher = true, Theme = "@style/PluginTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App> { }
