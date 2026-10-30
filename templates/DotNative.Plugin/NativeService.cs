using DotNative.Plugins;

namespace PluginTemplate;

public static class NativeApi
{
    public static Task<string> ReadAsync(CancellationToken cancellationToken = default) =>
        PluginImplementations.Resolve<INativeService>(
            () => new NativeService(PluginRuntime.Current.Channels)).ReadAsync(cancellationToken);
}

public interface INativeService
{
    Task<string> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class NativeService(IPlatformChannels channels) : INativeService
{
    public async Task<string> ReadAsync(CancellationToken cancellationToken = default) =>
        (string)
            (
                await channels
                    .Get("dotnative.plugins.pluginchannel")
                    .InvokeAsync("read", cancellationToken: cancellationToken)
            )!;
}
