"""Small real NuGet cache-poisoning probe; no repository package build.

Synthetic packages and a minimal net11 consumer measure restore/cache selection.
The unchanged shipped API consumer remains the full pack-consume CI check.
"""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('proof', Path(__file__).parents[1] / 'package-proof.py')
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)
version = '0.1.0-preview.cacheprobe'
with tempfile.TemporaryDirectory(prefix='api-nuget-probe-') as temporary:
    root = Path(temporary)
    staged = root / 'staged'
    poison = root / 'poison'
    cache = root / 'ambient-cache'
    for directory, content in [(staged, b'exact staged bytes'), (poison, b'poisoned same-id-version bytes')]:
        directory.mkdir()
        for package_id in proof.IDS:
            with zipfile.ZipFile(directory / f'{package_id}.{version}.nupkg', 'w') as archive:
                archive.writestr(f'{package_id}.nuspec', f'<package><metadata><id>{package_id}</id>'
                                 f'<version>{version}</version><authors>Probe</authors>'
                                 '<description>Offline cache selection probe</description></metadata></package>')
                archive.writestr('lib/net11.0/_._', '')
                archive.writestr('proof.txt', content)
    fixture = root / 'tests/package-consumer'
    fixture.mkdir(parents=True)
    references = ''.join(f'<PackageReference Include="{p}" Version="{version}" />' for p in proof.IDS)
    (fixture / 'Consumer.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
        '<OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><NuGetAudit>false</NuGetAudit>'
        '</PropertyGroup><ItemGroup>' + references + '</ItemGroup></Project>')
    (fixture / 'Program.cs').write_text('return 0;\n')
    config = root / 'NuGet.Config'
    config.write_text(f'<configuration><packageSources><clear/><add key="poison" value="{poison}" />'
                      '</packageSources></configuration>')
    subprocess.run(['dotnet', 'restore', str(fixture / 'Consumer.csproj'), '--configfile', str(config),
                    '--packages', str(cache)], check=True)
    for package_id in proof.IDS:
        cached = cache / package_id.lower() / version / f'{package_id.lower()}.{version}.nupkg'
        assert proof.digest(cached) != proof.digest(staged / f'{package_id}.{version}.nupkg')
    # Source/run binding is covered by the unit suite; this probe isolates real NuGet behavior.
    with patch.object(proof, 'ROOT', root), patch.object(proof.subprocess, 'check_output', return_value='a' * 40), \
            patch.dict(os.environ, {'NUGET_PACKAGES': str(cache)}, clear=False):
        proof.consume(staged, version)
    assert (staged / 'consumer-proof.json').is_file()
    print('PASS: real NuGet consumed exact staged archives despite a populated same-ID/version ambient cache')
