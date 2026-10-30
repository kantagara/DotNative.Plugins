using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DotNative.Plugins;
using DotNative.Plugins.Avalonia;
using StandalonePlugins;

namespace PluginAvalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var status = new TextBlock
            {
                Text = "Starting native plugin…",
                Margin = new Thickness(24),
            };
            var window = new Window
            {
                Title = "Standalone Plugins — Avalonia",
                Width = 520,
                Height = 180,
                Content = status,
            };
            window.Opened += async (_, _) =>
            {
                try
                {
                    await PluginChecks.Run(() =>
                        AvaloniaPluginHost.Register(
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
                    status.Text =
                        "PASS: Avalonia native SDK, errors, cancellation, shutdown and restart";
                    Console.WriteLine(status.Text);
                    if (desktop.Args?.Contains("--verify") == true)
                        desktop.Shutdown(success ? 0 : 1);
                }
                catch (Exception error)
                {
                    status.Text = "FAIL: " + error;
                    Console.WriteLine(status.Text);
                    if (desktop.Args?.Contains("--verify") == true)
                        desktop.Shutdown(1);
                }
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
