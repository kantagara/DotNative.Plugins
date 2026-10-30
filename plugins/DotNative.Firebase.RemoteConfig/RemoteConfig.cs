using DotNative.Plugins;

namespace DotNative.Firebase.RemoteConfig;

public sealed record FirebaseOptions(
    string ApiKey,
    string ApplicationId,
    string ProjectId,
    string MessagingSenderId
);

public sealed class FirebaseRemoteConfig(IPlatformChannels channels)
{
    private MethodChannel Channel => channels.Get("dotnative.firebase.remoteconfig");

    public Task InitializeAsync(
        FirebaseOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (
            var value in new[]
            {
                options.ApiKey,
                options.ApplicationId,
                options.ProjectId,
                options.MessagingSenderId,
            }
        )
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Channel.InvokeAsync(
            "initialize",
            PluginArgs.Create(
                ("apiKey", options.ApiKey),
                ("applicationId", options.ApplicationId),
                ("projectId", options.ProjectId),
                ("senderId", options.MessagingSenderId)
            ),
            cancellationToken
        );
    }

    public Task SetDefaultsAsync(
        IReadOnlyDictionary<string, string> defaults,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return Channel.InvokeAsync(
            "setDefaults",
            defaults.ToDictionary(p => p.Key, p => (object?)p.Value),
            cancellationToken
        );
    }

    public Task FetchAsync(CancellationToken cancellationToken = default) =>
        Channel.InvokeAsync("fetch", cancellationToken: cancellationToken);

    public async Task<string> GetStringAsync(
        string key,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var result = await Channel
            .InvokeAsync("getString", PluginArgs.Create(("key", key)), cancellationToken)
            .ConfigureAwait(false);
        return result as string
            ?? throw new InvalidDataException("Firebase returned a non-string result.");
    }
}
