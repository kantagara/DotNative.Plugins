using System.Collections.Concurrent;
using DotNative;
using DotNative.Plugins;
using PluginTemplate;

// A console host owns its main-thread queue. UI apps use their framework dispatcher.
try
{
    var dispatcher = new MainThreadDispatcher();
    var filename =
        OperatingSystem.IsMacOS() ? "libdotnative_plugins_host.dylib"
        : OperatingSystem.IsWindows() ? "dotnative_plugins_host.dll"
        : "libdotnative_plugins_host.so";
    using var host = new PluginHost(
        dispatcher,
        new NativePluginConnection(Path.Combine(AppContext.BaseDirectory, filename))
    ).RegisterDefault();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var call = NativeApi.ReadAsync(timeout.Token);
    while (!call.IsCompleted)
    {
        dispatcher.Drain();
        Thread.Sleep(1);
    }
    Console.WriteLine(call.GetAwaiter().GetResult());
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}

sealed class MainThreadDispatcher : IUiDispatcher
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> queue = new();

    public bool CheckAccess() => Environment.CurrentManagedThreadId == thread;

    public void Post(Action action) => queue.Enqueue(action);

    public void Drain()
    {
        while (queue.TryDequeue(out var action))
            action();
    }
}
