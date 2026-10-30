using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DotNative.Plugins;

/// <summary>A native event subscription. One active consumer per channel/session.</summary>
public sealed class EventChannel
{
    private static readonly ConditionalWeakTable<
        IPlatformChannels,
        ConcurrentDictionary<uint, SemaphoreSlim>
    > Gates = new();
    private readonly MethodChannel channel;
    private readonly SemaphoreSlim gate;

    public EventChannel(IPlatformChannels channels, string name)
        : this(channels, name, 1) { }

    public EventChannel(IPlatformChannels channels, string name, uint version)
        : this(channels, NamedChannels.GetId(name, version)) { }

    public EventChannel(IPlatformChannels channels, ChannelId id)
    {
        channel = channels.Get(id);
        gate = Gates.GetOrCreateValue(channels).GetOrAdd(id.Value, _ => new SemaphoreSlim(1, 1));
    }

    public async IAsyncEnumerable<object?> ReadAllAsync(
        object? arguments = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!gate.Wait(0))
            throw new PluginException("busy", "Event channel already has a consumer.");
        var listening = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            listening = true;
            try
            {
                await channel
                    .InvokeAsync("listen", arguments, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PluginException error) when (error.Code == "busy")
            {
                listening = false;
                throw;
            }
            while (true)
            {
                var response = await channel
                    .InvokeAsync("next", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (
                    response is not Dictionary<string, object?> packet
                    || packet.GetValueOrDefault("event") is not string kind
                )
                    throw new InvalidDataException("Invalid native event envelope.");
                if (kind == "end")
                    yield break;
                if (kind == "error")
                    throw new PluginException(
                        packet.GetValueOrDefault("code") as string ?? "stream_error",
                        packet.GetValueOrDefault("message") as string ?? "",
                        packet.GetValueOrDefault("details")
                    );
                if (kind != "data")
                    throw new InvalidDataException("Unknown native event envelope.");
                yield return packet.GetValueOrDefault("value");
            }
        }
        finally
        {
            try
            {
                if (listening)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await channel
                            .InvokeAsync("cancel", arguments, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error)
                        when (error
                                is OperationCanceledException
                                    or ObjectDisposedException
                                    or PluginException
                                    or IOException
                        ) { }
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
