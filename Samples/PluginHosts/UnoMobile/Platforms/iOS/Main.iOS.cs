using Uno.UI.Hosting;
using Foundation;
using UIKit;
using Uno.UI.Xaml;

namespace PluginUnoMobile;

[Register("PluginUnoSceneDelegate")]
public sealed class PluginUnoSceneDelegate : UIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        // This single-window sample uses Uno 6's existing UIKit window. Keep its
        // root and rendering surface, and adopt the system's scene lifecycle.
        Window = (Microsoft.UI.Xaml.Application.Current as App)?.MainWindow?.GetNativeWindow() as UIWindow
            ?? throw new InvalidOperationException("Uno did not initialize its application window.");
        Window.WindowScene = windowScene;
        Window.MakeKeyAndVisible();
    }
}

public static class Program
{
    public static void Main(string[] args) => UnoPlatformHostBuilder.Create()
        .App(() => new App()).UseAppleUIKit().Build().Run();
}
