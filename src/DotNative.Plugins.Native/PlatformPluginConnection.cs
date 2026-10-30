namespace DotNative.Plugins;

/// <summary>Platform connection selection used by framework adapters.</summary>
public static class PlatformPluginConnection
{
    public static Func<IPluginConnection>? AndroidFactory { private get; set; }

    public static IPluginConnection Create()
    {
        if (OperatingSystem.IsAndroid())
            return AndroidFactory?.Invoke()
                ?? throw new InvalidOperationException(
                    "The Android plugin adapter is not initialized."
                );
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            return new NativePluginConnection();
        var file =
            OperatingSystem.IsWindows() ? "dotnative_plugins_host.dll"
            : OperatingSystem.IsMacOS() ? "libdotnative_plugins_host.dylib"
            : OperatingSystem.IsLinux() ? "libdotnative_plugins_host.so"
            : throw new PlatformNotSupportedException("No native plugin connection for this OS.");
        return new NativePluginConnection(Path.Combine(AppContext.BaseDirectory, file));
    }
}
