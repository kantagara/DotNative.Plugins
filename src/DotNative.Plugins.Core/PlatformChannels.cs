using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace DotNative.Plugins;

public readonly record struct ChannelId(uint Value);

public interface IPluginTransport
{
    void SendPlugin(ReadOnlySpan<byte> message);
}

public interface IPlatformChannels
{
    MethodChannel Get(ChannelId id);
}

public sealed class PluginException(string code, string message, object? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

public sealed class MethodChannel
{
    private readonly PlatformChannels owner;
    private readonly ChannelId id;

    internal MethodChannel(PlatformChannels owner, ChannelId id) =>
        (this.owner, this.id) = (owner, id);

    public Task<object?> InvokeMethodAsync(
        string method,
        object? arguments = null,
        CancellationToken cancellationToken = default
    ) => owner.InvokeAsync(id, method, arguments, cancellationToken);

    public Task<object?> InvokeAsync(
        string method,
        object? arguments = null,
        CancellationToken cancellationToken = default
    ) => owner.InvokeAsync(id, method, arguments, cancellationToken);
}

/// <summary>Owns requests for one application session; replies can arrive from any thread.</summary>
public sealed class PlatformChannels(IUiDispatcher dispatcher, IPluginTransport? transport)
    : IPlatformChannels,
        IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<object?>> _pending = new();
    private static long _nextId;
    private readonly SemaphoreSlim _slots = new(256, 256);
    private int _disposed;

    public MethodChannel Get(ChannelId id) =>
        id.Value != 0 ? new(this, id) : throw new ArgumentOutOfRangeException(nameof(id));

    internal async Task<object?> InvokeAsync(
        ChannelId channel,
        string method,
        object? arguments,
        CancellationToken cancellation
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(method);
        cancellation.ThrowIfCancellationRequested();
        if (transport is null)
            throw new PluginException("unavailable", "This host does not support plugins.");
        var payload = PluginCodec.Encode(
            new Dictionary<string, object?> { ["method"] = method, ["arguments"] = arguments }
        );
        var id = Interlocked.Increment(ref _nextId);
        if (id <= 0)
            throw new InvalidOperationException("Plugin request ID exhausted.");
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (!_slots.Wait(0))
            throw new PluginException("busy", "Too many pending plugin calls.");
        _pending.TryAdd(id, completion);
        try
        {
            dispatcher.Post(() =>
            {
                if (!_pending.ContainsKey(id))
                    return;
                try
                {
                    transport.SendPlugin(Packet(id, channel, 1, payload));
                }
                catch (Exception error)
                {
                    if (_pending.TryRemove(id, out var call))
                        call.TrySetException(error);
                }
            });
            using var registration = cancellation.Register(() =>
            {
                if (!_pending.TryRemove(id, out var call))
                    return;
                call.TrySetCanceled(cancellation);
                try
                {
                    dispatcher.Post(() =>
                    {
                        try
                        {
                            transport.SendPlugin(Packet(id, channel, 2, []));
                        }
                        catch
                        { /* Caller is already cancelled. */
                        }
                    });
                }
                catch
                { /* A stopped dispatcher cannot deliver cancellation. */
                }
            });
            if (Volatile.Read(ref _disposed) != 0)
                DisposePending();
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _slots.Release();
        }
    }

    private static byte[] Packet(long id, ChannelId channel, uint kind, byte[] payload)
    {
        var message = new byte[16 + payload.Length];
        BinaryPrimitives.WriteInt64LittleEndian(message, id);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), channel.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), kind);
        payload.CopyTo(message, 16);
        return message;
    }

    public void AcceptReply(ReadOnlySpan<byte> message)
    {
        if (message.Length < 13 || message.Length > PluginCodec.MaximumBytes + 12)
            throw new InvalidDataException("Invalid plugin reply size.");
        var id = BinaryPrimitives.ReadInt64LittleEndian(message);
        var status = BinaryPrimitives.ReadUInt32LittleEndian(message[8..]);
        if (id <= 0 || status > 2)
            throw new InvalidDataException("Invalid plugin reply header.");
        var value = PluginCodec.Decode(message[12..]);
        Exception? error = null;
        if (status == 1)
        {
            if (
                value is not Dictionary<string, object?> fields
                || !fields.TryGetValue("code", out var code)
                || code is not string c
                || !fields.TryGetValue("message", out var text)
                || text is not string m
            )
                throw new InvalidDataException("Invalid plugin error.");
            error = new PluginException(c, m, fields.GetValueOrDefault("details"));
        }
        if (!_pending.TryRemove(id, out var completion))
            return; // Cancelled or already answered.
        if (error is not null)
            completion.TrySetException(error);
        else if (status == 2)
            completion.TrySetCanceled();
        else
            completion.TrySetResult(value);
    }

    private void DisposePending()
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var call))
                call.TrySetException(new ObjectDisposedException(nameof(PlatformChannels)));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        DisposePending();
    }
}
