#!/usr/bin/env python3
"""Create an official dotnet-new MAUI app and add preview packages, without SDK source references."""
import argparse
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--packages', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
output = args.output.resolve()
if output.exists():
    raise SystemExit('Output must be a new directory')
dotnet = os.environ.get('DOTNET', 'dotnet')
subprocess.run([dotnet, 'new', 'maui', '-n', 'FreshMaui', '-o', str(output), '--no-restore'], check=True)
config = ET.Element('configuration')
settings = ET.SubElement(config, 'config')
ET.SubElement(settings, 'add', key='globalPackagesFolder', value=str(output / '.packages'))
sources = ET.SubElement(config, 'packageSources')
ET.SubElement(sources, 'clear')
ET.SubElement(sources, 'add', key='preview', value=str(args.packages.resolve()))
ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
mapping = ET.SubElement(config, 'packageSourceMapping')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='preview'), 'package', pattern='DotNative.*')
ET.SubElement(ET.SubElement(mapping, 'packageSource', key='nuget.org'), 'package', pattern='*')
ET.indent(config)
ET.ElementTree(config).write(output / 'NuGet.Config')
project = output / 'FreshMaui.csproj'
for package in ['DotNative.Plugins.Maui', 'DotNative.Plugins.NativeProbe']:
    subprocess.run([dotnet, 'add', str(project), 'package', package,
                    '--version', '0.1.0-preview.18', '--no-restore'], cwd=output, check=True)
page = output / 'MainPage.xaml.cs'
source = page.read_text(encoding='utf-8-sig')
assert source.count('InitializeComponent();') == 1
source = source.replace('InitializeComponent();', '''InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                DotNative.Plugins.PluginHost.Register();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20000);
                var actual = await Task.Run(() => StandalonePlugins.Probe.HashAsync(bytes, deadline.Token));
                if (!actual.SequenceEqual(System.Security.Cryptography.SHA256.HashData(bytes)))
                    throw new Exception("Native SHA256 mismatch");
                CounterBtn.Text = "PASS: native plugin";
                Console.WriteLine("PASS: official dotnet new maui + dotnet add package + PluginHost.Register + worker native SHA256");
            }
            catch (Exception error)
            {
                CounterBtn.Text = "FAIL: native plugin";
                Console.WriteLine("FAIL: " + error);
            }
        };''')
page.write_text(source)
print('Created official template consumer:', project)
print('Build/run with the platform workload. Direct APK installation requires -p:EmbedAssembliesIntoApk=true.')
