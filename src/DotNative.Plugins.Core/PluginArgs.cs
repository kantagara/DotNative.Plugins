namespace DotNative.Plugins;

/// <summary>Creates named channel arguments without reflection or JSON serialization.</summary>
public static class PluginArgs
{
    /// <summary>Names must be unique. Values use the types supported by PluginCodec.</summary>
    public static Dictionary<string, object?> Create(params (string Name, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var arguments = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
        foreach (var (name, value) in fields)
            arguments.Add(name, value);
        return arguments;
    }
}
