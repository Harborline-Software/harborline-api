import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import tarfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile

spec = importlib.util.spec_from_file_location('proof', Path(__file__).parents[1] / 'package-proof.py')
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)
VERSION = '0.1.0-preview.123'
PACKAGE_IDS = ('Harborline.Api.Contracts', 'Harborline.Api.Client', 'Harborline.Api.Testing')


class PromotionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.stage = Path(self.temporary.name) / 'stage'
        self.stage.mkdir()
        self.environment = patch.dict(os.environ, {
            'GITHUB_REPOSITORY': 'Harborline-Software/harborline-api',
            'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1',
            'GITHUB_WORKFLOW_REF': 'Harborline-Software/harborline-api/.github/workflows/packages.yml@refs/tags/v0.1.0-preview.123',
        })
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.git = patch.object(proof.subprocess, 'check_output', return_value='a' * 40)
        self.git.start()
        self.addCleanup(self.git.stop)
        self.sha = patch.dict(os.environ, {'GITHUB_SHA': 'a' * 40})
        self.sha.start()
        self.addCleanup(self.sha.stop)
        for package_id in PACKAGE_IDS:
            self.package(package_id)
        with tarfile.open(self.stage / 'contracts.tgz', 'w:gz') as archive:
            data = json.dumps(dict(name='@harborline-software/api-contracts', version=VERSION)).encode()
            entry = tarfile.TarInfo('package/package.json')
            entry.size = len(data)
            archive.addfile(entry, io.BytesIO(data))
        self.calls = []

    def package(self, package_id, version=VERSION, payload=b'original'):
        with zipfile.ZipFile(self.stage / f'{package_id}.{VERSION}.nupkg', 'w') as archive:
            archive.writestr(f'{package_id}.nuspec',
                             f'<package><metadata><id>{package_id}</id><version>{version}</version></metadata></package>')
            archive.writestr('lib/net10.0/payload', payload)

    def dotnet(self, args, **kwargs):
        self.calls.append(args)
        self.assertTrue(kwargs['check'])
        cache = Path(kwargs['env']['NUGET_PACKAGES'])
        self.assertNotEqual(cache, self.ambient)
        project = Path(args[2] if args[1] == 'restore' else args[3])
        self.assertNotEqual(project.parent, proof.ROOT / 'tests/package-consumer')
        self.assertFalse((project.parent / 'obj').exists())
        if args[1] == 'restore':
            config = ET.parse(args[args.index('--configfile') + 1])
            self.assertEqual(config.find('packageSources/add[@key="staged"]').get('value'), str(self.stage.resolve()))
            self.assertEqual({e.get('pattern') for e in config.findall('packageSourceMapping/packageSource[@key="staged"]/package')}, set(PACKAGE_IDS))
            self.assertIn('--no-http-cache', args)
            for package_id in PACKAGE_IDS:
                restored = cache / package_id.lower() / VERSION
                restored.mkdir(parents=True)
                shutil.copyfile(self.stage / f'{package_id}.{VERSION}.nupkg',
                                restored / f'{package_id.lower()}.{VERSION}.nupkg')

    def consume(self, failure=False):
        self.ambient = Path(self.temporary.name) / 'ambient'
        # Same ID/version, different bytes: no subprocess is permitted to use this cache.
        self.ambient.mkdir(exist_ok=True)
        (self.ambient / 'poison.nupkg').write_bytes(b'different bytes')
        with patch.dict(os.environ, {'NUGET_PACKAGES': str(self.ambient)}), patch.object(
            proof.subprocess, 'run', side_effect=self.dotnet if not failure else subprocess.CalledProcessError(1, 'dotnet')):
            proof.consume(self.stage, VERSION)

    def test_success_transfer_and_tarball_hash_binding(self):
        self.consume()
        proof.verify(self.stage, VERSION, seal=True)
        copied = Path(self.temporary.name) / 'downloaded'
        shutil.copytree(self.stage, copied)
        proof.verify(copied, VERSION)
        self.assertEqual([call[1] for call in self.calls], ['restore', 'run'])
        (copied / 'contracts.tgz').write_bytes(b'tampered')
        with self.assertRaises((ValueError, tarfile.ReadError)):
            proof.verify(copied, VERSION)

    def test_missing_and_wrong_version_refused_before_consumer(self):
        (self.stage / f'{PACKAGE_IDS[0]}.{VERSION}.nupkg').unlink()
        with self.assertRaises(ValueError):
            proof.consume(self.stage, VERSION)
        self.package(PACKAGE_IDS[0], version='0.1.0-preview.999')
        with self.assertRaises(ValueError):
            proof.consume(self.stage, VERSION)

    def test_tampered_package_cannot_be_promoted(self):
        self.consume()
        proof.verify(self.stage, VERSION, seal=True)
        self.package(PACKAGE_IDS[0], payload=b'replacement')
        with self.assertRaises(ValueError):
            proof.verify(self.stage, VERSION)

    def test_wrong_source_and_cross_run_refused(self):
        self.consume()
        proof.verify(self.stage, VERSION, seal=True)
        for field, value in [('GITHUB_RUN_ID', '456'), ('GITHUB_RUN_ATTEMPT', '2'),
                             ('GITHUB_SHA', 'b' * 40), ('GITHUB_REPOSITORY', 'other/repo'),
                             ('GITHUB_WORKFLOW_REF', 'other/workflow')]:
            with self.subTest(field=field), patch.dict(os.environ, {field: value}):
                with self.assertRaises(ValueError):
                    proof.verify(self.stage, VERSION)

    def test_consumer_failure_removes_old_success(self):
        self.consume()
        with self.assertRaises(subprocess.CalledProcessError):
            self.consume(failure=True)
        self.assertFalse((self.stage / 'consumer-proof.json').exists())
        with self.assertRaises(FileNotFoundError):
            proof.verify(self.stage, VERSION, seal=True)

    def test_run_failure_cannot_earn_proof(self):
        self.ambient = Path(self.temporary.name) / 'ambient'
        def fail_run(args, **kwargs):
            self.dotnet(args, **kwargs)
            if args[1] == 'run':
                raise subprocess.CalledProcessError(1, 'dotnet run')
        with patch.object(proof.subprocess, 'run', side_effect=fail_run):
            with self.assertRaises(subprocess.CalledProcessError):
                proof.consume(self.stage, VERSION)
        self.assertFalse((self.stage / 'consumer-proof.json').exists())

    def test_restored_replacement_cannot_earn_proof(self):
        self.ambient = Path(self.temporary.name) / 'ambient'
        def poisoned_restore(args, **kwargs):
            self.dotnet(args, **kwargs)
            cache = Path(kwargs['env']['NUGET_PACKAGES'])
            restored = cache / PACKAGE_IDS[0].lower() / VERSION / f'{PACKAGE_IDS[0].lower()}.{VERSION}.nupkg'
            restored.write_bytes(b'different restored bytes')
        with patch.object(proof.subprocess, 'run', side_effect=poisoned_restore):
            with self.assertRaisesRegex(ValueError, 'staged bytes'):
                proof.consume(self.stage, VERSION)
        self.assertFalse((self.stage / 'consumer-proof.json').exists())

    def test_wrong_manifest_refused(self):
        self.consume()
        proof.verify(self.stage, VERSION, seal=True)
        manifest = self.stage / 'package-manifest.json'
        value = json.loads(manifest.read_text())
        value['identity']['source'] = 'b' * 40
        manifest.write_text(json.dumps(value))
        with self.assertRaises(ValueError):
            proof.verify(self.stage, VERSION)


if __name__ == '__main__':
    unittest.main()
