using DotNative.Plugins;

namespace DotNative.Firebase.RemoteConfig;

/// <summary>Firebase Remote Config without dependency injection. Uses the current host
/// on each call, so no service or channel survives an application restart.</summary>
public static class RemoteConfig
{
    private static FirebaseRemoteConfig Client => new(PluginRuntime.Current.Channels);

    public static Task InitializeAsync(
        FirebaseOptions options,
        CancellationToken cancellationToken = default
    ) => Client.InitializeAsync(options, cancellationToken);

    public static Task SetDefaultsAsync(
        IReadOnlyDictionary<string, string> defaults,
        CancellationToken cancellationToken = default
    ) => Client.SetDefaultsAsync(defaults, cancellationToken);

    public static Task FetchAsync(CancellationToken cancellationToken = default) =>
        Client.FetchAsync(cancellationToken);

    public static Task<string> GetStringAsync(
        string key,
        CancellationToken cancellationToken = default
    ) => Client.GetStringAsync(key, cancellationToken);
}
