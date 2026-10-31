using Avalonia.iOS;
using Foundation;
using UIKit;

namespace PluginAvaloniaMobile;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App> { }

[Register("PluginSceneDelegate")]
public sealed class PluginSceneDelegate : UIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene ||
            UIApplication.SharedApplication.Delegate is not AppDelegate appDelegate ||
            appDelegate.Window is not { } window)
            throw new InvalidOperationException("Avalonia did not initialize its application window.");

        // Avalonia 11 creates its root controller in FinishedLaunching. Attach that
        // existing window to the system scene instead of creating a second root.
        Window = window;
        window.WindowScene = windowScene;
        window.MakeKeyAndVisible();
    }
}

public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
