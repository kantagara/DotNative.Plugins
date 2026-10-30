using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;

namespace DotNative.Plugins.Android;

/// <summary>Framework-independent Activity bridge. Forward Activity results before base callbacks.</summary>
public sealed class AndroidPluginConnection : IPluginConnection
{
    public static AndroidPluginConnection? Current { get; private set; }
    public Activity Activity { get; }
    private static bool active;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool disposed;

    public AndroidPluginConnection(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Activity = activity;
        if (global::Android.OS.Looper.MyLooper() != global::Android.OS.Looper.MainLooper)
            throw new InvalidOperationException("Create plugins on the Android main thread.");
        if (active)
            throw new InvalidOperationException("Only one Android plugin host may be active.");
        active = true;
        Current = this;
        try
        {
            Invoke(
                "com/dotnative/plugins/GeneratedPlugins",
                "register",
                "(Landroid/app/Activity;)V",
                new JValue(activity)
            );
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != thread)
            throw new InvalidOperationException("Android plugins require their UI thread.");
    }

    public void SendPlugin(ReadOnlySpan<byte> message)
    {
        Verify();
        var clazz = JNIEnv.FindClass("com/dotnative/plugins/NativeChannels");
        var bytes = JNIEnv.NewArray(message.ToArray());
        try
        {
            var method = JNIEnv.GetStaticMethodID(clazz, "send", "([B)I");
            if (JNIEnv.CallStaticIntMethod(clazz, method, new JValue(bytes)) != 0)
                throw new PluginException("transport", "Kotlin plugin request was rejected.");
        }
        finally
        {
            JNIEnv.DeleteLocalRef(bytes);
            JNIEnv.DeleteGlobalRef(clazz);
        }
    }

    public void DispatchReplies(PlatformChannels channels)
    {
        Verify();
        var clazz = JNIEnv.FindClass("com/dotnative/plugins/NativeChannels");
        try
        {
            var method = JNIEnv.GetStaticMethodID(clazz, "poll", "()[B");
            for (var i = 0; i < 256; i++)
            {
                var reply = JNIEnv.CallStaticObjectMethod(clazz, method);
                if (reply == IntPtr.Zero)
                    return;
                byte[] bytes;
                try
                {
                    bytes = JNIEnv.GetArray<byte>(reply)!;
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(reply);
                }
                channels.AcceptReply(bytes);
            }
        }
        finally
        {
            JNIEnv.DeleteGlobalRef(clazz);
        }
    }

    public void OnActivityResult(int requestCode, Result resultCode, Intent? data) =>
        Forward(
            "activityResult",
            "(IILandroid/content/Intent;)V",
            new JValue(requestCode),
            new JValue((int)resultCode),
            new JValue(data?.Handle ?? IntPtr.Zero)
        );

    public void OnNewIntent(Intent intent) =>
        Forward("newIntent", "(Landroid/content/Intent;)V", new JValue(intent));

    public void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grants
    )
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var names = JNIEnv.NewArray(permissions);
        var results = JNIEnv.NewArray(grants.Select(value => (int)value).ToArray());
        try
        {
            Forward(
                "permissionResult",
                "(I[Ljava/lang/String;[I)V",
                new JValue(requestCode),
                new JValue(names),
                new JValue(results)
            );
        }
        finally
        {
            JNIEnv.DeleteLocalRef(names);
            JNIEnv.DeleteLocalRef(results);
        }
    }

    private void Forward(string name, string signature, params JValue[] args)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Invoke("com/dotnative/plugins/NativeChannels", name, signature, args);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Verify();
        Invoke("com/dotnative/plugins/NativeChannels", "detach", "()V");
        active = false;
        if (ReferenceEquals(Current, this))
            Current = null;
        disposed = true;
    }

    private static void Invoke(string type, string name, string signature, params JValue[] args)
    {
        var clazz = JNIEnv.FindClass(type);
        try
        {
            JNIEnv.CallStaticVoidMethod(
                clazz,
                JNIEnv.GetStaticMethodID(clazz, name, signature),
                args
            );
        }
        finally
        {
            // JNIEnv.FindClass promotes the class to a global reference.
            JNIEnv.DeleteGlobalRef(clazz);
        }
    }
}
