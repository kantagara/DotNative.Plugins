using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

try
{
    if (args.Length == 4 && args[0] == "--stage-apple-bundle")
    {
        await AppleDependencies.StageBundle(args[1], args[2], args[3]);
        return;
    }
    if (args.Length != 4)
        throw new ArgumentException(
            "Expected output directory, RID, minimum OS, native SDK directory."
        );
    var output = Path.GetFullPath(args[0]);
    var rid = args[1];
    var sdkSources = Path.GetFullPath(args[3]);
    string[] Entries(string name) =>
        File.Exists(Path.Combine(output, name))
            ? File.ReadAllLines(Path.Combine(output, name))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToArray()
            : [];
    var sources = Entries("plugin-sources.txt");
    var entries = Entries("plugin-entries.txt");
    foreach (var entry in entries)
        if (!Regex.IsMatch(entry, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidDataException("Invalid plugin entry.");
    if (rid == "linux-bionic-arm64")
    {
        var generated = Path.Combine(output, "plugin-java");
        if (Directory.Exists(generated))
            Directory.Delete(generated, true);
        Directory.CreateDirectory(generated);
        if (entries.Length > 0)
        {
            foreach (
                var source in sources.Concat(
                    Directory.GetFiles(Path.Combine(sdkSources, "android"), "*.kt")
                )
            )
            {
                if (Path.GetExtension(source) is not (".java" or ".kt"))
                    throw new InvalidDataException("Invalid Android plugin source.");
                var sourceDirectory = Path.Combine(
                    generated,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24]
                );
                Directory.CreateDirectory(sourceDirectory);
                var dest = Path.Combine(sourceDirectory, Path.GetFileName(source));
                if (File.Exists(dest))
                    throw new InvalidDataException("Duplicate Android source filename: " + source);
                File.Copy(source, dest);
            }
            File.WriteAllText(
                Path.Combine(generated, "GeneratedPlugins.kt"),
                "package com.dotnative.plugins\nimport android.app.Activity\nobject GeneratedPlugins {\n@JvmStatic fun register(activity: Activity) {\nNativeChannels.connectHost()\n"
                    + string.Join("\n", entries.Select(e => $"{e}(activity)"))
                    + "\n}\n}\n"
            );
        }
        // Recreate on every publish so removed packages leave no stale dependencies.
        File.WriteAllText(
            Path.Combine(output, "plugin-dependencies.gradle"),
            string.Join("\n", Entries("plugin-gradle.txt").Select(File.ReadAllText))
        );
        return;
    }
    var appleDependencies = await AppleDependencies.Resolve(
        Entries("plugin-apple-manifests.txt"),
        rid,
        AppleDependencies.DefaultCache,
        minimumOS: args[2]
    );
    AppleDependencies.WritePlan(output, appleDependencies);
    var (sdk, triple) = rid switch
    {
        "ios-arm64" => ("iphoneos", $"arm64-apple-ios{args[2]}"),
        "iossimulator-arm64" => ("iphonesimulator", $"arm64-apple-ios{args[2]}-simulator"),
        "iossimulator-x64" => ("iphonesimulator", $"x86_64-apple-ios{args[2]}-simulator"),
        "maccatalyst-arm64" => ("macosx", $"arm64-apple-ios{args[2]}-macabi"),
        "maccatalyst-x64" => ("macosx", $"x86_64-apple-ios{args[2]}-macabi"),
        "osx-arm64" => ("macosx", "arm64-apple-macos13.0"),
        _ => throw new NotSupportedException(rid),
    };
    var sysroot = (await Run("xcrun", ["--sdk", sdk, "--show-sdk-path"], true)).Trim();
    var objects = Path.Combine(output, "plugin-objects");
    Directory.CreateDirectory(objects);
    var link = new List<string>();
    var swift = new List<string>();
    var searchPaths = FrameworkSearchPaths();

    foreach (var source in sources)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException(source);
        var ext = Path.GetExtension(source);
        if (ext == ".swift")
        {
            swift.Add(source);
            continue;
        }
        if (ext is not (".c" or ".m" or ".mm" or ".cc" or ".cpp"))
            throw new InvalidDataException("Unsupported source: " + source);
        var obj = Path.Combine(
            objects,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24] + ".o"
        );
        var command = new List<string>
        {
            "--sdk",
            sdk,
            "clang",
            "-target",
            triple,
            "-isysroot",
            sysroot,
            "-O2",
            "-fPIC",
            "-c",
            source,
            "-o",
            obj,
        };
        if (ext is ".m" or ".mm")
            command.Add("-fobjc-arc");
        if (ext is ".mm" or ".cc" or ".cpp")
            command.Add("-std=c++17");
        await Run("xcrun", command.ToArray());
        link.Add(Quote(obj));
    }
    if (swift.Count > 0)
    {
        swift.AddRange(Directory.GetFiles(Path.Combine(sdkSources, "apple"), "*.swift"));
        var registration = Path.Combine(objects, "Registration.swift");
        File.WriteAllText(
            registration,
            "@_cdecl(\"dotnative_register_plugins\")\n@MainActor public func registerPlugins() {\n    connectPluginTransportToHost()\n"
                + string.Join("\n", entries.Select(e => $"{e}.register()"))
                + "\n}\n"
        );
        swift.Add(registration);
        var obj = Path.Combine(objects, "Plugins.o");
        var swiftArgs = new List<string>
        {
            "swiftc",
            "-swift-version",
            "5",
            "-parse-as-library",
            "-whole-module-optimization",
            "-O",
            "-emit-object",
            "-target",
            triple,
            "-sdk",
            sysroot,
        };
        foreach (var dir in searchPaths)
            swiftArgs.AddRange(["-F", dir]);
        if (rid.StartsWith("maccatalyst-"))
            swiftArgs.AddRange([
                "-F",
                Path.Combine(sysroot, "System/iOSSupport/System/Library/Frameworks"),
            ]);
        swiftArgs.AddRange(swift);
        swiftArgs.AddRange(["-o", obj]);
        await Run("xcrun", swiftArgs.ToArray());
        link.Add(Quote(obj));
        var compiler = (await Run("xcrun", ["--find", "swiftc"], true)).Trim();
        link.Add(
            "-L"
                + Quote(
                    Path.GetFullPath(
                        Path.Combine(
                            compiler,
                            "../../lib/swift",
                            rid.StartsWith("maccatalyst-") ? "maccatalyst" : sdk
                        )
                    )
                )
        );
        link.Add("-L" + Quote(Path.Combine(sysroot, "usr/lib/swift")));
    }
    else
    {
        // Native runners reference the registration entry even when the app has no
        // Swift plugins. A weak import alone does not satisfy a static executable's
        // link on current Apple toolchains.
        if (entries.Length != 0)
            throw new InvalidDataException("Apple plugin entries require Swift sources.");
        var registration = Path.Combine(objects, "Registration.c");
        var obj = Path.Combine(objects, "Registration.o");
        File.WriteAllText(
            registration,
            "int dotnative_plugins_reset(void) { return 0; }\nint dotnative_send_plugin(const unsigned char *p, unsigned long n) { return -1; }\nunsigned long dotnative_poll_plugin(unsigned char *p, unsigned long n) { return 0; }\nvoid dotnative_register_plugins(void) {}\nvoid dotnative_apns_registered(const unsigned char *bytes, long count) {}\nvoid dotnative_apns_failed(const char *message) {}\n"
        );
        await Run(
            "xcrun",
            [
                "--sdk",
                sdk,
                "clang",
                "-target",
                triple,
                "-isysroot",
                sysroot,
                "-O2",
                "-c",
                registration,
                "-o",
                obj,
            ]
        );
        link.Add(Quote(obj));
    }
    foreach (var lib in Entries("plugin-libraries.txt"))
    {
        if (!File.Exists(lib) || Path.GetExtension(lib) != ".a")
            throw new InvalidDataException("Invalid plugin archive: " + lib);
        link.Add(Quote(lib));
    }
    foreach (
        var framework in Entries("plugin-frameworks.txt")
            .Concat(appleDependencies.SystemFrameworks)
            .Distinct()
    )
    {
        if (!Regex.IsMatch(framework, "^[A-Za-z][A-Za-z0-9_]*$"))
            throw new InvalidDataException("Invalid framework name.");
        link.Add("-framework");
        link.Add(framework);
    }
    if (searchPaths.Length > 0)
        link.Add("-ObjC");
    foreach (var dir in searchPaths)
    {
        link.Add("-F" + Quote(dir));
        foreach (var framework in Directory.GetDirectories(dir, "*.framework"))
        {
            var name = Path.GetFileNameWithoutExtension(framework);
            if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$"))
                throw new InvalidDataException("Invalid framework name.");
            link.Add("-framework");
            link.Add(name);
        }
    }
    if (appleDependencies.BundleInputs.Any(b => b.Sign))
        link.AddRange(["-rpath", "@executable_path/Frameworks"]);
    File.WriteAllLines(Path.Combine(output, "plugins.link.rsp"), link);

    string[] FrameworkSearchPaths()
    {
        var paths = Entries("plugin-framework-search-paths.txt")
            .Concat(appleDependencies.FrameworkPaths.Select(p => Path.GetDirectoryName(p)!))
            .Distinct()
            .ToArray();
        foreach (var path in paths)
            if (!Path.IsPathRooted(path) || !Directory.Exists(path))
                throw new InvalidDataException("Invalid Apple framework search path: " + path);
        return paths;
    }
    static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    static async Task<string> Run(string file, string[] args, bool capture = false)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var text = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed: {process.ExitCode}");
        return await text;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("DNPLUG002: " + error.Message);
    Environment.ExitCode = 1;
}
