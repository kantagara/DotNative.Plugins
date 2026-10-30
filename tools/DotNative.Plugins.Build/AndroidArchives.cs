using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class AndroidArchives
{
    internal static string WithoutHostClasses(string path, HashSet<string> supplied, string output)
    {
        Directory.CreateDirectory(output);
        var destination = Path.Combine(output, Path.GetFileName(path));
        using var input = ZipFile.OpenRead(path);
        if (File.Exists(destination))
            File.Delete(destination);
        using var target = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            if (supplied.Contains(entry.FullName))
                continue;
            // A modified JAR cannot retain its old signing metadata.
            if (
                entry.FullName.StartsWith("META-INF/", StringComparison.Ordinal)
                && Path.GetExtension(entry.FullName).ToUpperInvariant() is ".SF" or ".RSA" or ".DSA"
            )
                continue;
            var copy = target.CreateEntry(entry.FullName);
            using var source = entry.Open();
            using var sink = copy.Open();
            source.CopyTo(sink);
        }
        return destination;
    }

    internal static HashSet<string> Classes(string path)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return classes;
        using var stream = File.OpenRead(path);
        if (Path.GetExtension(path) != ".dll")
        {
            if (Path.GetExtension(path) is ".aar" or ".jar")
                ReadArchive(stream, classes);
            return classes;
        }
        // Modern .NET Android bindings put the Java payload beside the assembly.
        foreach (var extension in new[] { ".aar", ".jar" })
            classes.UnionWith(Classes(Path.ChangeExtension(path, extension)));
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } header)
            return classes;
        var metadata = pe.GetMetadataReader();
        if (header.ResourcesDirectory.Size == 0)
            return classes;
        var resources = pe.GetSectionData(header.ResourcesDirectory.RelativeVirtualAddress)
            .GetContent();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil)
                continue;
            var name = metadata.GetString(resource.Name);
            if (
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".aar", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            var offset = checked((int)resource.Offset);
            var length = BinaryPrimitives.ReadInt32LittleEndian(resources.AsSpan(offset, 4));
            using var archive = new MemoryStream(resources.AsSpan(offset + 4, length).ToArray());
            ReadArchive(archive, classes);
        }
        return classes;
    }

    private static void ReadArchive(Stream stream, HashSet<string> classes)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (
                entry.FullName.EndsWith(".class", StringComparison.Ordinal)
                && !entry.FullName.StartsWith("META-INF/", StringComparison.Ordinal)
                && !entry.FullName.EndsWith("/module-info.class", StringComparison.Ordinal)
            )
                classes.Add(entry.FullName);
            else if (
                entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".aar", StringComparison.OrdinalIgnoreCase)
            )
            {
                using var input = entry.Open();
                using var nested = new MemoryStream();
                input.CopyTo(nested);
                nested.Position = 0;
                ReadArchive(nested, classes);
            }
        }
    }
}
