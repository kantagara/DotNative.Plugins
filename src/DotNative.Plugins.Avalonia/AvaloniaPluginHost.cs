using Avalonia.Threading;

namespace DotNative.Plugins.Avalonia;

public sealed class AvaloniaPluginDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action)
    {
        dispatcher.Post(action);
    }
}

public static class AvaloniaPluginHost
{
    private static PluginHost? automatic;
    private static Action? detach;

    public static void Install()
    {
#if ANDROID
        global::DotNative.Plugins.Android.AndroidPluginActivity.Install();
#endif
        PluginHost.InstallAdapter("Avalonia", IsActive, RegisterAutomatic);
    }

    private static void StopAutomatic()
    {
        var host = automatic;
        automatic = null;
        detach?.Invoke();
        detach = null;
        host?.Dispose();
    }

    private static bool IsActive() => global::Avalonia.Application.Current is not null;

    private static void RegisterAutomatic()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException(
                "Call PluginHost.Register() on the Avalonia UI thread."
            );
        if (automatic is not null)
        {
            automatic.RegisterDefault();
            return;
        }
        var app = global::Avalonia.Application.Current!;
        if (
            app.ApplicationLifetime
                is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window
        )
        {
            if (!window.IsVisible)
                throw new InvalidOperationException(
                    "Call PluginHost.Register() after the Avalonia window opens."
                );
            automatic = Register(PlatformPluginConnection.Create());
            EventHandler closed = (_, _) => StopAutomatic();
            window.Closed += closed;
            detach = () => window.Closed -= closed;
            return;
        }
        if (
            app.ApplicationLifetime
                is global::Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime single
            && single.MainView is { } view
        )
        {
            if (!global::Avalonia.VisualTree.VisualExtensions.IsAttachedToVisualTree(view))
                throw new InvalidOperationException(
                    "Call PluginHost.Register() after the Avalonia root view is attached."
                );
            automatic = Register(PlatformPluginConnection.Create());
            EventHandler<global::Avalonia.VisualTreeAttachmentEventArgs> detached = (_, _) =>
                StopAutomatic();
            view.DetachedFromVisualTree += detached;
            detach = () => view.DetachedFromVisualTree -= detached;
            return;
        }
        throw new InvalidOperationException(
            "Avalonia's main window/root view is not ready for PluginHost.Register()."
        );
    }

    public static PluginHost Create(
        IPluginConnection connection,
        PresentationTarget? target = null
    ) => new(new AvaloniaPluginDispatcher(Dispatcher.UIThread), connection, target);

    /// <summary>Create and register the default host for static plugin APIs.
    /// Dispose the returned host when the owning application/window stops.</summary>
    public static PluginHost Register(
        IPluginConnection connection,
        PresentationTarget? target = null
    )
    {
        var host = Create(connection, target);
        try
        {
            return host.RegisterDefault();
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }
}
