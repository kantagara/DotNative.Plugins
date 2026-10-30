using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNative.Plugins;

/// <summary>
/// Standalone C ABI transport. One active native session per process; registration survives reset.
/// Create/dispose on the OS UI thread. Does not load or initialize a DotNative renderer.
/// </summary>
public sealed unsafe class NativePluginConnection : IPluginConnection
{
    private static readonly object Gate = new();
    private static bool active;
    private static readonly Dictionary<string, nint> Libraries = new(StringComparer.Ordinal);
    private static bool embeddedRegistered;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly bool embedded;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, int> send;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, nuint> poll;
    private readonly delegate* unmanaged[Cdecl]<int> reset;
    private byte[] buffer = new byte[4096];
    private bool disposed;

    /// <param name="libraryPath">Combined desktop plugin library, or null for an iOS statically linked library.</param>
    public NativePluginConnection(string? libraryPath = null)
        : this(libraryPath, 0) { }

    public NativePluginConnection(string? libraryPath, nint nativeWindow)
    {
        lock (Gate)
        {
            if (active)
                throw new InvalidOperationException(
                    "Only one standalone native plugin session may be active per process."
                );
            embedded = libraryPath is null;
            if (embedded)
            {
                if (!OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst())
                    throw new PlatformNotSupportedException(
                        "Supply the combined native plugin library path on desktop."
                    );
                if (Embedded.Reset() != 0)
                    throw new InvalidOperationException("Native plugins require the OS UI thread.");
                if (!embeddedRegistered)
                {
                    Embedded.Register();
                    embeddedRegistered = true;
                }
            }
            else
            {
                var path = OperatingSystem.IsAndroid()
                    ? libraryPath!
                    : Path.GetFullPath(libraryPath!);
                var fresh = !Libraries.TryGetValue(path, out var handle);
                if (fresh)
                    handle = OperatingSystem.IsAndroid()
                        ? NativeLibrary.Load(path, typeof(NativePluginConnection).Assembly, null)
                        : NativeLibrary.Load(path);
                // Native plugin registrations can retain callbacks forever. Keep successful libraries loaded.
                try
                {
                    send = (delegate* unmanaged[Cdecl]<byte*, nuint, int>)
                        NativeLibrary.GetExport(handle, "dotnative_send_plugin");
                    poll = (delegate* unmanaged[Cdecl]<byte*, nuint, nuint>)
                        NativeLibrary.GetExport(handle, "dotnative_poll_plugin");
                    reset = (delegate* unmanaged[Cdecl]<int>)
                        NativeLibrary.GetExport(handle, "dotnative_plugins_reset");
                    var register = (delegate* unmanaged[Cdecl]<void>)
                        NativeLibrary.GetExport(handle, "dotnative_register_plugins");
                    if (reset() != 0)
                        throw new InvalidOperationException(
                            "Native plugins require the OS UI thread."
                        );
                    if (
                        NativeLibrary.TryGetExport(
                            handle,
                            "dotnative_plugins_attach",
                            out var attach
                        )
                    )
                    {
                        if (fresh)
                            Libraries.Add(path, handle);
                        if (((delegate* unmanaged[Cdecl]<nint, int>)attach)(nativeWindow) != 0)
                            throw new InvalidOperationException(
                                "Native plugin registration failed."
                            );
                    }
                    else if (fresh)
                    {
                        Libraries.Add(path, handle);
                        register();
                    }
                }
                catch
                {
                    if (fresh && !Libraries.ContainsKey(path))
                        NativeLibrary.Free(handle);
                    throw;
                }
            }
            active = true;
        }
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Native plugin connection requires its UI thread.");
    }

    public void SendPlugin(ReadOnlySpan<byte> message)
    {
        Verify();
        fixed (byte* data = message)
            if (
                (
                    embedded
                        ? Embedded.Send(data, (nuint)message.Length)
                        : send(data, (nuint)message.Length)
                ) != 0
            )
                throw new PluginException("transport", "Native plugin request was rejected.");
    }

    public void DispatchReplies(PlatformChannels channels)
    {
        Verify();
        for (var i = 0; i < 256; i++)
        {
            nuint size;
            fixed (byte* data = buffer)
                size = embedded
                    ? Embedded.Poll(data, (nuint)buffer.Length)
                    : poll(data, (nuint)buffer.Length);
            if (size == 0)
                return;
            if (size < 13 || size > PluginCodec.MaximumBytes + 12)
                throw new InvalidDataException("Invalid native plugin reply size.");
            if (size > (nuint)buffer.Length)
            {
                Array.Resize(ref buffer, (int)size);
                i--;
                continue;
            }
            channels.AcceptReply(buffer.AsSpan(0, (int)size));
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Verify();
        // Keep the lease if reset fails; another host must not inherit live operations.
        if ((embedded ? Embedded.Reset() : reset()) != 0)
            throw new InvalidOperationException("Native plugin reset failed.");
        disposed = true;
        lock (Gate)
            active = false;
    }
}

internal static partial class Embedded
{
    [LibraryImport("__Internal", EntryPoint = "dotnative_register_plugins")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Register();

    [LibraryImport("__Internal", EntryPoint = "dotnative_plugins_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Reset();

    [LibraryImport("__Internal", EntryPoint = "dotnative_send_plugin")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Send(byte* data, nuint length);

    [LibraryImport("__Internal", EntryPoint = "dotnative_poll_plugin")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial nuint Poll(byte* data, nuint length);
}
