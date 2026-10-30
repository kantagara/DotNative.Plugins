using DotNative.Plugins;
using PluginTemplate;

namespace PluginDemo;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var status = new Label { Text = "Starting native plugin…" };
        var page = new ContentPage { Padding = 24, Content = status };
        var window = new Window(page);
        bool started = false;
        page.Loaded += async (_, _) =>
        {
            if (started)
                return;
            started = true;
            try
            {
                PluginHost.Register();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                status.Text = await NativeApi.ReadAsync(timeout.Token);
                Console.WriteLine("PLUGIN_RESULT PASS: " + status.Text);
#if ANDROID
                global::Android.Util.Log.Info(
                    "DotNativePlugins",
                    "PLUGIN_RESULT PASS: " + status.Text
                );
#endif
            }
            catch (Exception error)
            {
                status.Text = error.Message;
                Console.WriteLine("PLUGIN_RESULT FAIL: " + error);
            }
        };
        return window;
    }
}
