using Avalonia.iOS;
using Foundation;
using UIKit;

namespace PluginAvaloniaMobile;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App> { }

public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
