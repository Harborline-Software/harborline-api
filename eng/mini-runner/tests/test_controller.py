"""Oracles: literal trusted identities, required job set, scoped lifecycle properties."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import admission
import controller as c

HEAD = '1234567890abcdef1234567890abcdef12345678'
POLICY = {'version': 1, 'runId': '42', 'head': HEAD}
ENV = {
    'GITHUB_EVENT_NAME': 'workflow_dispatch',
    'GITHUB_REPOSITORY': 'Harborline-Software/harborline-api',
    'GITHUB_REPOSITORY_ID': '1360432948', 'GITHUB_REF': 'refs/heads/main',
    'GITHUB_ACTOR': 'ctwoodwa', 'GITHUB_ACTOR_ID': '1328090',
    'GITHUB_TRIGGERING_ACTOR': 'ctwoodwa', 'GITHUB_JOB': 'portable',
    'GITHUB_WORKFLOW_REF': 'Harborline-Software/harborline-api/.github/workflows/mini-portable-gate.yml@refs/heads/main',
    'GITHUB_SHA': HEAD, 'GITHUB_WORKFLOW_SHA': HEAD,
    'GITHUB_RUN_ID': '42', 'GITHUB_RUN_ATTEMPT': '1',
}
EVENT = {'ref': 'refs/heads/main', 'repository': {'id': 1360432948, 'full_name': 'Harborline-Software/harborline-api', 'fork': False}, 'sender': {'id': 1328090, 'login': 'ctwoodwa'}}
RUN = {'id': 42, 'repository': {'id': 1360432948}, 'head_repository': {'id': 1360432948}, 'event': 'workflow_dispatch', 'run_attempt': 1, 'head_branch': 'main', 'head_sha': HEAD, 'path': '.github/workflows/mini-portable-gate.yml', 'status': 'queued', 'conclusion': None, 'actor': {'id': 1328090, 'login': 'ctwoodwa'}, 'triggering_actor': {'id': 1328090, 'login': 'ctwoodwa'}}
JOBS = [{'name': 'portable ('+lane+')', 'status': 'queued', 'runner_id': 0, 'labels': ['self-hosted', 'harborline-api-mini-portable-v1', 'linux-arm64-orbstack', 'lane-'+lane]} for lane in ('a',)]


class Admission(unittest.TestCase):
    def test_exact_trusted_fixture(self):
        self.assertTrue(admission.admitted(POLICY, ENV, EVENT))

    def test_every_context_binding_is_required(self):
        for key in ENV:
            with self.subTest(key=key):
                altered = {**ENV, key: 'wrong'}
                self.assertFalse(admission.admitted(POLICY, altered, EVENT))

    def test_event_independent_identity_and_no_fork_or_inputs(self):
        for path, value in [(('repository', 'id'), 1), (('repository', 'full_name'), 'other/repo'), (('repository', 'fork'), True), (('sender', 'id'), 1), (('sender', 'login'), 'other'), (('ref',), 'refs/heads/feature'), (('pull_request',), {}), (('workflow_run',), {}), (('inputs',), {'command': 'untrusted'})]:
            with self.subTest(path=path):
                event = copy.deepcopy(EVENT)
                target = event[path[0]] if len(path) == 2 else event
                target[path[-1]] = value
                self.assertFalse(admission.admitted(POLICY, ENV, event))

    def test_policy_rejects_malformed_identity(self):
        for key, value in [('version', 2), ('head', '../x'), ('runId', '０42'), ('runId', '0'), ('head', HEAD.upper())]:
            with self.subTest(key=key, value=value):
                self.assertFalse(admission.admitted({**POLICY, key: value}, ENV, EVENT))


class HostAdmission(unittest.TestCase):
    def test_literal_run(self):
        self.assertEqual(c.validate_run(RUN, JOBS, HEAD, b'reviewed', b'reviewed'), POLICY)

    def test_event_attempt_branch_head_workflow_completion_and_actors(self):
        for key, value in [('event', 'pull_request'), ('run_attempt', 2), ('head_branch', 'feature'), ('head_sha', '0'*40), ('path', 'other.yml'), ('status', 'completed'), ('conclusion', 'success'), ('head_repository', {'id': 1}), ('repository', {'id': 1}), ('actor', {'id': 1, 'login': 'ctwoodwa'}), ('triggering_actor', {'id': 1328090, 'login': 'other'})]:
            with self.subTest(key=key):
                with self.assertRaises(RuntimeError):
                    c.validate_run({**RUN, key: value}, JOBS, HEAD, b'reviewed', b'reviewed')

    def test_reviewed_bytes_and_exact_unassigned_job_required(self):
        with self.assertRaises(RuntimeError):
            c.validate_run(RUN, JOBS, HEAD, b'changed', b'reviewed')
        variants = [[], JOBS+JOBS[:1]]
        for key, value in [('name', 'other'), ('status', 'in_progress'), ('runner_id', 4), ('labels', ['self-hosted'])]:
            jobs = copy.deepcopy(JOBS)
            jobs[0][key] = value
            variants.append(jobs)
        for jobs in variants:
            with self.subTest(jobs=jobs), self.assertRaises(RuntimeError):
                c.validate_run(RUN, jobs, HEAD, b'reviewed', b'reviewed')

    def test_missing_sdk_refuses_before_credentials(self):
        image = 'sha256:'+'a'*64
        policy = {**POLICY, 'sdk': '11.0.100-rc.1.26425.128', 'sourcesSha256': 'unused'}
        info = [{'Id': image, 'Architecture': 'arm64', 'Os': 'linux', 'Config': {'User': '1001:1001', 'Entrypoint': ['/opt/mini/entrypoint.sh'], 'Env': ['ACTIONS_RUNNER_HOOK_JOB_STARTED=/opt/mini/start-hook.sh', 'HOME=/runner/home']}}]
        with patch.object(c, 'docker', side_effect=[json.dumps(info), json.dumps(policy), '10.0.401 [/usr/share/dotnet/sdk]\n']), patch.object(c, 'api') as api:
            with self.assertRaisesRegex(RuntimeError, 'Pinned SDK absent'):
                c.preflight_image(image, policy, 'a'*32)
            api.assert_not_called()


class Recovery(unittest.TestCase):
    session = 'a'*32

    def test_absence_requires_successful_inventories(self):
        with patch.object(c, 'docker', return_value=''), patch.object(c, 'runners', return_value=[]):
            self.assertTrue(c.cleanup(self.session)['clean'])
        with patch.object(c, 'docker', side_effect=RuntimeError('daemon unavailable')), patch.object(c, 'runners', return_value=[]):
            self.assertFalse(c.cleanup(self.session)['clean'])
        with patch.object(c, 'docker', return_value=''), patch.object(c, 'runners', side_effect=RuntimeError('auth unavailable')):
            self.assertFalse(c.cleanup(self.session)['clean'])

    def test_only_exact_session_names_can_be_removed(self):
        with patch.object(c, 'inventory', return_value={'some-other-work'}), patch.object(c, 'docker') as docker, patch.object(c, 'runners', return_value=[]):
            self.assertFalse(c.cleanup(self.session)['clean'])
            docker.assert_not_called()
        with self.assertRaises(RuntimeError):
            c.cleanup('../../other')

    def test_partial_creation_and_runner_reconciliation(self):
        name = 'hl-mini-'+'a'*32+'-a'
        inventories = [{name}, set(), {name}, set(), set(), set()]
        unrelated = {'name': 'existing-user-runner', 'id': 9}
        with patch.object(c, 'inventory', side_effect=inventories), patch.object(c, 'docker') as docker, patch.object(c, 'runners', side_effect=[[{'name': name, 'id': 7}, unrelated], [unrelated]]), patch.object(c, 'api') as api:
            self.assertTrue(c.cleanup(self.session)['clean'])
            self.assertEqual(docker.call_args_list[0].args, ('rm', '-f', name))
            self.assertEqual(docker.call_args_list[1].args, ('volume', 'rm', name))
            api.assert_called_once_with('actions/runners/7', 'DELETE')

    def test_remaining_resource_is_failure(self):
        name = 'hl-mini-'+'a'*32+'-a'
        with patch.object(c, 'inventory', return_value={name}), patch.object(c, 'docker'), patch.object(c, 'runners', return_value=[]):
            self.assertFalse(c.cleanup(self.session)['clean'])

    def test_reservation_preserves_existing_inode_and_refuses_contention(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'heavy.lock'
            path.write_text('existing owner reservation')
            inode = path.stat().st_ino
            fd = c.reserve(path)
            try:
                with self.assertRaises(BlockingIOError):
                    c.reserve(path)
                self.assertEqual(path.stat().st_ino, inode)
                self.assertEqual(path.read_text(), 'existing owner reservation')
            finally:
                c.os.close(fd)


if __name__ == '__main__':
    unittest.main()

class SubprocessFailures(unittest.TestCase):
    def test_timeout_and_missing_tool_are_sanitized(self):
        for failure in (c.subprocess.TimeoutExpired('secret argv', 1, output='secret'), OSError('secret')):
            with patch.object(c.subprocess, 'run', side_effect=failure):
                with self.assertRaises(RuntimeError) as caught:
                    c.command(['gh', 'secret'])
                self.assertNotIn('secret', str(caught.exception))


class GateEvidence(unittest.TestCase):
    def test_missing_step_stale_head_and_quality_digest_fail(self):
        receipt = {'coverage': 'none', 'schemaVersion': 1, 'repository': 'harborline-api', 'lane': 'all', 'baseHead': HEAD, 'testedTree': 'b'*40, 'hostBaseline': 'eng/baselines/host-test-baseline.ubuntu.json', 'steps': ['boundaries', 'dependency-ledger', 'identity-r3', 'codegen-check', 'codegen-guard-suite', 'contracts-typescript', 'contracts-csharp', 'localfirst-csharp', 'rule-engine-conformance', 'contracts-rust', 'operator-cli-headless', 'install-artefact', 'removal-exercise', 'exact-clone', {'id': 'quality', 'decisionDigest': 'sha256:'+'1'*64, 'policyDigest': 'sha256:'+'2'*64}, 'quality-baseline', 'packages']}
        decision = {'decisionId': 'sha256:'+'1'*64, 'policyDigest': 'sha256:'+'2'*64}
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root/'gate-exit.txt').write_text('0\n')
            (root/'harborline-api-quality-decision.json').write_text(json.dumps(decision))
            def save(value):
                (root/'harborline-api-verify-receipt.json').write_text(json.dumps(value))
            save(receipt)
            c.validate_receipt(root, HEAD, 'b'*40)
            for i in range(17):
                changed = copy.deepcopy(receipt)
                del changed['steps'][i]
                save(changed)
                with self.subTest(omitted=i), self.assertRaises(RuntimeError):
                    c.validate_receipt(root, HEAD, 'b'*40)
            for key, value in [('baseHead', '0'*40), ('testedTree', 'c'*40), ('lane', 'shared'), ('hostBaseline', 'eng/baselines/host-test-baseline.json')]:
                save({**receipt, key: value})
                with self.assertRaises(RuntimeError):
                    c.validate_receipt(root, HEAD, 'b'*40)
            save(receipt)
            (root/'harborline-api-quality-decision.json').write_text(json.dumps({**decision, 'decisionId': 'sha256:'+'3'*64}))
            with self.assertRaises(RuntimeError):
                c.validate_receipt(root, HEAD, 'b'*40)

class BundlePreparation(unittest.TestCase):
    def test_exports_committed_heads_and_tags_without_touching_dirty_user_work(self):
        import argparse
        import hashlib
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            paths = {}
            heads = {}
            for name in ('control', 'quality', 'platform', 'api'):
                path = root/name
                path.mkdir()
                c.command(['git', 'init', '-q', str(path)])
                c.git(path, 'config', 'user.email', 'fixture@example.invalid')
                c.git(path, 'config', 'user.name', 'Fixture')
                (path/'file.txt').write_text('committed\n')
                if name == 'api':
                    (path/'eng').mkdir()
                    for pin in ('platform', 'quality'):
                        (path/'eng'/(pin+'-pin.json')).write_text(json.dumps({'commit': heads[pin]}))
                    (path/'global.json').write_text(json.dumps({'sdk': {'version': '11.0.100-rc.1.26425.128', 'rollForward': 'disable'}}))
                c.git(path, 'add', '.')
                c.git(path, 'commit', '-qm', 'fixture')
                c.git(path, 'tag', 'v0.1.0')
                paths[name] = str(path)
                heads[name] = c.git(path, 'rev-parse', 'HEAD')
            api = Path(paths['api'])
            (api/'file.txt').write_text('user change; preserve\n')
            (api/'untracked.txt').write_text('untracked user work\n')
            before = c.git(api, 'status', '--porcelain')
            archive = root/'runner.tar.gz'
            archive.write_bytes(b'fixture-archive')
            args = argparse.Namespace(run_id='42', control_head=heads['control'], runner_archive=str(archive), output=str(root/'output'), **paths)
            with patch.object(c, 'pending', return_value={**POLICY, 'head': heads['api']}), patch.object(c, 'RUNNER_SHA', hashlib.sha256(b'fixture-archive').hexdigest()):
                c.prepare(args)
            self.assertEqual(c.git(api, 'status', '--porcelain'), before)
            self.assertEqual((api/'file.txt').read_text(), 'user change; preserve\n')
            c.command(['git', 'clone', str(root/'output/bundles/api.bundle'), str(root/'copy')])
            self.assertEqual(c.git(root/'copy', 'describe', '--tags'), 'v0.1.0')
            self.assertEqual((root/'copy/file.txt').read_text(), 'committed\n')
            self.assertFalse((root/'copy/untracked.txt').exists())
            self.assertEqual(c.git(root/'copy', 'rev-parse', 'HEAD'), heads['api'])

class FailureTeardown(unittest.TestCase):
    def test_refusal_signals_init_even_if_marker_write_fails(self):
        with patch.object(admission.signal, 'signal'), patch.object(admission.Path, 'touch', side_effect=OSError('full volume')), patch.object(admission.os, 'kill') as kill, patch.object(admission.time, 'sleep', side_effect=RuntimeError('fixture ends blocked loop')):
            with self.assertRaisesRegex(RuntimeError, 'fixture ends'):
                admission.reject()
            kill.assert_called_once_with(1, admission.signal.SIGTERM)

    def test_export_directory_failure_still_tears_down(self):
        import argparse
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            policy = root/'policy.json'
            policy.write_text(json.dumps(POLICY))
            lock = root/'heavy.lock'
            lock.touch()
            args = argparse.Namespace(policy=str(policy), lock=str(lock), output=str(root/'evidence'), image='sha256:'+'a'*64)
            def api(path, method='GET'):
                if path.endswith('registration-token'):
                    return {'token': 'fixture-only'}
                if '/jobs?' in path:
                    return {'total_count': 1, 'jobs': [{'runner_name': 'hl-mini-'+'a'*32+'-a', 'conclusion': 'success'}]}
                return {'run_attempt': 1, 'head_sha': HEAD, 'status': 'completed', 'conclusion': 'success'}
            original_mkdir = Path.mkdir
            def mkdir(path, *args, **kwargs):
                if path.name == 'a':
                    raise OSError('full disk during export')
                return original_mkdir(path, *args, **kwargs)
            def docker(*args, **kwargs):
                return '2.338.0\n' if args[-1] == '--version' else ''
            with patch.object(c, 'pending', return_value=POLICY), patch.object(c, 'preflight_image', return_value={}), patch.object(c.uuid, 'uuid4', return_value=SimpleNamespace(hex='a'*32)), patch.object(c, 'docker', side_effect=docker), patch.object(c, 'api', side_effect=api), patch.object(c, 'cleanup', return_value={'clean': True}) as cleanup, patch.object(Path, 'mkdir', mkdir):
                with self.assertRaisesRegex(RuntimeError, 'Run or verified cleanup failed'):
                    c.execute(args)
                cleanup.assert_called_once_with('a'*32)
            self.assertFalse(json.loads((root/'evidence/result.json').read_text())['success'])
            # Reservation released even though the export failed.
            fd = c.reserve(lock)
            c.os.close(fd)
