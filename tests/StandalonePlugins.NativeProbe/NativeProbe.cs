using DotNative.Plugins;

namespace StandalonePlugins;

public sealed class NativeProbe(IPlatformChannels channels)
{
    public async Task<byte[]> HashAsync(
        byte[] bytes,
        CancellationToken cancellationToken = default
    ) =>
        (byte[])
            (
                await channels
                    .Get("dotnative.standalone.probe")
                    .InvokeAsync("sha256", bytes, cancellationToken)
            )!;
}

/// <summary>Static entry point used by host integration tests, including worker threads.</summary>
public static class Probe
{
    public static Task<byte[]> HashAsync(
        byte[] bytes,
        CancellationToken cancellationToken = default
    ) => new NativeProbe(PluginRuntime.Current.Channels).HashAsync(bytes, cancellationToken);
}
