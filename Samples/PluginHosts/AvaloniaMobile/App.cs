using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Media;

namespace PluginAvaloniaMobile;

public sealed class App : Avalonia.Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = CreateProbe(single, 1);
        base.OnFrameworkInitializationCompleted();
    }

    private static Control CreateProbe(ISingleViewApplicationLifetime lifetime, int cycle)
    {
        var view = new TextBlock
        {
            Text = $"Checking native plugin, cycle {cycle}",
            Margin = new Thickness(24, 64, 24, 24),
            Foreground = Brushes.White,
            Background = Brushes.Black,
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
        };
        var started = false;
        view.Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                await AutomaticPluginChecks.RunAsync($"Avalonia mobile cycle {cycle}");
                if (cycle == 1)
                {
                    AutomaticPluginChecks.BeginShutdownCheck();
                    lifetime.MainView = new TextBlock { Text = "Replacing root view" };
                    await AutomaticPluginChecks.ExpectStoppedAsync();
                    Dispatcher.UIThread.Post(() => lifetime.MainView = CreateProbe(lifetime, 2));
                }
                else
                {
                    view.Text = "PASS: native plugin and automatic lifecycle restart";
                    Console.WriteLine("PASS: Avalonia mobile automatic root lifecycle restart");
                }
            }
            catch (Exception error)
            {
                view.Text = "FAIL: " + error.Message;
                Console.WriteLine("FAIL: " + error);
            }
        };
        return view;
    }
}
