#!/usr/bin/env python3
"""Download a GitHub preview and prepare a fresh, package-only sample consumer."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = 'kantagara/DotNative.Plugins'
VERSION = '0.1.0-preview.17'
SAMPLES = {'Maui': 'Maui', 'Avalonia': 'Avalonia', 'Uno': 'Uno',
           'AvaloniaMobile': 'Avalonia', 'UnoMobile': 'Uno'}


def download(tag, feed):
    feed.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='dotnative-release-') as temporary:
        stage = Path(temporary)
        subprocess.run(['gh', 'release', 'download', tag, '--repo', REPOSITORY,
                        '--pattern', '*.nupkg', '--pattern', 'packages.json',
                        '--dir', str(stage)], check=True)
        manifest = json.loads((stage / 'packages.json').read_text())
        if manifest['version'] != tag.removeprefix('v'):
            raise ValueError('Release version does not match manifest')
        for package in manifest['packages']:
            name = package['file']
            if Path(name).name != name or not name.endswith('.nupkg'):
                raise ValueError('Invalid package filename')
            source = stage / name
            if hashlib.sha256(source.read_bytes()).hexdigest() != package['sha256']:
                raise ValueError('Package checksum mismatch: ' + name)
        for package in manifest['packages']:
            shutil.copy2(stage / package['file'], feed / package['file'])
        shutil.copy2(stage / 'packages.json', feed / 'packages.json')
    print(f"Downloaded and verified {len(manifest['packages'])} packages from {tag}")


def consumer(sample, output, feed, version):
    if output.exists():
        raise ValueError('Choose a new output directory; existing consumers are never overwritten')
    output.mkdir(parents=True)
    project_dir = output / 'Samples/PluginHosts' / sample
    shutil.copytree(ROOT / 'Samples/PluginHosts' / sample, project_dir,
                    ignore=shutil.ignore_patterns('bin', 'obj', '*.user'))
    shutil.copytree(ROOT / 'Samples/PluginHosts/Shared', project_dir.parent / 'Shared')
    config = ET.Element('configuration')
    settings = ET.SubElement(config, 'config')
    ET.SubElement(settings, 'add', key='globalPackagesFolder', value=str(output / '.packages'))
    sources = ET.SubElement(config, 'packageSources')
    ET.SubElement(sources, 'clear')
    ET.SubElement(sources, 'add', key='github-preview', value=str(feed))
    ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
    mapping = ET.SubElement(config, 'packageSourceMapping')
    local = ET.SubElement(mapping, 'packageSource', key='github-preview')
    ET.SubElement(local, 'package', pattern='DotNative.*')
    upstream = ET.SubElement(mapping, 'packageSource', key='nuget.org')
    ET.SubElement(upstream, 'package', pattern='*')
    ET.indent(config)
    ET.ElementTree(config).write(output / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
    project = next(project_dir.glob('*.csproj'))
    tree = ET.parse(project)
    for parent in tree.iter():
        for child in list(parent):
            if child.tag in ('RestoreAdditionalProjectSources', 'RestorePackagesPath') or (
                child.tag == 'PackageReference' and child.get('Include', '').startswith('DotNative.')):
                parent.remove(child)
    if any(True for _ in tree.iter('ProjectReference')):
        raise ValueError('Consumer must not reference SDK source projects')
    ET.indent(tree)
    tree.write(project, encoding='utf-8', xml_declaration=True)
    dotnet = os.environ.get('DOTNET', 'dotnet')
    for package in ['DotNative.Plugins.' + SAMPLES[sample], 'DotNative.Plugins.NativeProbe']:
        subprocess.run([dotnet, 'add', str(project), 'package', package,
                        '--version', version, '--no-restore'], cwd=output, check=True)
    print('Fresh package-only consumer:', project)
    print('Build with the appropriate target framework and runtime; package cache:', output / '.packages')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', default='v' + VERSION)
    parser.add_argument('--feed', type=Path, default=ROOT / 'artifacts/standalone-plugins/packages')
    parser.add_argument('--sample', choices=SAMPLES)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--download', action='store_true')
    args = parser.parse_args()
    feed = args.feed.resolve()
    if args.download:
        download(args.tag, feed)
    if args.sample:
        if args.output is None:
            parser.error('--sample requires --output')
        consumer(args.sample, args.output.resolve(), feed, args.tag.removeprefix('v'))
    elif not args.download:
        parser.error('Choose --download or --sample')
