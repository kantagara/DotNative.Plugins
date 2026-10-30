namespace DotNative.Plugins;

/// <summary>The channels and destination platform belonging to one host session.</summary>
public sealed record PluginRuntimeContext(IPlatformChannels Channels, PresentationTarget Target);

/// <summary>Scoped plugin access with an explicitly registered default host. No UI framework dependency.</summary>
public static class PluginRuntime
{
    private static readonly AsyncLocal<Func<PluginRuntimeContext>?> Resolver = new();

    private static Registration? defaultHost;

    public static PluginRuntimeContext Current
    {
        get
        {
            // A captured, stopped scope must fail; never silently switch it to another host.
            if (Resolver.Value is { } scoped)
                return scoped();
            return Volatile.Read(ref defaultHost)?.Resolve()
                ?? throw new InvalidOperationException(
                    "Native plugins are not initialized. Register your framework's plugin host once at application startup."
                );
        }
    }

    /// <summary>Registers one default host. Dispose the token when that host stops.
    /// Scoped access takes precedence. A second default is rejected, never substituted.</summary>
    public static IDisposable RegisterDefault(Func<PluginRuntimeContext> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _ = resolver();
        var registration = new Registration(resolver);
        if (Interlocked.CompareExchange(ref defaultHost, registration, null) is not null)
            throw new InvalidOperationException(
                "A default plugin host is already registered. Dispose it before registering another host."
            );
        return registration;
    }

    private sealed class Registration(Func<PluginRuntimeContext> resolver) : IDisposable
    {
        private int disposed;

        public PluginRuntimeContext Resolve()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return resolver();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Interlocked.CompareExchange(ref defaultHost, null, this);
        }
    }

    /// <summary>
    /// Host integration hook. The resolver must reject a stopped session, including
    /// captured async continuations. Dispose the scope in the same execution context.
    /// </summary>
    public static IDisposable Enter(Func<PluginRuntimeContext> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var previous = Resolver.Value;
        Resolver.Value = resolver;
        return new Scope(previous);
    }

    private sealed class Scope(Func<PluginRuntimeContext>? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            Resolver.Value = previous;
        }
    }
}
