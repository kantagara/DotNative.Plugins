using System.Buffers.Binary;
using DotNative;
using DotNative.Plugins;

internal static class EventChannelTests
{
    public static void Run() =>
        Exercise().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    private static async Task Exercise()
    {
        var transport = new Transport();
        using var channels = new PlatformChannels(new ImmediateDispatcher(), transport);
        transport.Owner = channels;
        var events = new EventChannel(channels, "fixture/events");
        var another = new EventChannel(channels, "fixture/events");
        await using (var first = events.ReadAllAsync().GetAsyncEnumerator())
        {
            if (!await first.MoveNextAsync() || (long)first.Current! != 17)
                throw new Exception("Wrong stream value");
            await using var duplicate = another.ReadAllAsync().GetAsyncEnumerator();
            try
            {
                await duplicate.MoveNextAsync();
                throw new Exception("Duplicate consumer accepted");
            }
            catch (PluginException error) when (error.Code == "busy") { }
            if (transport.Cancellations != 0)
                throw new Exception("Rejected consumer cancelled active subscription");
        }
        if (transport.Cancellations != 1)
            throw new Exception("Iterator disposal did not cancel");
        using var cancellation = new CancellationTokenSource();
        transport.HoldListen = true;
        await using (
            var pending = events
                .ReadAllAsync(cancellationToken: cancellation.Token)
                .GetAsyncEnumerator()
        )
        {
            var read = pending.MoveNextAsync().AsTask();
            cancellation.Cancel();
            try
            {
                await read;
                throw new Exception("Cancelled listen completed");
            }
            catch (OperationCanceledException) { }
        }
        if (transport.Cancellations != 2)
            throw new Exception("Cancelled listen leaked native subscription");
        transport.HoldListen = false;
        await foreach (var value in another.ReadAllAsync())
            break;
        if (transport.Cancellations != 3)
            throw new Exception("Gate not reusable after cancellation");
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class Transport : IPluginTransport
    {
        public PlatformChannels Owner = null!;
        public int Cancellations;
        public bool HoldListen;

        public void SendPlugin(ReadOnlySpan<byte> request)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(request[12..]) != 1)
                return;
            var call = (Dictionary<string, object?>)PluginCodec.Decode(request[16..])!;
            var method = (string)call["method"]!;
            if (method == "listen" && HoldListen)
                return;
            if (method == "cancel")
                Cancellations++;
            var payload = PluginCodec.Encode(
                method == "next"
                    ? new Dictionary<string, object?> { ["event"] = "data", ["value"] = 17L }
                    : null
            );
            var packet = new byte[payload.Length + 12];
            request[..8].CopyTo(packet);
            payload.CopyTo(packet, 12);
            Owner.AcceptReply(packet);
        }
    }
}
