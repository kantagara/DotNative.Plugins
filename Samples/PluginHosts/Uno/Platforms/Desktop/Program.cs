using Uno.UI.Hosting;

namespace PluginUno;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.Verify = args.Contains("--verify");
        var builder = UnoPlatformHostBuilder.Create().App(() => new App());
#if PLUGIN_WINDOWS
        builder.UseWin32();
#elif PLUGIN_LINUX
        builder.UseX11();
#else
        builder.UseMacOS();
#endif
        var host = builder.Build();
        host.Run();
    }
}
