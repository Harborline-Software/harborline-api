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
        self.tarball(self.stage / 'contracts.tgz')
        self.calls = []

    def tarball(self, path, extra=None):
        files = {'package/package.json': json.dumps(dict(name='@harborline-software/api-contracts', version=VERSION)).encode()}
        if extra is not None:
            files['package/extra.js'] = extra
        with tarfile.open(path, 'w:gz') as archive:
            for name, data in files.items():
                entry = tarfile.TarInfo(name)
                entry.size = len(data)
                archive.addfile(entry, io.BytesIO(data))

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
        self.assertEqual(json.loads((self.stage / 'consumer-proof.json').read_text())['schema'],
                         'harborline-api/consumer-proof/1')
        self.assertEqual(json.loads((self.stage / 'package-manifest.json').read_text())['schema'],
                         'harborline-api/package-manifest/1')
        copied = Path(self.temporary.name) / 'downloaded'
        shutil.copytree(self.stage, copied)
        proof.verify(copied, VERSION)
        self.assertEqual([call[1] for call in self.calls], ['restore', 'run'])
        # Same npm identity and version, different bytes: only the manifest's tarball hash can refuse this.
        self.tarball(copied / 'contracts.tgz', extra=b'tampered')
        with self.assertRaisesRegex(ValueError, 'promotion manifest does not match'):
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

    def test_recorded_proof_source_and_repository_bind_to_environment(self):
        expected = proof.identity(VERSION)
        for field, value in [('source', 'b' * 40), ('repository', 'other/repo')]:
            with self.subTest(field=field):
                recorded = dict(expected, **{field: value})
                # The production writers build an internally consistent pair for this identity.
                # identity() guard tests above remain separate from these report-binding cases.
                with patch.object(proof, 'identity', return_value=recorded):
                    self.consume()
                    proof.verify(self.stage, VERSION, seal=True)
                    proof.verify(self.stage, VERSION)
                with self.assertRaisesRegex(ValueError, 'consumer proof does not match'):
                    proof.verify(self.stage, VERSION)

    def test_recorded_manifest_source_and_repository_bind_to_environment(self):
        self.consume()
        proof.verify(self.stage, VERSION, seal=True)
        path = self.stage / 'package-manifest.json'
        valid = json.loads(path.read_text())
        for field, value in [('source', 'b' * 40), ('repository', 'other/repo')]:
            with self.subTest(field=field):
                manifest = dict(valid, identity=dict(valid['identity'], **{field: value}))
                path.write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, 'promotion manifest does not match'):
                    proof.verify(self.stage, VERSION)

    def schema_refusal(self, filename, schema, refusal):
        self.consume()
        proof.verify(self.stage, VERSION, seal=True)
        path = self.stage / filename
        value = json.loads(path.read_text())
        if schema is None:
            del value['schema']
        else:
            value['schema'] = schema
        path.write_text(json.dumps(value))
        if filename == 'consumer-proof.json':
            # Preserve the hash binding so the schema refusal cannot be masked by stale hashes.
            manifest_path = self.stage / 'package-manifest.json'
            manifest = json.loads(manifest_path.read_text())
            manifest['consumerProofSha256'] = proof.digest(path)
            manifest_path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, refusal):
            proof.verify(self.stage, VERSION)

    def test_missing_consumer_proof_schema_refused(self):
        self.schema_refusal('consumer-proof.json', None, 'consumer proof does not match')

    def test_unknown_consumer_proof_schema_refused(self):
        self.schema_refusal('consumer-proof.json', 'harborline-api/consumer-proof/999', 'consumer proof does not match')

    def test_missing_package_manifest_schema_refused(self):
        self.schema_refusal('package-manifest.json', None, 'promotion manifest does not match')

    def test_unknown_package_manifest_schema_refused(self):
        self.schema_refusal('package-manifest.json', 'harborline-api/package-manifest/999', 'promotion manifest does not match')


if __name__ == '__main__':
    unittest.main()
