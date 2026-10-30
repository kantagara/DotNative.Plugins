"""Run a newly scaffolded native plugin through ProjectReference and a packed NuGet.

Uses the local SDK feed; on macOS runs the real Swift implementation. No UI renderer.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--template", action="store_true", help="Use only dotnet new and the template NuGet, never the DotNative CLI")
args = parser.parse_args()
args.template = True  # Public repository always uses dotnet new.

root = Path(__file__).resolve().parents[2]
feed = root / "artifacts/standalone-plugins/packages"
env = dict(os.environ, DOTNET_ROLL_FORWARD="Major")


def run(*args):
    result = subprocess.run(list(map(str, args)), cwd=root, env=env, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    print(result.stdout, end="")
    result.check_returncode()
    return result.stdout


with tempfile.TemporaryDirectory(prefix="dotnative-scaffold-") as directory:
    work = Path(directory).resolve()  # Avoid mixed /tmp and /private/tmp restore graph identities.
    config = ET.Element("configuration")
    settings = ET.SubElement(config, "config")
    ET.SubElement(settings, "add", key="globalPackagesFolder", value=str(work / "package-cache"))
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="sdk", value=str(feed))
    ET.SubElement(sources, "add", key="test", value=str(work / "packages"))
    ET.SubElement(sources, "add", key="nuget", value="https://api.nuget.org/v3/index.json")
    (work / "packages").mkdir()
    ET.ElementTree(config).write(work / "NuGet.Config")
    plugin = work / "Acme.Sample"
    if args.template:
        hive = work / "template-hive"
        run("dotnet", "new", "install", feed / "DotNative.Plugins.Templates.0.1.0-preview.15.nupkg",
            "--debug:custom-hive", hive)
        run("dotnet", "new", "dotnative-plugin", "-n", "Acme.Sample", "-o", plugin,
            "--debug:custom-hive", hive)
        assert not (plugin / "dotnative.local.props").exists()
    project = plugin / "Acme.Sample.csproj"
    assert not any(p.get("Include") == "DotNative" for p in ET.parse(project).iter("PackageReference"))
    app = plugin / "example-standalone/Standalone.csproj"
    result = run("dotnet", "run", "--project", app)
    assert "macOS: Version" in result if os.uname().sysname == "Darwin" else ": " in result
    for rid, platform, expected in [("", "android", ".kt"), ("maccatalyst-arm64", "maccatalyst", ".swift")]:
        result = run("dotnet", "msbuild", app, "-t:CollectDotNativeProjectPlugins",
                     f"-p:RuntimeIdentifier={rid}", f"-p:TargetPlatformIdentifier={platform}",
                     "-getItem:DotNativeNativeSource,DotNativePluginEntry")
        items = json.loads(result)["Items"]
        assert len(items["DotNativePluginEntry"]) == 1
        assert items["DotNativeNativeSource"][0]["Identity"].endswith(expected)
    run("dotnet", "pack", project, "-c", "Release", "-o", work / "packages", "-p:PackageVersion=1.0.0-scaffoldtest")
    tree = ET.parse(app)
    for group in tree.getroot().findall("ItemGroup"):
        for item in list(group):
            if item.tag == "ProjectReference":
                group.remove(item)
                ET.SubElement(group, "PackageReference", Include="Acme.Sample", Version="1.0.0-scaffoldtest")
    tree.write(app)
    run("dotnet", "clean", app, "-v:q")
    result = run("dotnet", "run", "--project", app)
    assert "macOS: Version" in result if os.uname().sysname == "Darwin" else ": " in result
    print("PASS generated plugin: ProjectReference and NuGet both execute native implementation")
