using System.Text;

namespace DotNative.Plugins;

/// <summary>Bounded binary values shared by plugin requests and replies. No reflection or JSON.</summary>
public static class PluginCodec
{
    public const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        Write(writer, value, 0);
        if (stream.Length > MaximumBytes)
            throw new InvalidDataException("Plugin message is too large.");
        return stream.ToArray();
    }

    public static object? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("Plugin message is too large.");
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = new BinaryReader(stream, Utf8);
        var result = Read(reader, 0);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Trailing plugin data.");
        return result;
    }

    private static void Write(BinaryWriter writer, object? value, int depth)
    {
        if (depth > 16 || writer.BaseStream.Length > MaximumBytes)
            throw new InvalidDataException("Plugin value limit exceeded.");
        switch (value)
        {
            case null:
                writer.Write((byte)0);
                break;
            case bool b:
                writer.Write((byte)(b ? 2 : 1));
                break;
            case int n:
                writer.Write((byte)3);
                writer.Write((long)n);
                break;
            case long n:
                writer.Write((byte)3);
                writer.Write(n);
                break;
            case double number:
                writer.Write((byte)8);
                writer.Write(number);
                break;
            case float number:
                writer.Write((byte)8);
                writer.Write((double)number);
                break;
            case string s:
                if (Utf8.GetByteCount(s) > MaximumBytes - writer.BaseStream.Length - 5)
                    throw new InvalidDataException("Plugin string is too large.");
                var text = Utf8.GetBytes(s);
                if (text.Length > MaximumBytes)
                    throw new InvalidDataException("Plugin string is too large.");
                writer.Write((byte)4);
                writer.Write(text.Length);
                writer.Write(text);
                break;
            case byte[] data:
                if (data.Length > MaximumBytes - writer.BaseStream.Length - 5)
                    throw new InvalidDataException("Plugin bytes are too large.");
                writer.Write((byte)5);
                writer.Write(data.Length);
                writer.Write(data);
                break;
            case IReadOnlyDictionary<string, object?> map:
                if (map.Count > 4096)
                    throw new InvalidDataException("Plugin map is too large.");
                writer.Write((byte)6);
                writer.Write(map.Count);
                foreach (var entry in map)
                {
                    Write(writer, entry.Key, depth + 1);
                    Write(writer, entry.Value, depth + 1);
                }
                break;
            case IReadOnlyList<object?> list:
                if (list.Count > 4096)
                    throw new InvalidDataException("Plugin list is too large.");
                writer.Write((byte)7);
                writer.Write(list.Count);
                foreach (var entry in list)
                    Write(writer, entry, depth + 1);
                break;
            default:
                throw new ArgumentException($"Unsupported plugin value: {value.GetType().Name}");
        }
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException("Invalid plugin value length.");
        return count;
    }

    private static byte[] Bytes(BinaryReader reader)
    {
        int count = Count(reader, MaximumBytes);
        var data = reader.ReadBytes(count);
        if (data.Length != count)
            throw new EndOfStreamException();
        return data;
    }

    private static object? Read(BinaryReader reader, int depth)
    {
        if (depth > 16)
            throw new InvalidDataException("Plugin nesting limit exceeded.");
        switch (reader.ReadByte())
        {
            case 0:
                return null;
            case 1:
                return false;
            case 2:
                return true;
            case 3:
                return reader.ReadInt64();
            case 8:
                return reader.ReadDouble();
            case 4:
                return Utf8.GetString(Bytes(reader));
            case 5:
                return Bytes(reader);
            case 6:
                int entries = Count(reader, 4096);
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < entries; i++)
                {
                    if (
                        Read(reader, depth + 1) is not string key
                        || !map.TryAdd(key, Read(reader, depth + 1))
                    )
                        throw new InvalidDataException("Invalid or duplicate plugin map key.");
                }
                return map;
            case 7:
                var list = new object?[Count(reader, 4096)];
                for (int i = 0; i < list.Length; i++)
                    list[i] = Read(reader, depth + 1);
                return list;
            default:
                throw new InvalidDataException("Unknown plugin value tag.");
        }
    }
}
