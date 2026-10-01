"""Bind clean NuGet consumption and promotion to the staged package bytes.

This is a same-run integrity receipt, not a signature or release provenance.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
IDS = ('Harborline.Api.Contracts', 'Harborline.Api.Client', 'Harborline.Api.Testing')
REPOSITORY = 'Harborline-Software/harborline-api'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def identity(version):
    commit = subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip()
    if os.environ.get('GITHUB_SHA', commit) != commit:
        raise ValueError('checkout does not match workflow source')
    repository = os.environ.get('GITHUB_REPOSITORY', REPOSITORY)
    if repository != REPOSITORY:
        raise ValueError('unexpected repository')
    return dict(version=version, source=commit, repository=repository,
                run=os.environ.get('GITHUB_RUN_ID', 'local'),
                attempt=os.environ.get('GITHUB_RUN_ATTEMPT', 'local'),
                workflow=os.environ.get('GITHUB_WORKFLOW_REF', 'local'))


def inventory(directory, version, tarballs=False):
    result = {}
    found = set()
    for path in sorted(directory.glob('*.nupkg')):
        if path.is_symlink():
            raise ValueError('symlink package')
        with zipfile.ZipFile(path) as archive:
            specs = [name for name in archive.namelist() if name.endswith('.nuspec')]
            if len(specs) != 1:
                raise ValueError('expected one nuspec')
            metadata = ET.fromstring(archive.read(specs[0]))
            values = {e.tag.split('}')[-1]: e.text for e in metadata.iter()}
        package_id = values.get('id')
        if package_id not in IDS or package_id in found or values.get('version') != version:
            raise ValueError('unexpected package identity/version')
        found.add(package_id)
        result[path.name] = dict(id=package_id, sha256=digest(path))
    if found != set(IDS):
        raise ValueError('missing package')
    if tarballs:
        archives = sorted(directory.glob('*.tgz'))
        if len(archives) != 1:
            raise ValueError('expected one npm contracts tarball')
        for path in archives:
            if path.is_symlink():
                raise ValueError('symlink tarball')
            with tarfile.open(path) as archive:
                metadata = json.load(archive.extractfile('package/package.json'))
            if metadata.get('name') != '@harborline-software/api-contracts' or metadata.get('version') != version:
                raise ValueError('unexpected npm package identity/version')
            result[path.name] = dict(id=metadata['name'], sha256=digest(path))
    return result


def consume(directory, version):
    receipt = directory / 'consumer-proof.json'
    receipt.unlink(missing_ok=True)
    packages = inventory(directory, version)
    source = identity(version)
    with tempfile.TemporaryDirectory(prefix='harborline-package-consumer-') as temporary:
        scratch = Path(temporary)
        consumer = scratch / 'consumer'
        shutil.copytree(ROOT / 'tests/package-consumer', consumer,
                        ignore=shutil.ignore_patterns('bin', 'obj'))
        # A standalone consumer needs these language defaults, without repository build imports.
        (scratch / 'Directory.Build.props').write_text(
            '<Project><PropertyGroup><Nullable>enable</Nullable>'
            '<ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
            '</PropertyGroup></Project>')
        config = ET.Element('configuration')
        sources = ET.SubElement(config, 'packageSources')
        ET.SubElement(sources, 'clear')
        ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
        ET.SubElement(sources, 'add', key='staged', value=str(directory.resolve()))
        mapping = ET.SubElement(config, 'packageSourceMapping')
        remote = ET.SubElement(mapping, 'packageSource', key='nuget.org')
        ET.SubElement(remote, 'package', pattern='*')
        staged = ET.SubElement(mapping, 'packageSource', key='staged')
        for package_id in IDS:
            ET.SubElement(staged, 'package', pattern=package_id)
        config_path = scratch / 'NuGet.Config'
        ET.ElementTree(config).write(config_path, encoding='utf-8', xml_declaration=True)
        cache = scratch / 'packages'
        environment = dict(os.environ, NUGET_PACKAGES=str(cache),
                           NUGET_HTTP_CACHE_PATH=str(scratch / 'http-cache'))
        project = consumer / 'Consumer.csproj'
        subprocess.run(['dotnet', 'restore', str(project), '--configfile', str(config_path),
                        '--packages', str(cache), '--no-http-cache', '--force-evaluate',
                        f'-p:HarborlinePackageVersion={version}'], cwd=ROOT, env=environment, check=True)
        # Verify the actual archive in the isolated restore cache, not only the feed's copy.
        for package in packages.values():
            package_id = package['id'].lower()
            restored = cache / package_id / version.lower() / f'{package_id}.{version.lower()}.nupkg'
            if not restored.is_file() or digest(restored) != package['sha256']:
                raise ValueError('restore did not consume the staged bytes')
        subprocess.run(['dotnet', 'run', '--project', str(project), '-c', 'Release',
                        '--no-restore', f'-p:HarborlinePackageVersion={version}'],
                       cwd=ROOT, env=environment, check=True)
    if inventory(directory, version) != packages:
        raise ValueError('packages changed during consumption')
    receipt.write_text(json.dumps(dict(identity=source, packages=packages), indent=2) + '\n')


def verify(directory, version, seal=False):
    proof = json.loads((directory / 'consumer-proof.json').read_text())
    packages = inventory(directory, version)
    expected = identity(version)
    if proof != dict(identity=expected, packages=packages):
        raise ValueError('consumer proof does not match source or package bytes')
    manifest = dict(identity=expected, packages=inventory(directory, version, tarballs=True),
                    consumerProofSha256=digest(directory / 'consumer-proof.json'))
    manifest_path = directory / 'package-manifest.json'
    if seal:
        manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')
    elif json.loads(manifest_path.read_text()) != manifest:
        raise ValueError('promotion manifest does not match staged bytes/source')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['consume', 'seal', 'verify'])
    parser.add_argument('directory', type=Path)
    parser.add_argument('version')
    args = parser.parse_args()
    if args.action == 'consume':
        consume(args.directory, args.version)
    else:
        verify(args.directory, args.version, seal=args.action == 'seal')
