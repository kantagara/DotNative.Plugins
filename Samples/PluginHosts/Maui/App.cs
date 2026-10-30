using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNative.Firebase.RemoteConfig;
using DotNative.Plugins;
using DotNative.Plugins.Maui;
using StandalonePlugins;

namespace PluginMaui;

public sealed class App : Application
{
    private PluginHost? host;
#if ANDROID
    internal static DotNative.Plugins.Android.AndroidPluginConnection? AndroidConnection
    {
        get;
        private set;
    }
#endif

    private static void Report(string message)
    {
        Console.WriteLine(message);
#if ANDROID
        global::Android.Util.Log.Info("DotNativePlugins", message);
#endif
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var status = new Label { Text = "Starting native plugin…", AutomationId = "PluginStatus" };
        var page = new ContentPage { Padding = 32, Content = status };
        var window = new Window(page);
        page.Loaded += async (_, _) =>
        {
            if (host is not null)
                return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                IPluginConnection CreateConnection()
                {
#if ANDROID
                    return AndroidConnection =
                        new DotNative.Plugins.Android.AndroidPluginConnection(
                            Microsoft.Maui.ApplicationModel.Platform.CurrentActivity!
                        );
#else
                    return new NativePluginConnection();
#endif
                }
                await PluginChecks.Run(() =>
                    MauiPluginHost.Register(page.Dispatcher, CreateConnection())
                );
                Report(
                    "PASS: MAUI native errors, large replies, cancellation, shutdown and restart"
                );
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    PluginHost.Register();
                    PluginHost.Register();
                    var bytes = System.Text.Encoding.UTF8.GetBytes("One SDK, multiple .NET hosts.");
                    var hash = await Probe.HashAsync(bytes, timeout.Token);
                    if (!hash.SequenceEqual(SHA256.HashData(bytes)))
                        throw new InvalidOperationException("Native SHA256 mismatch");
                    using var configuration =
                        typeof(App).Assembly.GetManifestResourceStream("firebase-test.json")
                        ?? throw new InvalidOperationException(
                            "Build with FirebaseTestConfig pointing to local test options JSON."
                        );
                    var options =
                        await JsonSerializer.DeserializeAsync(
                            configuration,
                            FirebaseTestJson.Default.FirebaseOptions,
                            timeout.Token
                        ) ?? throw new InvalidOperationException("Invalid Firebase test options");
                    // Static plugin API: no service registration or DI.
                    await RemoteConfig.InitializeAsync(options, timeout.Token);
                    var key = "dn_" + Guid.NewGuid().ToString("N");
                    var value = "native-sdk-cycle-" + cycle;
                    await RemoteConfig.SetDefaultsAsync(
                        new Dictionary<string, string> { [key] = value },
                        timeout.Token
                    );
                    if (await RemoteConfig.GetStringAsync(key, timeout.Token) != value)
                        throw new InvalidOperationException("Firebase native defaults mismatch");
                    Report($"PASS: Firebase native defaults cycle {cycle + 1}");
                    if (cycle == 0)
                    {
                        Report("Checking Firebase server fetch…");
                        await RemoteConfig.FetchAsync(timeout.Token);
                    }
                    if (await RemoteConfig.GetStringAsync(key, timeout.Token) != value)
                        throw new InvalidOperationException("Firebase default lost after fetch");
                    Report($"PASS: MAUI native Firebase cycle {cycle + 1}");
                    // The adapter owns this host until window/activity destruction.
#if ANDROID
                    AndroidConnection = null;
#endif
                }
                status.Text = "PASS: Firebase SDK, fetch, dispose and restart";
                Report(status.Text);
            }
            catch (Exception error)
            {
                status.Text = "FAIL: " + error;
                Report(status.Text);
            }
        };
        window.Destroying += (_, _) =>
        {
            host?.Dispose();
            host = null;
#if ANDROID
            AndroidConnection = null;
#endif
        };
        return window;
    }
}

public static class MauiProgram
{
    public static Microsoft.Maui.Hosting.MauiApp CreateMauiApp() =>
        Microsoft.Maui.Hosting.MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}

[JsonSerializable(typeof(FirebaseOptions))]
internal partial class FirebaseTestJson : JsonSerializerContext { }
