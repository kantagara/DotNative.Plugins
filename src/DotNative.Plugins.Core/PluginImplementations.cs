using System.Collections.Concurrent;

namespace DotNative.Plugins;

/// <summary>Application-owned implementations of plugin service interfaces.
/// Factories run on the caller's thread; the application owns returned services.</summary>
public static class PluginImplementations
{
    private static readonly ConcurrentDictionary<Type, object> Factories = new();

    /// <summary>Register once at startup, before calling the plugin. The token removes
    /// this override; it does not dispose services created by the factory.</summary>
    public static IDisposable Register<T>(Func<T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var registration = new Registration<T>(factory);
        if (!Factories.TryAdd(typeof(T), registration))
            throw new InvalidOperationException($"An implementation of {typeof(T).Name} is already registered.");
        return registration;
    }

    /// <summary>Resolve per operation; never retain a previous host's native channels.</summary>
    public static T Resolve<T>(Func<T> nativeFactory) where T : class
    {
        ArgumentNullException.ThrowIfNull(nativeFactory);
        var service = Factories.TryGetValue(typeof(T), out var value)
            ? ((Registration<T>)value).Factory()
            : nativeFactory();
        return service ?? throw new InvalidOperationException($"The {typeof(T).Name} factory returned null.");
    }

    private sealed class Registration<T>(Func<T> factory) : IDisposable where T : class
    {
        internal Func<T> Factory { get; } = factory;
        public void Dispose() => ((ICollection<KeyValuePair<Type, object>>)Factories)
            .Remove(new(typeof(T), this));
    }
}
