using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class AndroidBuild
{
    internal static async Task Build(string output, string package, string host, string sdk, string minimum)
    {
        if (!Version.TryParse(minimum.Contains('.') ? minimum : minimum + ".0", out var platform)
            || platform.Major < 21 || platform.Minor != 0 || platform.Build > 0 || platform.Revision > 0)
            throw new ArgumentException("Android minimum OS must be a whole API level of at least 21.");
        var apiLevel = platform.Major;
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        Directory.CreateDirectory(output);
        File.WriteAllText(
            Path.Combine(output, "standalone-android.txt"),
            "JNI is supplied by .NET Android."
        );
        await Run(dotnet, [host, output, "linux-bionic-arm64", apiLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), sdk]);
        var root = Path.Combine(output, "android");
        Directory.CreateDirectory(root);
        var template = Path.Combine(package, "tools", "AndroidProject");
        foreach (var file in Directory.GetFiles(template, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(root, Path.GetRelativePath(template, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
        var module = Path.Combine(root, "plugins");
        Directory.CreateDirectory(Path.Combine(module, "src/main"));
        // Microsoft Android bindings publish their exact Maven coordinate in NuGet tags.
        // Resolve Gradle against those versions before choosing which side packages each JAR/AAR.
        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in File.ReadAllLines(Path.Combine(output, "host-android-libraries.txt")))
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
            for (
                var depth = 0;
                directory is not null && depth < 4;
                depth++, directory = directory.Parent
            )
            {
                var nuspecs = directory.Exists ? directory.GetFiles("*.nuspec") : [];
                if (nuspecs.Length == 0)
                    continue;
                foreach (var nuspec in nuspecs)
                {
                    var tags =
                        XDocument
                            .Load(nuspec.FullName)
                            .Descendants()
                            .FirstOrDefault(e => e.Name.LocalName == "tags")
                            ?.Value
                        ?? "";
                    foreach (
                        Match match in Regex.Matches(tags, @"(?:^|\s)artifact_versioned=([^\s]+)")
                    )
                        coordinates.Add(match.Groups[1].Value);
                }
                break;
            }
        }
        File.WriteAllText(
            Path.Combine(output, "host-maven-versions.json"),
            JsonSerializer.Serialize(coordinates)
        );
        // Remove obsolete native payload when upgrading an existing intermediate directory.
        var jni = Path.Combine(module, "src/main/jniLibs");
        if (Directory.Exists(jni))
            Directory.Delete(jni, true);
        File.WriteAllText(
            Path.Combine(module, "src/main/AndroidManifest.xml"),
            "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\" />"
        );
        File.WriteAllText(
            Path.Combine(root, "settings.gradle"),
            "pluginManagement { repositories { google(); mavenCentral(); gradlePluginPortal() } }\ndependencyResolutionManagement { repositories { google(); mavenCentral() } }\ninclude ':plugins'\n"
        );
        File.WriteAllText(
            Path.Combine(root, "build.gradle"),
            "plugins { id 'com.android.library' version '8.11.1' apply false; id 'org.jetbrains.kotlin.android' version '2.2.20' apply false }\n"
        );
        File.WriteAllText(
            Path.Combine(root, "gradle.properties"),
            "android.useAndroidX=true\nkotlin.stdlib.default.dependency=false\norg.gradle.jvmargs=-Xmx2g\n"
        );
        File.WriteAllText(
            Path.Combine(module, "build.gradle"),
            $$"""
            plugins { id 'com.android.library'; id 'org.jetbrains.kotlin.android' }
            android {
                namespace 'com.dotnative.plugins'
                compileSdk 36
                defaultConfig { minSdk {{apiLevel}}; consumerProguardFiles 'consumer-rules.pro' }
                sourceSets.main.java.srcDir '../../plugin-java'
                compileOptions { sourceCompatibility JavaVersion.VERSION_17; targetCompatibility JavaVersion.VERSION_17 }
            }
            kotlin { jvmToolchain(17) }
            apply from: '../../plugin-dependencies.gradle'
            def hostCoordinates = new groovy.json.JsonSlurper().parse(file('../../host-maven-versions.json'))
            dependencies {
                def kotlinCoordinate = hostCoordinates.find { it.startsWith('org.jetbrains.kotlin:kotlin-stdlib:') }
                implementation(kotlinCoordinate ?: 'org.jetbrains.kotlin:kotlin-stdlib:2.2.20')
                constraints {
                    hostCoordinates.each { coordinate ->
                        def parts = coordinate.split(':')
                        implementation(parts[0] + ':' + parts[1]) { version { strictly(parts[2]) } }
                    }
                }
            }
            def hostLibrariesFile = file('../../host-android-libraries.txt')
            def hostModules = hostLibrariesFile.exists() ? hostLibrariesFile.readLines().collect {
                new File(it).name.replaceFirst(/\.(aar|jar)$/, '')
            }.toSet() : [] as Set
            tasks.register('copyPluginDependencies', Copy) {
                // Host-supplied classes (including Kotlin) are reconciled after resolution.
                from { configurations.releaseRuntimeClasspath.resolvedConfiguration.resolvedArtifacts.findAll {
                    !hostModules.contains(it.moduleVersion.id.group + '.' + it.name)
                }.collect { it.file } }
                into layout.buildDirectory.dir('plugin-dependencies')
            }
            """
        );
        File.WriteAllText(
            Path.Combine(module, "consumer-rules.pro"),
            "-keep class com.dotnative.plugins.** { *; }\n"
        );
        var dependencies = Path.Combine(module, "build/plugin-dependencies");
        if (Directory.Exists(dependencies))
            Directory.Delete(dependencies, true);
        await Run(
            "bash",
            [
                Path.Combine(root, "gradlew"),
                "--no-daemon",
                "-p",
                root,
                ":plugins:assembleRelease",
                ":plugins:copyPluginDependencies",
            ]
        );
        var items = new XElement("ItemGroup");
        var suppliedClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in File.ReadAllLines(Path.Combine(output, "host-android-libraries.txt")))
            suppliedClasses.UnionWith(AndroidArchives.Classes(file));
        foreach (
            var file in (
                Directory.Exists(dependencies) ? Directory.GetFiles(dependencies) : []
            ).Append(Path.Combine(module, "build/outputs/aar/plugins-release.aar"))
        )
        {
            if (Path.GetExtension(file) is not (".aar" or ".jar"))
                continue;
            var classes = AndroidArchives.Classes(file);
            var overlap = classes.Count(suppliedClasses.Contains);
            var packagedFile = file;
            if (overlap > 0)
            {
                if (overlap == classes.Count)
                {
                    Console.WriteLine(
                        $"Native plugins: reusing host classes for {Path.GetFileName(file)}."
                    );
                    continue;
                }
                // Some bindings split a Maven JAR (e.g. Guava's ListenableFuture) into
                // a separate package. Preserve the remaining classes and reuse the host type.
                if (Path.GetExtension(file) != ".jar")
                    throw new InvalidOperationException(
                        $"Android dependency {Path.GetFileName(file)} partially overlaps host bindings ({overlap}/{classes.Count} classes). Align the host binding and plugin Maven versions."
                    );
                packagedFile = AndroidArchives.WithoutHostClasses(
                    file,
                    suppliedClasses,
                    Path.Combine(output, "merged-jars")
                );
                Console.WriteLine(
                    $"Native plugins: reusing {overlap} host classes from {Path.GetFileName(file)}."
                );
            }
            items.Add(
                new XElement(
                    "AndroidLibrary",
                    new XAttribute("Include", packagedFile),
                    new XElement("Bind", "false"),
                    new XElement("Pack", "true")
                )
            );
        }
        // .NET shrinkers must preserve methods called through the generic JNI bridge.
        items.Add(
            new XElement(
                "ProguardConfiguration",
                new XAttribute("Include", Path.Combine(module, "consumer-rules.pro"))
            )
        );
        var project = new XElement("Project", items);
        foreach (
            var type in new[]
            {
                "AndroidLibrary",
                "ProguardConfiguration",
                "NativeReference",
                "BundleResource",
                "None",
                "LinkerArgument",
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

    private static async Task Run(string executable, string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} failed: {process.ExitCode}");
    }
}
