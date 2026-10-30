namespace DotNative.Plugins;

/// <summary>A connection owned by one host. All methods run on its UI dispatcher.</summary>
public interface IPluginConnection : IPluginTransport, IDisposable
{
    void DispatchReplies(PlatformChannels channels);
}

/// <summary>Owns service channels, reply pumping and context lifetime, independently of UI rendering.</summary>
public sealed class PluginHost : IDisposable, IAsyncDisposable
{
    private static readonly object AdapterGate = new();
    private static readonly Dictionary<string, (Func<bool> Active, Action Register)> Adapters =
        new();

    /// <summary>Connect the active framework's native host. Call on its UI thread after
    /// the first window/activity is ready. The adapter owns shutdown. Repeated calls are harmless.</summary>
    public static void Register()
    {
        lock (AdapterGate)
        {
            var active = Adapters.Where(pair => pair.Value.Active()).ToArray();
            if (active.Length == 0)
                throw new InvalidOperationException(
                    "No active plugin adapter. Install the MAUI, Avalonia or Uno adapter package and call PluginHost.Register() on the UI thread after the window/activity is ready. In DotNative, call after builder.Build()."
                );
            if (active.Length != 1)
                throw new InvalidOperationException(
                    "Multiple plugin frameworks are active. Use an explicit adapter host instead of PluginHost.Register()."
                );
            active[0].Value.Register();
        }
    }

    /// <summary>Adapter bootstrap hook, called by generated startup code; never creates a native host.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void InstallAdapter(string name, Func<bool> isActive, Action register)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(isActive);
        ArgumentNullException.ThrowIfNull(register);
        lock (AdapterGate)
            Adapters.TryAdd(name, (isActive, register));
    }

    private readonly IUiDispatcher dispatcher;
    private readonly IPluginConnection connection;
    private readonly Timer timer;
    private IDisposable? registration;
    private int queued;
    private int stopped;
    private Exception? fault;
    public PlatformChannels Channels { get; }
    public PresentationTarget Target { get; }
    public event Action<Exception>? Faulted;

    /// <summary>Create on the UI thread. Ownership of connection transfers on successful construction.</summary>
    public PluginHost(
        IUiDispatcher dispatcher,
        IPluginConnection connection,
        PresentationTarget? target = null
    )
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(connection);
        if (!dispatcher.CheckAccess())
            throw new InvalidOperationException("Create the plugin host on the UI thread.");
        this.dispatcher = dispatcher;
        this.connection = connection;
        Target = target ?? PresentationTarget.Local;
        Channels = new(dispatcher, connection);
        // Timer does not capture the creating application's ambient execution context.
        if (ExecutionContext.IsFlowSuppressed())
            timer = CreateTimer();
        else
        {
            using (ExecutionContext.SuppressFlow())
                timer = CreateTimer();
        }
    }

    /// <summary>Enable static plugin calls for this host. Call once on its UI thread.
    /// Repeated calls on the same host are harmless; disposal unregisters it.</summary>
    public PluginHost RegisterDefault()
    {
        if (!dispatcher.CheckAccess())
            throw new InvalidOperationException("Register plugins on the UI thread.");
        _ = Context();
        registration ??= PluginRuntime.RegisterDefault(Context);
        return this;
    }

    private Timer CreateTimer() =>
        new(_ => QueuePoll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));

    private PluginRuntimeContext Context()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
        if (fault is { } error)
            throw new InvalidOperationException("The plugin connection failed.", error);
        return new(Channels, Target);
    }

    /// <summary>Run a callback in this session, including its asynchronous continuations.</summary>
    public async Task RunAsync(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!dispatcher.CheckAccess())
            throw new InvalidOperationException("Run plugin callbacks on the UI thread.");
        _ = Context();
        using var scope = PluginRuntime.Enter(Context);
        await callback();
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> callback)
    {
        T result = default!;
        await RunAsync(async () =>
        {
            result = await callback();
        });
        return result;
    }

    private void QueuePoll()
    {
        if (
            Volatile.Read(ref stopped) != 0
            || Volatile.Read(ref fault) != null
            || Interlocked.Exchange(ref queued, 1) != 0
        )
            return;
        try
        {
            dispatcher.Post(() =>
            {
                try
                {
                    if (Volatile.Read(ref stopped) == 0 && fault is null)
                        connection.DispatchReplies(Channels);
                }
                catch (Exception error)
                {
                    Fail(error);
                }
                finally
                {
                    Volatile.Write(ref queued, 0);
                }
            });
        }
        catch (Exception error)
        {
            Volatile.Write(ref queued, 0);
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.CompareExchange(ref fault, error, null) is not null)
            return;
        Channels.Dispose();
        // A failed dispatcher can report here on the timer thread. Subscribers must not touch UI directly.
        Faulted?.Invoke(error);
    }

    public void Dispose()
    {
        if (!dispatcher.CheckAccess())
            throw new InvalidOperationException("Dispose on the UI thread or use DisposeAsync.");
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;
        registration?.Dispose();
        timer.Dispose();
        Channels.Dispose();
        connection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref stopped) != 0)
            return ValueTask.CompletedTask;
        if (dispatcher.CheckAccess())
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        try
        {
            dispatcher.Post(() =>
            {
                try
                {
                    Dispose();
                    completion.SetResult();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            });
        }
        catch (Exception error)
        {
            completion.SetException(error);
        }
        return new(completion.Task);
    }
}
