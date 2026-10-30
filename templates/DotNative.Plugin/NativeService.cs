using DotNative.Plugins;

namespace PluginTemplate;

public static class NativeApi
{
    public static Task<string> ReadAsync(CancellationToken cancellationToken = default) =>
        new NativeService(PluginRuntime.Current.Channels).ReadAsync(cancellationToken);
}

public sealed class NativeService(IPlatformChannels channels)
{
    public async Task<string> ReadAsync(CancellationToken cancellationToken = default) =>
        (string)
            (
                await channels
                    .Get("dotnative.plugins.pluginchannel")
                    .InvokeAsync("read", cancellationToken: cancellationToken)
            )!;
}
