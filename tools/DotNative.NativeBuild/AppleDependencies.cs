using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

// Shared source: publish and preview must resolve exactly the same dependency graph.
internal static class AppleDependencies
{
    internal sealed record Manifest(int SchemaVersion, Dependency[] Dependencies);

    internal sealed record Dependency(
        string Id,
        string Version,
        string Url,
        string Sha256,
        string[] Xcframeworks,
        string[]? ResourceBundles = null,
        string[]? SystemFrameworks = null
    );

    internal sealed record BundleInput(string Source, string Destination, bool Sign = false);

    internal sealed record Plan(
        string[] FrameworkPaths,
        string[] SystemFrameworks,
        BundleInput[] BundleInputs
    );

    private sealed record CacheStamp(Dictionary<string, string> Files);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExtractedBytes = 8L * 1024 * 1024 * 1024;
    internal static string DefaultCache =>
        Environment.GetEnvironmentVariable("DOTNATIVE_APPLE_CACHE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "dotnative",
            "apple"
        );

    internal static async Task<Plan> Resolve(
        string[] manifests,
        string rid,
        string cache,
        HttpClient? client = null,
        string minimumOS = "16.0"
    )
    {
        if (
            manifests.Length != 0
            && rid
                is not (
                    "ios-arm64"
                    or "iossimulator-arm64"
                    or "iossimulator-x64"
                    or "maccatalyst-arm64"
                    or "maccatalyst-x64"
                )
        )
            throw new NotSupportedException("Unsupported Apple dependency target: " + rid);
        var dependencies = new Dictionary<string, Dependency>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifests.Distinct().Order())
        {
            var manifest =
                JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException("Empty Apple manifest: " + file);
            if (manifest.SchemaVersion != 1 || manifest.Dependencies is null)
                throw new InvalidDataException("Unsupported Apple dependency manifest: " + file);
            foreach (var dependency in manifest.Dependencies)
            {
                Validate(dependency);
                if (dependencies.TryGetValue(dependency.Id, out var old))
                {
                    if (
                        old.Version != dependency.Version
                        || old.Url != dependency.Url
                        || !old.Sha256.Equals(dependency.Sha256, StringComparison.OrdinalIgnoreCase)
                    )
                        throw new InvalidDataException(
                            $"Conflicting Apple dependency '{dependency.Id}': {old.Version} versus {dependency.Version}. Pin one version, URL and SHA-256 across plugins."
                        );
                    dependencies[dependency.Id] = old with
                    {
                        Xcframeworks = old
                            .Xcframeworks.Concat(dependency.Xcframeworks)
                            .Distinct()
                            .Order()
                            .ToArray(),
                        ResourceBundles = (old.ResourceBundles ?? [])
                            .Concat(dependency.ResourceBundles ?? [])
                            .Distinct()
                            .Order()
                            .ToArray(),
                        SystemFrameworks = (old.SystemFrameworks ?? [])
                            .Concat(dependency.SystemFrameworks ?? [])
                            .Distinct()
                            .Order()
                            .ToArray(),
                    };
                }
                else
                {
                    dependencies[dependency.Id] = dependency with
                    {
                        Sha256 = dependency.Sha256.ToLowerInvariant(),
                        Xcframeworks = dependency.Xcframeworks.Distinct().Order().ToArray(),
                        ResourceBundles = (dependency.ResourceBundles ?? [])
                            .Distinct()
                            .Order()
                            .ToArray(),
                        SystemFrameworks = (dependency.SystemFrameworks ?? [])
                            .Distinct()
                            .Order()
                            .ToArray(),
                    };
                }
            }
        }
        using var ownedClient = client is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(10),
            }
            : null;
        client ??= ownedClient!;
        Directory.CreateDirectory(cache);
        var frameworks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bundles = new Dictionary<string, BundleInput>(StringComparer.OrdinalIgnoreCase);
        var system = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dependency in dependencies.Values.OrderBy(d => d.Id))
        {
            var digest = dependency.Sha256.ToLowerInvariant();
            // Locks are OS file locks, released on process exit (including interrupted builds).
            await using var held = await AcquireLock(Path.Combine(cache, digest + ".lock"));
            var archive = Path.Combine(cache, digest + ".zip");
            if (!File.Exists(archive))
            {
                var partial = archive + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    using var response = await client.GetAsync(
                        dependency.Url,
                        HttpCompletionOption.ResponseHeadersRead
                    );
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidDataException(
                            "Apple SDK download redirected away from HTTPS."
                        );
                    await using (var input = await response.Content.ReadAsStreamAsync())
                    await using (var output = File.Create(partial))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer)) != 0)
                        {
                            total += read;
                            if (total > MaxArchiveBytes)
                                throw new InvalidDataException(
                                    "Apple SDK archive exceeds size limit."
                                );
                            await output.WriteAsync(buffer.AsMemory(0, read));
                        }
                    }
                    VerifyArchive(partial, digest);
                    File.Move(partial, archive);
                }
                finally
                {
                    if (File.Exists(partial))
                        File.Delete(partial);
                }
            }
            VerifyArchive(archive, digest);
            var selection = Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dependency, Json))
                    )
                )
                .ToLowerInvariant();
            var selected = Path.Combine(cache, digest, rid, selection);
            if (!ValidCache(selected))
            {
                var stage = selected + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    Directory.CreateDirectory(stage);
                    ExtractSelected(archive, dependency, rid, stage);
                    WriteJson(
                        Path.Combine(stage, ".files.json"),
                        new CacheStamp(TreeFiles(stage, cache: true))
                    );
                    if (Directory.Exists(selected))
                        Directory.Delete(selected, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(selected)!);
                    Directory.Move(stage, selected);
                }
                finally
                {
                    if (Directory.Exists(stage))
                        Directory.Delete(stage, true);
                }
            }
            foreach (
                var framework in Directory
                    .GetDirectories(Path.Combine(selected, "frameworks"), "*.framework")
                    .Order()
            )
            {
                var name = Path.GetFileNameWithoutExtension(framework);
                if (
                    frameworks.TryGetValue(name, out var existing)
                    && Fingerprint(existing) != Fingerprint(framework)
                )
                    throw new InvalidDataException("Conflicting Apple framework binaries: " + name);
                frameworks[name] = framework;
                var binary = Path.Combine(framework, name);
                await Run(
                    "xcrun",
                    ["lipo", binary, "-verify_arch", rid.EndsWith("-x64") ? "x86_64" : "arm64"]
                );
                // Static SDK framework plists can contain placeholder deployment versions
                // (Firebase ships 100.0). Mach-O load commands describe the actual requirement.
                var loadCommands = await Run(
                    "xcrun",
                    ["otool", "-arch", rid.EndsWith("-x64") ? "x86_64" : "arm64", "-l", binary],
                    true
                );
                var requiredVersions = Regex
                    .Matches(
                        loadCommands,
                        @"cmd LC_BUILD_VERSION\s+cmdsize \d+\s+platform \S+\s+minos ([0-9.]+)|cmd LC_VERSION_MIN_IPHONEOS\s+cmdsize \d+\s+version ([0-9.]+)"
                    )
                    .Select(m =>
                        Version.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    );
                foreach (var required in requiredVersions)
                    if (required > Version.Parse(minimumOS))
                        throw new InvalidDataException(
                            $"{name} requires iOS {required}, app targets {minimumOS}."
                        );
                var kind = await Run("xcrun", ["otool", "-hv", binary], true);
                if (Regex.IsMatch(kind, @"\bDYLIB\b"))
                {
                    AddBundle(new(framework, "Frameworks/" + Path.GetFileName(framework), true));
                }
                else
                {
                    foreach (
                        var resource in Directory.GetDirectories(
                            framework,
                            "*.bundle",
                            SearchOption.AllDirectories
                        )
                    )
                        if (
                            !Path.GetRelativePath(framework, resource)
                                .Split(Path.DirectorySeparatorChar)
                                .SkipLast(1)
                                .Any(p => p.EndsWith(".bundle"))
                        )
                            AddBundle(new(resource, Path.GetFileName(resource)));
                    // Bare privacy manifests in static frameworks need a resource bundle, too.
                    var privacy = Path.Combine(framework, "PrivacyInfo.xcprivacy");
                    if (File.Exists(privacy))
                    {
                        var wrapper = Path.Combine(
                            selected,
                            "privacy",
                            name + "_DotNativePrivacy.bundle"
                        );
                        Directory.CreateDirectory(wrapper);
                        File.Copy(privacy, Path.Combine(wrapper, "PrivacyInfo.xcprivacy"), true);
                        File.WriteAllText(
                            Path.Combine(wrapper, "Info.plist"),
                            $"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>org.dotnative.privacy.{name}</string><key>CFBundlePackageType</key><string>BNDL</string></dict></plist>"
                        );
                        AddBundle(new(wrapper, Path.GetFileName(wrapper)));
                    }
                }
            }
            foreach (
                var resource in Directory.GetDirectories(
                    Path.Combine(selected, "resources"),
                    "*.bundle"
                )
            )
                AddBundle(new(resource, Path.GetFileName(resource)));
            foreach (var name in dependency.SystemFrameworks ?? [])
                system.Add(name);
        }
        return new(
            frameworks.Values.Order().ToArray(),
            system.ToArray(),
            bundles.Values.OrderBy(b => b.Destination).ToArray()
        );

        void AddBundle(BundleInput input)
        {
            if (
                bundles.TryGetValue(input.Destination, out var old)
                && Fingerprint(old.Source) != Fingerprint(input.Source)
            )
                throw new InvalidDataException(
                    "Conflicting Apple bundle destination: " + input.Destination
                );
            bundles[input.Destination] = input;
        }
    }

    private static void Validate(Dependency dependency)
    {
        if (
            string.IsNullOrWhiteSpace(dependency.Id)
            || string.IsNullOrWhiteSpace(dependency.Version)
            || !Regex.IsMatch(dependency.Sha256 ?? "", "^[a-fA-F0-9]{64}$")
            || !Uri.TryCreate(dependency.Url, UriKind.Absolute, out var url)
            || url.Scheme != "https"
            || dependency.Xcframeworks is null
            || dependency.Xcframeworks.Length == 0
        )
            throw new InvalidDataException(
                "Apple dependencies require an ID, pinned version, HTTPS URL, SHA-256 and explicit XCFramework paths."
            );
        foreach (var path in dependency.Xcframeworks)
        {
            SafeRelative(path);
            if (!path.EndsWith(".xcframework", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid XCFramework path: " + path);
        }
        foreach (var path in dependency.ResourceBundles ?? [])
        {
            SafeRelative(path);
            if (!path.EndsWith(".bundle", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid resource bundle: " + path);
        }
        foreach (var framework in dependency.SystemFrameworks ?? [])
            if (!Regex.IsMatch(framework, "^[A-Za-z][A-Za-z0-9_]*$"))
                throw new InvalidDataException("Invalid system framework: " + framework);
    }

    private static void ExtractSelected(
        string archive,
        Dependency dependency,
        string rid,
        string stage
    )
    {
        using var zip = ZipFile.OpenRead(archive);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            SafeRelative(entry.FullName.TrimEnd('/'));
            if (!entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("Duplicate ZIP entry: " + entry.FullName);
        }
        Directory.CreateDirectory(Path.Combine(stage, "frameworks"));
        Directory.CreateDirectory(Path.Combine(stage, "resources"));
        long total = 0;
        foreach (var path in dependency.Xcframeworks)
        {
            if (!entries.TryGetValue(path + "/Info.plist", out var metadata))
                throw new InvalidDataException("Missing XCFramework: " + path);
            if (metadata.Length > 1024 * 1024)
                throw new InvalidDataException("XCFramework metadata exceeds size limit.");
            using var bytes = new MemoryStream();
            using (var input = metadata.Open())
                input.CopyTo(bytes);
            var plist = ReadPlist(bytes.ToArray());
            var libraries = plist["AvailableLibraries"]
                .Elements("dict")
                .Select(Dict)
                .Where(d =>
                    d["SupportedPlatform"].Value == "ios"
                    && (d.GetValueOrDefault("SupportedPlatformVariant")?.Value ?? "")
                        == (
                            rid.StartsWith("iossimulator-") ? "simulator"
                            : rid.StartsWith("maccatalyst-") ? "maccatalyst"
                            : ""
                        )
                    && d["SupportedArchitectures"]
                        .Elements("string")
                        .Any(a => a.Value == (rid.EndsWith("-x64") ? "x86_64" : "arm64"))
                )
                .ToArray();
            if (libraries.Length != 1)
                throw new InvalidDataException(
                    $"Expected exactly one {rid} slice in {path}; found {libraries.Length}."
                );
            var slice = libraries[0];
            var identifier = slice["LibraryIdentifier"].Value;
            var library = slice["LibraryPath"].Value;
            SafeRelative(identifier);
            SafeRelative(library);
            if (!Regex.IsMatch(library, "^[A-Za-z][A-Za-z0-9_]*[.]framework$"))
                throw new InvalidDataException(
                    "Only framework XCFramework slices are supported: " + path
                );
            var target = Path.Combine(stage, "frameworks", library);
            var temporary = target + "." + Guid.NewGuid().ToString("N");
            var prefix = path + "/" + identifier + "/" + library + "/";
            // Catalyst distributions can use macOS-style versioned frameworks.
            // Flatten the selected version instead of extracting ZIP symlinks.
            if (
                rid.StartsWith("maccatalyst-")
                && entries.TryGetValue(prefix + "Versions/Current", out var current)
            )
            {
                if (current.Length > 128 || ((current.ExternalAttributes >> 16) & 0xf000) != 0xa000)
                    throw new InvalidDataException("Invalid framework version selector.");
                using var reader = new StreamReader(current.Open());
                var version = reader.ReadToEnd();
                if (!Regex.IsMatch(version, "^[A-Za-z0-9_-]+$"))
                    throw new InvalidDataException("Unsafe framework version selector.");
                CopyPrefix(prefix + "Versions/" + version + "/", temporary);
                var info = Path.Combine(temporary, "Resources/Info.plist");
                if (File.Exists(info))
                    File.Copy(info, Path.Combine(temporary, "Info.plist"));
            }
            else
                CopyPrefix(prefix, temporary);
            if (Directory.Exists(target))
            {
                if (Fingerprint(target) != Fingerprint(temporary))
                    throw new InvalidDataException("Conflicting framework in archive: " + library);
                Directory.Delete(temporary, true);
            }
            else
                Directory.Move(temporary, target);
        }
        foreach (var path in dependency.ResourceBundles ?? [])
        {
            var target = Path.Combine(stage, "resources", Path.GetFileName(path));
            var temporary = target + "." + Guid.NewGuid().ToString("N");
            CopyPrefix(path + "/", temporary);
            if (Directory.Exists(target))
            {
                if (Fingerprint(target) != Fingerprint(temporary))
                    throw new InvalidDataException("Conflicting resource in archive: " + path);
                Directory.Delete(temporary, true);
            }
            else
                Directory.Move(temporary, target);
        }

        void CopyPrefix(string prefix, string destination)
        {
            var files = entries
                .Values.Where(e =>
                    e.FullName.StartsWith(prefix, StringComparison.Ordinal)
                    && !e.FullName.EndsWith('/')
                )
                .ToArray();
            if (files.Length == 0)
                throw new InvalidDataException("Missing SDK files: " + prefix);
            foreach (var entry in files)
            {
                // iOS slices should contain real files, not macOS framework symlink layouts.
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException(
                        "Symlink in selected iOS SDK slice: " + entry.FullName
                    );
                total += entry.Length;
                if (total > MaxExtractedBytes)
                    throw new InvalidDataException("Apple SDK extracted data exceeds limit.");
                var file = Path.Combine(destination, entry.FullName[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using (var input = entry.Open())
                using (var output = File.Create(file))
                    input.CopyTo(output);
                var mode = (entry.ExternalAttributes >> 16) & 0x1ff;
                if (mode != 0 && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(file, (UnixFileMode)mode);
            }
        }
    }

    internal static void WritePlan(string output, Plan plan)
    {
        Directory.CreateDirectory(output);
        WriteJson(Path.Combine(output, "apple-dependencies.json"), plan);
        var items = new XElement("ItemGroup");
        foreach (var framework in plan.FrameworkPaths)
        {
            var dynamic = plan.BundleInputs.Any(b => b.Sign && b.Source == framework);
            items.Add(
                new XElement(
                    "NativeReference",
                    new XAttribute("Include", framework),
                    new XElement("Kind", "Framework"),
                    new XElement("SmartLink", "false"),
                    new XElement("ForceLoad", dynamic ? "false" : "true"),
                    new XElement("Frameworks", string.Join(" ", plan.SystemFrameworks))
                )
            );
        }
        foreach (var resource in plan.BundleInputs.Where(b => !b.Sign))
        foreach (var file in Directory.GetFiles(resource.Source, "*", SearchOption.AllDirectories))
            items.Add(
                new XElement(
                    "BundleResource",
                    new XAttribute("Include", file),
                    new XElement(
                        "LogicalName",
                        resource.Destination + "/" + Path.GetRelativePath(resource.Source, file)
                    )
                )
            );
        new XDocument(new XElement("Project", items)).Save(
            Path.Combine(output, "apple-dependencies.targets")
        );
        var assembly = Assembly.GetEntryAssembly()!.Location;
        var script =
            "#!/bin/bash\nset -euo pipefail\nexec dotnet "
            + Shell(assembly)
            + " --stage-apple-bundle "
            + Shell(output)
            + " \"$TARGET_BUILD_DIR/$WRAPPER_NAME\" \"${EXPANDED_CODE_SIGN_IDENTITY:--}\"\n";
        File.WriteAllText(Path.Combine(output, "plugin-bundle.sh"), script);
    }

    internal static async Task StageBundle(string output, string bundle, string identity)
    {
        var plan = JsonSerializer.Deserialize<Plan>(
            File.ReadAllText(Path.Combine(output, "apple-dependencies.json")),
            Json
        )!;
        bundle = Path.GetFullPath(bundle);
        if (!Directory.Exists(bundle))
            throw new DirectoryNotFoundException(bundle);
        var ownership = Path.Combine(bundle, ".dotnative-native-dependencies.json");
        var previous = File.Exists(ownership)
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(ownership))!
            : [];
        foreach (var path in previous.Concat(plan.BundleInputs.Select(b => b.Destination)))
        {
            ValidateDestination(path);
            var parent = Path.GetDirectoryName(Path.Combine(bundle, path));
            while (parent is not null && parent.Length >= bundle.Length)
            {
                if (Directory.Exists(parent) && new DirectoryInfo(parent).LinkTarget is not null)
                    throw new InvalidDataException("Symlink in app bundle destination: " + parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        foreach (var input in plan.BundleInputs)
        {
            var target = Path.Combine(bundle, input.Destination);
            if (
                (
                    File.Exists(target)
                    || Directory.Exists(target)
                    || Directory.Exists(target + ".dotnative-staging")
                ) && !previous.Contains(input.Destination)
            )
                throw new InvalidDataException(
                    "Native dependency would overwrite an app-owned resource: " + input.Destination
                );
        }
        // Journal ownership before mutation so an interrupted/failed stage can be retried.
        // All destinations were checked against app-owned resources above.
        WriteJson(
            ownership,
            previous.Concat(plan.BundleInputs.Select(b => b.Destination)).Distinct().ToArray()
        );
        foreach (
            var owned in previous.Concat(plan.BundleInputs.Select(b => b.Destination)).Distinct()
        )
        {
            var staging = Path.Combine(bundle, owned) + ".dotnative-staging";
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
        foreach (var old in previous.Where(p => !plan.BundleInputs.Any(b => b.Destination == p)))
        {
            var path = Path.Combine(bundle, old);
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        foreach (var input in plan.BundleInputs)
        {
            var target = Path.Combine(bundle, input.Destination);
            var staging = target + ".dotnative-staging";
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            try
            {
                CopyDirectory(input.Source, staging);
                if (input.Sign)
                {
                    var binary = Path.Combine(staging, Path.GetFileNameWithoutExtension(target));
                    var architectures = (
                        await Run("xcrun", ["lipo", "-archs", binary], true)
                    ).Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    if (architectures.Length > 1)
                    {
                        await Run(
                            "xcrun",
                            ["lipo", binary, "-thin", "arm64", "-output", binary + ".thin"]
                        );
                        File.Move(binary + ".thin", binary, true);
                    }
                    foreach (var development in new[] { "Headers", "Modules" })
                    {
                        var path = Path.Combine(staging, development);
                        if (Directory.Exists(path))
                            Directory.Delete(path, true);
                    }
                    await Run(
                        "codesign",
                        [
                            "--force",
                            "--sign",
                            string.IsNullOrEmpty(identity) ? "-" : identity,
                            "--timestamp=none",
                            staging,
                        ]
                    );
                }
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                Directory.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
            }
        }
        WriteJson(ownership, plan.BundleInputs.Select(b => b.Destination).ToArray());
    }

    private static void ValidateDestination(string path)
    {
        SafeRelative(path);
        if (
            !(path.EndsWith(".bundle") && !path.Contains('/'))
            && !(
                path.StartsWith("Frameworks/")
                && path.EndsWith(".framework")
                && path.Count(c => c == '/') == 1
            )
        )
            throw new InvalidDataException("Invalid app bundle destination: " + path);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static void SafeRelative(string path)
    {
        if (
            string.IsNullOrEmpty(path)
            || path.Contains('\\')
            || path.Contains(':')
            || path.Any(char.IsControl)
            || path.StartsWith('/')
            || path.Split('/').Any(p => p is ".." or "." or "")
        )
            throw new InvalidDataException("Unsafe archive/manifest path: " + path);
    }

    private static string Fingerprint(string directory) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(TreeFiles(directory))))
        );

    private static Dictionary<string, string> TreeFiles(string directory, bool cache = false) =>
        Directory
            .GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p =>
                !cache
                || (
                    Path.GetRelativePath(directory, p) != ".files.json"
                    && !Path.GetRelativePath(directory, p).StartsWith("privacy/")
                )
            )
            .Order(StringComparer.Ordinal)
            .ToDictionary(p => Path.GetRelativePath(directory, p), FileHash);

    private static string FileHash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static bool ValidCache(string path)
    {
        try
        {
            var stamp = JsonSerializer.Deserialize<CacheStamp>(
                File.ReadAllText(Path.Combine(path, ".files.json")),
                Json
            );
            return stamp is not null
                && JsonSerializer.Serialize(stamp.Files)
                    == JsonSerializer.Serialize(TreeFiles(path, cache: true));
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return false;
        }
    }

    private static void VerifyArchive(string path, string digest)
    {
        if (new FileInfo(path).Length > MaxArchiveBytes || FileHash(path) != digest)
            throw new InvalidDataException(
                "Apple SDK SHA-256 mismatch; remove corrupt cached archive and retry: " + path
            );
    }

    private static async Task<FileStream> AcquireLock(string path)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (true)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static Dictionary<string, XElement> ReadPlist(byte[] bytes)
    {
        if (Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 6)) == "bplist")
        {
            var temp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(temp, bytes);
                Run("plutil", ["-convert", "xml1", temp]).GetAwaiter().GetResult();
                bytes = File.ReadAllBytes(temp);
            }
            finally
            {
                File.Delete(temp);
            }
        }
        using var reader = XmlReader.Create(
            new MemoryStream(bytes),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }
        );
        return Dict(XDocument.Load(reader).Root!.Element("dict")!);
    }

    private static Dictionary<string, XElement> Dict(XElement element)
    {
        var items = element.Elements().ToArray();
        var result = new Dictionary<string, XElement>();
        for (var i = 0; i < items.Length; i += 2)
            result.Add(items[i].Value, items[i + 1]);
        return result;
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task<string> Run(string file, string[] arguments, bool capture = false)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = capture,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var text = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(file + " failed: " + process.ExitCode);
        return await text;
    }
}
