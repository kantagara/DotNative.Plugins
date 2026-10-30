using Microsoft.Maui.Dispatching;

namespace DotNative.Plugins.Maui;

public sealed class MauiPluginDispatcher(IDispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => !dispatcher.IsDispatchRequired;

    public void Post(Action action)
    {
        if (!dispatcher.Dispatch(action))
            throw new InvalidOperationException("MAUI dispatcher is shutting down.");
    }
}

public static class MauiPluginHost
{
    private static PluginHost? automatic;
    private static Action? detach;

    public static void Install()
    {
#if ANDROID
        global::DotNative.Plugins.Android.AndroidPluginActivity.Install();
#endif
        PluginHost.InstallAdapter("Maui", IsActive, RegisterAutomatic);
    }

    private static void StopAutomatic()
    {
        var host = automatic;
        automatic = null;
        detach?.Invoke();
        detach = null;
        host?.Dispose();
    }

#if ANDROID
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        global::Microsoft.Maui.LifecycleEvents.ILifecycleBuilder,
        object
    > Hooks = new();

    private static void HookLifecycle(
        global::Microsoft.Maui.LifecycleEvents.ILifecycleBuilder events
    )
    {
        Hooks.GetValue(
            events,
            service =>
            {
                global::Microsoft.Maui.LifecycleEvents.AndroidLifecycle.OnActivityResult result = (
                    owner,
                    request,
                    code,
                    data
                ) =>
                {
                    var connection = global::DotNative
                        .Plugins
                        .Android
                        .AndroidPluginConnection
                        .Current;
                    if (connection is not null && ReferenceEquals(connection.Activity, owner))
                        connection.OnActivityResult(request, code, data);
                };
                global::Microsoft.Maui.LifecycleEvents.AndroidLifecycle.OnNewIntent intent = (
                    owner,
                    data
                ) =>
                {
                    var connection = global::DotNative
                        .Plugins
                        .Android
                        .AndroidPluginConnection
                        .Current;
                    if (
                        connection is not null
                        && ReferenceEquals(connection.Activity, owner)
                        && data is not null
                    )
                        connection.OnNewIntent(data);
                };
                global::Microsoft.Maui.LifecycleEvents.AndroidLifecycle.OnRequestPermissionsResult permissions =
                    (owner, request, names, values) =>
                    {
                        var connection = global::DotNative
                            .Plugins
                            .Android
                            .AndroidPluginConnection
                            .Current;
                        if (connection is not null && ReferenceEquals(connection.Activity, owner))
                            connection.OnRequestPermissionsResult(request, names, values);
                    };
                service.AddEvent("OnActivityResult", result);
                service.AddEvent("OnNewIntent", intent);
                service.AddEvent("OnRequestPermissionsResult", permissions);
                return new object();
            }
        );
    }
#endif

    private static bool IsActive() =>
        global::Microsoft.Maui.Controls.Application.Current is not null;

    private static void RegisterAutomatic()
    {
        var app = global::Microsoft.Maui.Controls.Application.Current!;
        if (app.Dispatcher.IsDispatchRequired)
            throw new InvalidOperationException(
                "Call PluginHost.Register() on the MAUI UI thread."
            );
        if (automatic is not null)
        {
            automatic.RegisterDefault();
            return;
        }
        if (app.Windows.Count != 1)
            throw new InvalidOperationException(
                "Call PluginHost.Register() after one MAUI window is ready. Multiple windows require explicit host selection."
            );
        var window = app.Windows[0];
#if ANDROID
        global::DotNative.Plugins.Android.AndroidPluginActivity.Install();
        var activity =
            global::Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
            ?? throw new InvalidOperationException("MAUI Android activity is not ready.");
        var connection = new global::DotNative.Plugins.Android.AndroidPluginConnection(activity);
#else
        var connection = PlatformPluginConnection.Create();
#endif
        automatic = Register(window.Dispatcher, connection);
        EventHandler destroyed = (_, _) => StopAutomatic();
        window.Destroying += destroyed;
        detach = () => window.Destroying -= destroyed;
#if ANDROID
        void OnDestroyed(global::Android.App.Activity ended)
        {
            if (ReferenceEquals(ended, activity))
                StopAutomatic();
        }
        global::DotNative.Plugins.Android.AndroidPluginActivity.Destroyed += OnDestroyed;
        var detachWindow = detach;
        detach = () =>
        {
            detachWindow();
            global::DotNative.Plugins.Android.AndroidPluginActivity.Destroyed -= OnDestroyed;
        };
        var events =
            (window.Handler?.MauiContext ?? app.Handler?.MauiContext)?.Services.GetService(
                typeof(global::Microsoft.Maui.LifecycleEvents.ILifecycleEventService)
            ) as global::Microsoft.Maui.LifecycleEvents.ILifecycleBuilder;
        if (events is not null)
            HookLifecycle(events);

#endif
    }

    public static PluginHost Create(
        IDispatcher dispatcher,
        IPluginConnection connection,
        PresentationTarget? target = null
    ) => new(new MauiPluginDispatcher(dispatcher), connection, target);

    /// <summary>Create and register the default host for static plugin APIs.
    /// Dispose the returned host when the owning application/window stops.</summary>
    public static PluginHost Register(
        IDispatcher dispatcher,
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
