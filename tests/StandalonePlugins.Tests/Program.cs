using System.Collections.Concurrent;
using System.Security.Cryptography;
using DotNative;
using DotNative.Plugins;
using StandalonePlugins;

try
{
    Check(
        (string)PluginCodec.Decode(PluginCodec.Encode("round trip"))! == "round trip",
        "Codec round trip"
    );
    EventChannelTests.Run();
    var dispatcher = new TestDispatcher();
    var library = Path.Combine(AppContext.BaseDirectory, "libdotnative_plugins_host.dylib");
    using var host = new PluginHost(dispatcher, new NativePluginConnection(library));
    var probe = new NativeProbe(host.Channels);
    var input = System.Text.Encoding.UTF8.GetBytes("One SDK, multiple .NET hosts.");
    var result = Pump(probe.HashAsync(input));
    Check(result.SequenceEqual(SHA256.HashData(input)), "Native CryptoKit SHA256 differs");
    var channel = host.Channels.Get("dotnative.standalone.probe");
    var large = new byte[20000];
    Random.Shared.NextBytes(large);
    Check(
        ((byte[])Pump(channel.InvokeAsync("echo", large))!).SequenceEqual(large),
        "Large reply resize"
    );
    Reject<PluginException>(() => Pump(channel.InvokeAsync("fail")));
    Reject<PluginException>(() => Pump(host.Channels.Get("missing").InvokeAsync("anything")));
    using var cancellation = new CancellationTokenSource();
    var pending = channel.InvokeAsync("wait", cancellationToken: cancellation.Token);
    dispatcher.Drain();
    cancellation.Cancel();
    Reject<OperationCanceledException>(() => Pump(pending));
    ExecutionContext? stale = null;
    Pump(
        host.RunAsync(async () =>
        {
            Check(ReferenceEquals(PluginRuntime.Current.Channels, host.Channels), "Host scope");
            await Task.Yield();
            Check(
                ReferenceEquals(PluginRuntime.Current.Channels, host.Channels),
                "Async host scope"
            );
            stale = ExecutionContext.Capture();
            return true;
        })
    );
    Reject<InvalidOperationException>(() => _ = PluginRuntime.Current);
    Reject<InvalidOperationException>(() =>
    {
        using var second = new NativePluginConnection(library);
    });
    var stopped = channel.InvokeAsync("wait");
    dispatcher.Drain();
    host.Dispose();
    Reject<ObjectDisposedException>(() => Pump(stopped));
    ExecutionContext.Run(
        stale!,
        _ => Reject<ObjectDisposedException>(() => _ = PluginRuntime.Current),
        null
    );
    using (var restarted = new PluginHost(dispatcher, new NativePluginConnection(library)))
        Check(
            Pump(new NativeProbe(restarted.Channels).HashAsync(input)).SequenceEqual(result),
            "Restart registration"
        );
    Check(
        typeof(PluginHost).Assembly.GetReferencedAssemblies().All(a => a.Name != "DotNative"),
        "Core references renderer"
    );
    Console.WriteLine(
        "PASS standalone NuGet build, native CryptoKit, codec/events, cancellation, errors, scope, shutdown and restart."
    );

    T Pump<T>(Task<T> task)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < timeout)
        {
            dispatcher.Drain();
            Thread.Sleep(1);
        }
        if (!task.IsCompleted)
            throw new TimeoutException();
        return task.GetAwaiter().GetResult();
    }
    static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    static void Reject<T>(Action callback)
        where T : Exception
    {
        try
        {
            callback();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

sealed class TestDispatcher : IUiDispatcher
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> pending = new();

    public bool CheckAccess() => Environment.CurrentManagedThreadId == thread;

    public void Post(Action action) => pending.Enqueue(action);

    public void Drain()
    {
        while (pending.TryDequeue(out var callback))
            callback();
    }
}
