import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import reclaim


def fixture():
    binding = {'runId': '42', 'jobId': '99', 'jobKey': 'deep', 'workflowSha': 'c' * 40,
               'workflowRef': 'Harborline-Software/harborline-control/.github/workflows/api-trusted-deep.yml@refs/heads/main',
               'taskId': 'portable-a', 'manifestSha256': 'd' * 64}
    assignment = {'verified': True, 'binding': copy.deepcopy(binding), 'runnerId': 17,
                  'runnerName': 'hl-trusted-42-99'}
    value = {'sources': {'api': 'a' * 40, 'control': 'c' * 40}, 'tree': 'b' * 40,
             'sdk': '11.0.100-rc.1.26425.128', 'inputDigests': {'environment': 'e' * 64},
             'tasks': [{'id': 'portable-a', 'kind': 'portable'}]}
    policy = {'head': 'a' * 40, 'tree': 'b' * 40, 'sdk': '11.0.100-rc.1.26425.128',
              'inputDigests': {'environment': 'e' * 64},
              'environment': {'privateBuildServerReclamation': 'exact-clone-host-tests'}}
    env = {'HARBORLINE_PRIVATE_BUILD_RECLAIM': '1', 'HARBORLINE_TASK_ID': 'portable-a',
           'HARBORLINE_APPROVED_MANIFEST_SHA256': 'd' * 64,
           'RUNNER_NAME': 'hl-trusted-42-99',
           'DOTNET_CLI_HOME': '/runner/gate/cache/dotnet', 'TMPDIR': '/runner/gate/tmp/'}
    return [binding, assignment, value, policy, env, 1001, 'linux',
            ('10737418240', '0', '500000 100000')]


class Reclamation(unittest.TestCase):
    def test_writable_or_retargeted_authority_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'binding.json'; path.write_text('{}')
            path.chmod(0o666)
            with self.assertRaises(ValueError): reclaim.protected_file(path, path)
            with self.assertRaisesRegex(ValueError, 'target'):
                reclaim.protected_file(path, '/runner/control/binding.json')

    def test_literal_private_profile_is_admitted(self):
        reclaim.validate(*fixture())

    def test_host_other_user_wrong_cgroup_and_endpoint_are_refused(self):
        changes = [(5, 0), (5, 501), (6, 'darwin'), (7, ('10737418240', '1', '500000 100000'))]
        for index, value in changes:
            case = fixture(); case[index] = value
            with self.subTest(index=index, value=value), self.assertRaises(ValueError):
                reclaim.validate(*case)
        for key, value in [('DOTNET_CLI_HOME', '/Users/chris'), ('TMPDIR', '/tmp/'),
                           ('HARBORLINE_TASK_ID', 'other'), ('RUNNER_NAME', 'another-runner'),
                           ('HARBORLINE_APPROVED_MANIFEST_SHA256', 'f' * 64)]:
            case = fixture(); case[4][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): reclaim.validate(*case)

    def test_unverified_assignment_changed_source_and_missing_profile_are_refused(self):
        for change in ('assignment', 'source', 'profile', 'task'):
            case = fixture()
            if change == 'assignment': case[1]['verified'] = False
            if change == 'source': case[2]['sources']['control'] = 'f' * 40
            if change == 'profile': case[3]['environment'] = {}
            if change == 'task': case[2]['tasks'][0]['kind'] = 'mutation-benchmark'
            with self.subTest(change=change), self.assertRaises(ValueError): reclaim.validate(*case)

    def test_shutdown_is_fixed_bounded_and_no_signals_are_used(self):
        binding, _, value, *_ = fixture(); events = []
        observations = iter([[{'pid': 12, 'kind': 'compiler', 'rssKiB': 900}], []])
        def command(argv, **kwargs):
            events.append(argv)
            self.assertEqual(argv, ['/usr/share/dotnet/dotnet', 'build-server', 'shutdown'])
            self.assertEqual(kwargs, {'capture_output': True, 'text': True, 'check': True, 'timeout': 45})
            return SimpleNamespace(returncode=0, stdout='fixture shutdown', stderr='')
        result = reclaim.shutdown(binding, value, runner=command, observe=lambda: next(observations), clock=lambda: 1)
        reclaim.validate_result(result, binding, value)
        self.assertEqual(len(events), 1); self.assertEqual(result['after'], [])
        self.assertIs(result['signalsSent'], False)
        result['after'] = [{'pid': 12}]
        with self.assertRaises(ValueError): reclaim.validate_result(result, binding, value)

    def test_command_failure_and_timeout_do_not_become_success(self):
        binding, _, value, *_ = fixture()
        for failure in (subprocess.CalledProcessError(1, 'fixture'), subprocess.TimeoutExpired('fixture', 45)):
            def command(*args, **kwargs): raise failure
            with self.subTest(failure=type(failure)), self.assertRaises(type(failure)):
                reclaim.shutdown(binding, value, runner=command, observe=lambda: [], clock=lambda: 1)

    def test_remaining_server_refuses_after_bounded_wait(self):
        binding, _, value, *_ = fixture(); now = [0]
        def wait(seconds): now[0] += 20
        with self.assertRaisesRegex(ValueError, 'did not drain'):
            reclaim.shutdown(binding, value, runner=lambda *a, **k: SimpleNamespace(returncode=0, stdout='', stderr=''),
                             observe=lambda: [{'pid': 12}], clock=lambda: now[0], pause=wait)
        self.assertLessEqual(now[0], 60)

    def test_process_inventory_excludes_testhost_other_uid_and_msbuild_main(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for pid, uid, argv in [(11, 1001, ['dotnet', '/sdk/VBCSCompiler.dll']),
                                   (12, 1001, ['dotnet', '/sdk/MSBuild.dll', '/nodemode:1']),
                                   (13, 1001, ['dotnet', '/sdk/testhost.dll']),
                                   (14, 501, ['dotnet', '/sdk/VBCSCompiler.dll']),
                                   (15, 1001, ['dotnet', '/sdk/MSBuild.dll', 'test'])]:
                p = root / str(pid); p.mkdir()
                (p/'status').write_text('Uid:\t'+'\t'.join([str(uid)]*4)+'\nVmRSS:\t123 kB\n')
                (p/'cmdline').write_bytes(b'\0'.join(x.encode() for x in argv)+b'\0')
            self.assertEqual(reclaim.owned_servers(root), [
                {'pid': 11, 'kind': 'compiler', 'rssKiB': 123},
                {'pid': 12, 'kind': 'msbuild-worker', 'rssKiB': 123}])


if __name__ == '__main__': unittest.main()
