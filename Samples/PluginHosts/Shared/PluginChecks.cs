using System.Security.Cryptography;
using DotNative.Plugins;
using StandalonePlugins;

internal static class PluginChecks
{
    internal static async Task Run(Func<PluginHost> create)
    {
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var host = create();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bytes = RandomNumberGenerator.GetBytes(20000);
            host.RegisterDefault(); // Idempotent on the same host.
            var hash = await Task.Run(() => Probe.HashAsync(bytes, deadline.Token));
            if (!hash.SequenceEqual(SHA256.HashData(bytes)))
                throw new Exception("Native SHA256 mismatch");
            var channel = host.Channels.Get("dotnative.standalone.probe");
            var echo = await channel.InvokeAsync("echo", bytes, deadline.Token);
            if (echo is not byte[] result || !result.SequenceEqual(bytes))
                throw new Exception("Large native reply mismatch");
            await Reject<PluginException>(() =>
                channel.InvokeAsync("fail", cancellationToken: deadline.Token)
            );
            await Reject<PluginException>(() =>
                host.Channels.Get("missing").InvokeAsync("test", cancellationToken: deadline.Token)
            );
            using var cancel = new CancellationTokenSource();
            var pending = channel.InvokeAsync("wait", cancellationToken: cancel.Token);
            await Task.Yield();
            cancel.Cancel();
            await Reject<OperationCanceledException>(() => pending);
            await host.RunAsync(async () =>
            {
                await Task.Yield();
                if (!ReferenceEquals(PluginRuntime.Current.Channels, host.Channels))
                    throw new Exception("Lost plugin scope");
            });
            var shutdown = channel.InvokeAsync("wait");
            host.Dispose();
            await Reject<ObjectDisposedException>(() => shutdown);
            await Reject<InvalidOperationException>(() => Probe.HashAsync(bytes));
        }
    }

    private static async Task Reject<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
