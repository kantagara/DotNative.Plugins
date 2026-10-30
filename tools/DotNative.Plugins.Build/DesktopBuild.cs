using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class DesktopBuild
{
    internal static async Task Build(string output, string package, string rid, string sdk)
    {
        var windows = rid.StartsWith("win-");
        if (windows != OperatingSystem.IsWindows() || (!windows && !OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException(
                "Build Windows/Linux plugins on their target OS."
            );
        Directory.CreateDirectory(output);
        string[] Entries(string name) =>
            File.Exists(Path.Combine(output, name))
                ? File.ReadAllLines(Path.Combine(output, name))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToArray()
                : [];
        var entries = Entries("plugin-entries.txt");
        foreach (var entry in entries)
            if (!Regex.IsMatch(entry, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new InvalidDataException("Invalid plugin entry.");
        var registration = Path.Combine(output, "registration.cpp");
        var export = windows ? "__declspec(dllexport)" : "__attribute__((visibility(\"default\")))";
        File.WriteAllText(
            registration,
            "#include <DotNativePlugins.hpp>\n"
                + string.Join(
                    "\n",
                    entries.Select(e => $"void {e}(const dotnative::PluginRegistrar&);")
                )
                + $"\nextern \"C\" {export} void dotnative_register_plugins() {{}}\n"
                + $"extern \"C\" {export} int dotnative_plugins_attach(void* window) {{ try {{\n"
                + "for (auto& entry : dotnative::detail::channels) { entry.second.handlers.clear(); entry.second.OnReset = {}; }\n"
                + "dotnative::PluginRegistrar registrar(window);\n"
                + string.Join("\n", entries.Select(e => $"{e}(registrar);"))
                + "\nreturn 0; } catch (...) { return -1; } }\n"
        );
        var sources = Entries("plugin-sources.txt");
        if (sources.Any(s => Path.GetExtension(s) is not (".cpp" or ".cc" or ".c")))
            throw new InvalidDataException("Windows/Linux service plugins require C/C++ sources.");
        var library = Path.Combine(
            output,
            windows ? "dotnative_plugins_host.dll" : "libdotnative_plugins_host.so"
        );
        var args = new List<string>();
        if (windows)
        {
            args.AddRange([
                "/nologo",
                "/std:c++17",
                "/EHsc",
                "/MD",
                "/LD",
                "/I" + Path.Combine(sdk, "cpp"),
                registration,
                Path.Combine(sdk, "cpp", "PluginTransport.cpp"),
            ]);
            args.AddRange(sources);
            args.AddRange([
                "/link",
                "/OUT:" + library,
                "advapi32.lib",
                "bcrypt.lib",
                "ntdll.lib",
                "userenv.lib",
                "ws2_32.lib",
                "ole32.lib",
            ]);
            foreach (
                var symbol in new[]
                {
                    "dotnative_send_plugin",
                    "dotnative_poll_plugin",
                    "dotnative_plugins_reset",
                    "dotnative_register_channel",
                    "dotnative_plugin_reply",
                }
            )
                args.Add("/EXPORT:" + symbol);
        }
        else
        {
            args.AddRange([
                "-std=c++17",
                "-fPIC",
                "-shared",
                "-I" + Path.Combine(sdk, "cpp"),
                registration,
                Path.Combine(sdk, "cpp", "PluginTransport.cpp"),
            ]);
            args.AddRange(sources);
            args.AddRange(["-ldl", "-lpthread", "-lm", "-o", library]);
        }
        args.AddRange(Entries("plugin-libraries.txt"));
        args.AddRange(Entries("plugin-linker-arguments.txt"));
        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("CXX") ?? (windows ? "cl.exe" : "c++")
        )
        {
            UseShellExecute = false,
            WorkingDirectory = output,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Native C++ compilation failed: {process.ExitCode}"
            );
        var project = new XElement(
            "Project",
            new XElement(
                "ItemGroup",
                new XElement(
                    "None",
                    new XAttribute("Include", library),
                    new XElement("Link", Path.GetFileName(library)),
                    new XElement("CopyToOutputDirectory", "PreserveNewest"),
                    new XElement("CopyToPublishDirectory", "PreserveNewest")
                )
            )
        );
        foreach (
            var type in new[]
            {
                "None",
                "NativeReference",
                "BundleResource",
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
    }
}
