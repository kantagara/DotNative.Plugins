using Android.App;
using Android.OS;

namespace DotNative.Plugins.Android;

/// <summary>Tracks the native activity without requiring a framework-specific startup call.</summary>
public sealed class AndroidPluginActivity
    : Java.Lang.Object,
        Application.IActivityLifecycleCallbacks
{
    private static AndroidPluginActivity? tracker;
    private static WeakReference<Activity>? current;
    public static Activity? Current =>
        current is { } reference && reference.TryGetTarget(out var value) ? value : null;
    public static event Action<Activity>? Destroyed;

    public static void Install()
    {
        if (tracker is not null)
            return;
        if (Application.Context is not Application app)
            return; // Bootstrap may run before Application exists.
        tracker = new();
        app.RegisterActivityLifecycleCallbacks(tracker);
        PlatformPluginConnection.AndroidFactory = () =>
            new AndroidPluginConnection(
                Current
                    ?? throw new InvalidOperationException(
                        "Call PluginHost.Register() after the Android activity is created."
                    )
            );
    }

    private void Track(Activity activity)
    {
        if (activity.Class?.Name != "com.dotnative.plugins.PluginResultActivity")
            current = new(activity);
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) => Track(activity);

    public void OnActivityResumed(Activity activity) => Track(activity);

    public void OnActivityStarted(Activity activity) => Track(activity);

    public void OnActivityPaused(Activity activity) { }

    public void OnActivityStopped(Activity activity) { }

    public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }

    public void OnActivityDestroyed(Activity activity)
    {
        if (ReferenceEquals(Current, activity))
            current = null;
        Destroyed?.Invoke(activity);
    }
}
