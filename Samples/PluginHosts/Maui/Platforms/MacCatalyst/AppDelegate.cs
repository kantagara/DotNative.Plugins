using Foundation;
using UIKit;

namespace PluginMaui;

[Register("SceneDelegate")]
public sealed class SceneDelegate : MauiUISceneDelegate { }

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
