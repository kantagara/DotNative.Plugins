using Microsoft.UI.Dispatching;

namespace DotNative.Plugins.Uno;

public sealed class UnoPluginDispatcher(DispatcherQueue dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.HasThreadAccess;

    public void Post(Action action)
    {
        if (!dispatcher.TryEnqueue(() => action()))
            throw new InvalidOperationException("Uno dispatcher is shutting down.");
    }
}

public static class UnoPluginHost
{
    private static PluginHost? automatic;
    private static Action? detach;
    private static Func<IPluginConnection>? connectionFactory;
    private static Func<Action, Action>? attachNativeLifetime;

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void InstallPlatform(Func<IPluginConnection> factory, Func<Action, Action> lifetime)
    {
        connectionFactory = factory;
        attachNativeLifetime = lifetime;
    }

    public static void Install()
    {
#if ANDROID
        global::DotNative.Plugins.Android.AndroidPluginActivity.Install();
#endif
        PluginHost.InstallAdapter("Uno", IsActive, RegisterAutomatic);
    }

    private static void StopAutomatic()
    {
        var host = automatic;
        automatic = null;
        detach?.Invoke();
        detach = null;
        host?.Dispose();
    }

    private static bool IsActive() => global::Microsoft.UI.Xaml.Application.Current is not null;

    private static void RegisterAutomatic()
    {
        var dispatcher =
            DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "Call PluginHost.Register() on the Uno UI thread."
            );
        if (!dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Uno UI thread required.");
        if (automatic is not null)
        {
            automatic.RegisterDefault();
            return;
        }
#pragma warning disable CS0618
        var window =
            global::Microsoft.UI.Xaml.Window.Current
            ?? throw new InvalidOperationException(
                "Call PluginHost.Register() after the Uno window is active."
            );
#pragma warning restore CS0618
        automatic = Register(dispatcher, connectionFactory?.Invoke() ?? PlatformPluginConnection.Create());
        global::Windows.Foundation.TypedEventHandler<
            object,
            global::Microsoft.UI.Xaml.WindowEventArgs
        > closed = (_, _) => StopAutomatic();
        window.Closed += closed;
        detach = () => window.Closed -= closed;
        if (window.Content is global::Microsoft.UI.Xaml.FrameworkElement root)
        {
            global::Microsoft.UI.Xaml.RoutedEventHandler unloaded = (_, _) => StopAutomatic();
            root.Unloaded += unloaded;
            detach += () => root.Unloaded -= unloaded;
        }
        detach += attachNativeLifetime?.Invoke(StopAutomatic);
    }

    public static PluginHost Create(
        DispatcherQueue dispatcher,
        IPluginConnection connection,
        PresentationTarget? target = null
    ) => new(new UnoPluginDispatcher(dispatcher), connection, target);

    /// <summary>Create and register the default host for static plugin APIs.
    /// Dispose the returned host when the owning application/window stops.</summary>
    public static PluginHost Register(
        DispatcherQueue dispatcher,
        IPluginConnection connection,
        PresentationTarget? target = null
    )
    {
        var host = Create(dispatcher, connection, target);
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
