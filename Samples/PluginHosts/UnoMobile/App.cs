using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PluginUnoMobile;

public sealed partial class App : Application
{
    public App() => InitializeComponent();

    private Window? window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window ??= new Window();
        window.Content = CreateProbe(window, 1);
        window.Activate();
    }

    private static TextBlock CreateProbe(Window window, int cycle)
    {
        var text = new TextBlock { Text = $"Checking native plugin, cycle {cycle}", Margin = new Thickness(24) };
        var started = false;
        text.Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                await AutomaticPluginChecks.RunAsync($"Uno mobile cycle {cycle}");
                if (cycle == 1)
                {
                    AutomaticPluginChecks.BeginShutdownCheck();
                    window.Content = new TextBlock { Text = "Replacing root view" };
                    await AutomaticPluginChecks.ExpectStoppedAsync();
                    window.Content = CreateProbe(window, 2);
                }
                else
                {
                    text.Text = "PASS: native plugin and automatic lifecycle restart";
                    Console.WriteLine("PASS: Uno mobile automatic root lifecycle restart");
                }
            }
            catch (Exception error)
            {
                text.Text = "FAIL: " + error.Message;
                Console.WriteLine("FAIL: " + error);
            }
        };
        return text;
    }
}
