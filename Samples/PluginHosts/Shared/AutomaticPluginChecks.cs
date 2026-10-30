using System.Security.Cryptography;
using DotNative.Plugins;
using StandalonePlugins;

internal static class AutomaticPluginChecks
{
    private static Task<object?>? pending;
    private static IPlatformChannels? previous;

    public static async Task RunAsync(string label)
    {
        PluginHost.Register();
        var channels = PluginRuntime.Current.Channels;
        PluginHost.Register();
        if (!ReferenceEquals(channels, PluginRuntime.Current.Channels))
            throw new Exception("Registration is not idempotent");
        if (ReferenceEquals(previous, channels))
            throw new Exception("Recreated view reused a stopped host");
#if MIGRATED_PLUGINS
        await MigratedPluginChecks.RunAsync();
#endif
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bytes = RandomNumberGenerator.GetBytes(20000);
        var hash = await Task.Run(() => Probe.HashAsync(bytes, deadline.Token));
        if (!hash.SequenceEqual(SHA256.HashData(bytes)))
            throw new Exception("Native SHA256 mismatch");
        var channel = channels.Get("dotnative.standalone.probe");
        if (await channel.InvokeAsync("echo", bytes, deadline.Token) is not byte[] echo
            || !echo.SequenceEqual(bytes))
            throw new Exception("Native 20 KB reply mismatch");
        await Reject<PluginException>(() => channel.InvokeAsync("fail"));
        await Reject<PluginException>(() => channels.Get("missing").InvokeAsync("test"));
        using var cancel = new CancellationTokenSource();
        var wait = channel.InvokeAsync("wait", cancellationToken: cancel.Token);
        cancel.Cancel();
        await Reject<OperationCanceledException>(() => wait);
        Console.WriteLine($"PASS: {label}: no-argument registration, worker native SHA256, 20 KB, errors, cancellation");
    }

    public static void BeginShutdownCheck()
    {
        previous = PluginRuntime.Current.Channels;
        pending = previous.Get("dotnative.standalone.probe").InvokeAsync("wait");
    }

    public static async Task ExpectStoppedAsync()
    {
        if (pending is null) throw new Exception("Shutdown check was not started");
        await Reject<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Reject<InvalidOperationException>(() => Probe.HashAsync(new byte[] { 1 }));
        Console.WriteLine("PASS: automatic lifecycle disposed host and rejected pending native request");
        pending = null;
    }

    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
