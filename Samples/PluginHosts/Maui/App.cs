using DotNative.Plugins;
#if FIREBASE_TEST
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNative.Firebase.RemoteConfig;
#endif

namespace PluginMaui;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var status = new Label { Text = "Starting native plugin…", AutomationId = "PluginStatus" };
        var page = new ContentPage { Padding = 32, Content = status };
        var window = new Window(page);
        var running = false;
        page.Loaded += async (_, _) =>
        {
            if (running) return;
            running = true;
            try
            {
                await AutomaticPluginChecks.RunAsync("MAUI");
#if FIREBASE_TEST
                using var configuration = typeof(App).Assembly.GetManifestResourceStream("firebase-test.json")
                    ?? throw new InvalidOperationException("Missing Firebase test configuration");
                var options = await JsonSerializer.DeserializeAsync(configuration,
                    FirebaseTestJson.Default.FirebaseOptions)
                    ?? throw new InvalidOperationException("Invalid Firebase configuration");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await RemoteConfig.InitializeAsync(options, timeout.Token);
                var key = "dn_" + Guid.NewGuid().ToString("N");
                await RemoteConfig.SetDefaultsAsync(new Dictionary<string, string> { [key] = "native-default" }, timeout.Token);
                if (await RemoteConfig.GetStringAsync(key, timeout.Token) != "native-default")
                    throw new Exception("Firebase defaults mismatch");
                await RemoteConfig.FetchAsync(timeout.Token);
                Console.WriteLine("PASS: MAUI Firebase native defaults and server fetch");
#endif
                status.Text = "PASS: native plugin through parameterless registration";
                Console.WriteLine(status.Text);
            }
            catch (Exception error)
            {
                status.Text = "FAIL: " + error;
                Console.WriteLine(status.Text);
            }
            finally { running = false; }
        };
        return window;
    }
}

public static class MauiProgram
{
    public static Microsoft.Maui.Hosting.MauiApp CreateMauiApp() =>
        Microsoft.Maui.Hosting.MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}
#if FIREBASE_TEST
[JsonSerializable(typeof(FirebaseOptions))]
internal partial class FirebaseTestJson : JsonSerializerContext { }
#endif
