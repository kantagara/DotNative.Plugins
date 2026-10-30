using System.Text;

namespace DotNative.Plugins;

/// <summary>Named service channels shared by native plugins and C# callers.</summary>
public static class NamedChannels
{
    public static ChannelId GetId(string name, uint version = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (version == 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        var id = ChannelIdentity.Hash(Encoding.UTF8.GetBytes($"{name}/v{version}"));
        return id != 0
            ? new(id)
            : throw new ArgumentException(
                "Channel identity hashes to the reserved ID.",
                nameof(name)
            );
    }

    public static MethodChannel Get(
        this IPlatformChannels channels,
        string name,
        uint version = 1
    ) => channels.Get(GetId(name, version));
}
