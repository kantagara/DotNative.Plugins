using System.Security.Cryptography;
using DotNative.Plugins;
using DotNative.Plugins.Uno;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StandalonePlugins;

namespace PluginUno;

public sealed class App : Application
{
    public static bool Verify { get; set; }
    private Window? window;
    private bool started;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var status = new TextBlock { Text = "Starting native plugin…", Margin = new Thickness(24) };
        window = new Window { Title = "Standalone Plugins — Uno", Content = status };
        status.Loaded += async (_, _) =>
        {
            if (started)
                return;
            started = true;
            try
            {
                await PluginChecks.Run(() =>
                    UnoPluginHost.Register(
                        window.DispatcherQueue,
                        new NativePluginConnection(
                            Path.Combine(
                                AppContext.BaseDirectory,
                                OperatingSystem.IsWindows() ? "dotnative_plugins_host.dll"
                                    : OperatingSystem.IsLinux() ? "libdotnative_plugins_host.so"
                                    : "libdotnative_plugins_host.dylib"
                            )
                        )
                    )
                );
                PluginHost.Register();
                PluginHost.Register();
                var input = RandomNumberGenerator.GetBytes(1000);
                var automaticHash = await Task.Run(() => Probe.HashAsync(input));
                if (!automaticHash.SequenceEqual(SHA256.HashData(input)))
                    throw new Exception("Automatic host mismatch");
                Console.WriteLine(
                    "PASS: parameterless PluginHost.Register and worker-thread native call"
                );
                window.Closed += (_, _) =>
                {
                    try
                    {
                        _ = PluginRuntime.Current;
                        Console.WriteLine("FAIL: automatic host survived window close");
                        Environment.ExitCode = 1;
                    }
                    catch (InvalidOperationException)
                    {
                        Console.WriteLine("PASS: automatic host disposed on window close");
                    }
                };
                var success = true;
                status.Text = "PASS: Uno native SDK, errors, cancellation, shutdown and restart";
                Console.WriteLine(status.Text);
                if (Verify)
                {
                    Environment.ExitCode = success ? 0 : 1;
                    window.Close();
                    Exit();
                }
            }
            catch (Exception error)
            {
                status.Text = "FAIL: " + error;
                Console.WriteLine(status.Text);
                if (Verify)
                {
                    Environment.ExitCode = 1;
                    window.Close();
                    Exit();
                }
            }
        };
        window.Activate();
    }
}
