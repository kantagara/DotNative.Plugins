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
                    await AutomaticPluginChecks.RunAsync("Avalonia desktop");
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
                        "PASS: Avalonia native SDK, errors and cancellation";
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
