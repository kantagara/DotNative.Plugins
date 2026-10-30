using Uno.UI.Hosting;

namespace PluginUnoMobile;

public static class Program
{
    public static void Main(string[] args) => UnoPlatformHostBuilder.Create()
        .App(() => new App()).UseAppleUIKit().Build().Run();
}
