using System.Diagnostics;
using System.Xml.Linq;

try
{
    // Called by framework-independent MSBuild targets. Inputs are line files, never shell fragments.
    if (args.Length != 4)
        throw new ArgumentException(
            "Expected output directory, RID, minimum OS, package directory."
        );
    var output = Path.GetFullPath(args[0]);
    var rid = args[1];
    var minimum = args[2];
    var package = Path.GetFullPath(args[3]);
    var host = Path.Combine(package, "tools", "NativeBuild", "DotNative.NativeBuild.dll");
    var sdk = Path.Combine(package, "tools", "PluginSdk");
    if (rid.StartsWith("win-") || rid.StartsWith("linux-"))
    {
        await DesktopBuild.Build(output, package, rid, sdk);
        return;
    }
    if (rid == "android" || rid.StartsWith("android-"))
    {
        await AndroidBuild.Build(output, package, host, sdk, minimum);
        return;
    }
    Directory.CreateDirectory(output);
    if (Directory.Exists(Path.Combine(output, "plugin-objects")))
        Directory.Delete(Path.Combine(output, "plugin-objects"), true);
    await Run(
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        [host, output, rid, minimum, sdk]
    );
    var dependencies = await AppleDependencies.Resolve(
        Entries("plugin-apple-manifests.txt"),
        rid,
        AppleDependencies.DefaultCache,
        minimumOS: minimum
    );
    var objects = Directory.GetFiles(Path.Combine(output, "plugin-objects"), "*.o");
    var library = Path.Combine(
        output,
        rid.StartsWith("osx-") ? "libdotnative_plugins_host.dylib" : "libdotnative_plugins_host.a"
    );
    var items = new XElement("ItemGroup");
    if (rid == "osx-arm64")
    {
        // NativeBuild emits fully quoted linker response arguments. xcrun consumes the file directly.
        await Run(
            "xcrun",
            [
                "clang",
                "-dynamiclib",
                "-o",
                library,
                "@" + Path.Combine(output, "plugins.link.rsp"),
                "-framework",
                "Foundation",
                "-framework",
                "AppKit",
                "-framework",
                "CryptoKit",
                "-framework",
                "UserNotifications",
            ]
        );
        items.Add(
            new XElement(
                "None",
                new XAttribute("Include", library),
                new XElement("Link", Path.GetFileName(library)),
                new XElement("CopyToOutputDirectory", "PreserveNewest"),
                new XElement("CopyToPublishDirectory", "PreserveNewest")
            )
        );
        if (dependencies.BundleInputs.Length > 0)
            throw new NotSupportedException(
                "Desktop third-party framework bundling is not implemented."
            );
    }
    else if (
        rid
        is "ios-arm64"
            or "iossimulator-arm64"
            or "iossimulator-x64"
            or "maccatalyst-arm64"
            or "maccatalyst-x64"
    )
    {
        // Firebase and other static Objective-C SDKs register components through +load.
        // Framework NativeReference metadata alone does not preserve their categories.
        items.Add(new XElement("LinkerArgument", new XAttribute("Include", "-ObjC")));
        await Run(
            "xcrun",
            [
                "libtool",
                "-static",
                "-no_warning_for_no_symbols",
                "-o",
                library,
                .. objects,
                .. Entries("plugin-libraries.txt"),
            ]
        );
        items.Add(
            new XElement(
                "NativeReference",
                new XAttribute("Include", library),
                new XElement("Kind", "Static"),
                new XElement("ForceLoad", "true"),
                new XElement("SmartLink", "false"),
                new XElement(
                    "Frameworks",
                    string.Join(
                        " ",
                        new[] { "Foundation", "UIKit", "CryptoKit", "UserNotifications" }.Concat(
                            Entries("plugin-frameworks.txt")
                        )
                    )
                )
            )
        );
        var appleSdk =
            rid.StartsWith("maccatalyst-") ? "macosx"
            : rid == "ios-arm64" ? "iphoneos"
            : "iphonesimulator";
        var swiftPlatform = rid.StartsWith("maccatalyst-") ? "maccatalyst" : appleSdk;
        var swift = (await Run("xcrun", ["--find", "swiftc"], true)).Trim();
        var sysroot = (await Run("xcrun", ["--sdk", appleSdk, "--show-sdk-path"], true)).Trim();
        foreach (
            var path in new[]
            {
                Path.GetFullPath(Path.Combine(swift, "../../lib/swift", swiftPlatform)),
                Path.Combine(sysroot, "usr/lib/swift"),
            }
        )
            items.Add(new XElement("LinkerArgument", new XAttribute("Include", "-L" + path)));
        items.Add(
            XDocument
                .Load(Path.Combine(output, "apple-dependencies.targets"))
                .Root!.Element("ItemGroup")!
                .Elements()
        );
    }
    else
        throw new NotSupportedException(rid);
    var project = new XElement("Project", items);
    foreach (
        var type in new[]
        {
            "NativeReference",
            "BundleResource",
            "None",
            "LinkerArgument",
            "AndroidLibrary",
            "ProguardConfiguration",
        }
    )
        project.Add(
            new XElement(
                "Target",
                new XAttribute("Name", "Get" + type),
                new XAttribute("Returns", "@(" + type + ")")
            )
        );
    new XDocument(project).Save(Path.Combine(output, "standalone-plugins.targets"));

    string[] Entries(string name) =>
        File.Exists(Path.Combine(output, name))
            ? File.ReadAllLines(Path.Combine(output, name))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToArray()
            : [];
    static async Task<string> Run(string file, string[] arguments, bool capture = false)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
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
