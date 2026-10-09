import errno
import json
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from observer_commands import Commands, partial_output

SESSION = 'abcdef0123456789abcdef0123456789'

class Child:
    """Pure fixture: no process or Docker command is launched."""
    pid = 421
    def __init__(self, error=None, returncode=0, stdout='', stderr=''):
        self.error, self.returncode, self.stdout, self.stderr = error, returncode, stdout, stderr
        self.kills = self.waits = 0
        self.timeouts = []
    def __enter__(self): return self
    def __exit__(self, *args): return False
    def communicate(self, timeout):
        self.timeouts.append(timeout)
        if self.error: raise self.error
        return self.stdout, self.stderr
    def kill(self):
        self.kills += 1
        self.returncode = -9
    def wait(self):
        self.waits += 1
        return self.returncode

class ObserverCommandsTests(unittest.TestCase):
    def commands(self, root, child, ticks=(100, 115.125), context=None):
        self.spawns = []
        def spawn(argv, **kwargs):
            self.spawns.append((argv, kwargs))
            return child
        clock = iter(ticks)
        return Commands(Path(root) / 'command-failures.jsonl', context or {
            'session': SESSION, 'controllerPid': 420,
            'phase': 'container-status', 'containerStatus': 'created'},
            spawn=spawn, clock=lambda: next(clock))

    def test_timeout_retains_identity_partial_output_and_original_alarm_without_retry(self):
        # Oracle: existing observer15s deadline, literal fixture IDs and injected clock delta.
        error = subprocess.TimeoutExpired('secret argv', 15,
            output=('hl-mini-' + SESSION + '-a\n').encode(), stderr=b'context deadline exceeded\n')
        child = Child(error=error)
        with tempfile.TemporaryDirectory() as root:
            commands = self.commands(root, child)
            with self.assertRaises(subprocess.TimeoutExpired) as caught:
                commands.run(['docker', 'inspect', 'secret argv'], 'docker.session-inspect')
            self.assertIs(caught.exception, error)
            record = json.loads(commands.output.read_text())
            self.assertEqual(record['operation'], 'docker.session-inspect')
            self.assertEqual((record['timeoutSeconds'], record['elapsedSeconds']), (15, 15.125))
            self.assertEqual((record['childPid'], record['controllerPid'], record['childReturnCode']), (421, 420, -9))
            self.assertEqual((record['lastContainerStatus'], record['observerPhase']), ('created', 'container-status'))
            self.assertEqual(record['stdout']['text'], 'hl-mini-' + SESSION + '-a')
            self.assertEqual(record['stderr']['text'], 'context deadline exceeded')
            self.assertNotIn('secret argv', commands.output.read_text())
            self.assertEqual(child.timeouts, [15])
            self.assertEqual((child.kills, child.waits, len(self.spawns)), (1, 1, 1))
            self.assertEqual(stat.S_IMODE(commands.output.stat().st_mode), 0o600)

    def test_nonzero_exit_keeps_refusal_and_suppresses_unmarked_secrets(self):
        child = Child(returncode=7, stdout='987654321098\n', stderr='Authorization: Bearer ghs_fixture\nunmarked-secret')
        with tempfile.TemporaryDirectory() as root:
            commands = self.commands(root, child, ticks=(100, 100.25))
            with self.assertRaisesRegex(RuntimeError, '^Read-only telemetry command failed: docker$'):
                commands.run(['docker', 'ps'], 'docker.session-list')
            record = commands.last_failure
            self.assertEqual((record['childReturnCode'], record['elapsedSeconds']), (7, 0.25))
            self.assertEqual(record['stdout']['text'], '<redacted>')
            for secret in ('987654321098', 'ghs_fixture', 'unmarked-secret'):
                self.assertNotIn(secret, commands.output.read_text())
            self.assertEqual((child.kills, child.waits, len(self.spawns)), (0, 0, 1))

    def test_launch_error_records_safe_errno_and_propagates_original(self):
        error = FileNotFoundError(errno.ENOENT, 'secret executable/path')
        with tempfile.TemporaryDirectory() as root:
            def spawn(*a, **k): raise error
            commands = Commands(Path(root) / 'command-failures.jsonl', {},
                spawn=spawn, clock=iter([1, 1.1]).__next__)
            with self.assertRaises(FileNotFoundError) as caught:
                commands.run(['secret executable/path'], 'secret operation')
            self.assertIs(caught.exception, error)
            record = commands.last_failure
            self.assertEqual((record['operation'], record['executable'], record['errno']), ('unknown', 'unknown', 2))
            self.assertIsNone(record['childPid'])
            self.assertNotIn('secret', commands.output.read_text())

    def test_success_preserves_original_output_without_diagnostic(self):
        with tempfile.TemporaryDirectory() as root:
            child = Child(stdout='raw original output')
            commands = self.commands(root, child, ticks=(1,))
            self.assertEqual(commands.run(['docker', 'ps'], 'docker.session-list'), 'raw original output')
            self.assertFalse(commands.output.exists())
            self.assertIsNone(commands.last_failure)
            self.assertEqual(child.timeouts, [15])

    def test_diagnostic_write_failure_does_not_mask_timeout_or_summary_record(self):
        error = subprocess.TimeoutExpired('hidden', 15)
        with tempfile.TemporaryDirectory() as root:
            commands = self.commands(root, Child(error=error))
            with patch('observer_commands.os.open', side_effect=OSError('secret disk error')):
                with self.assertRaises(subprocess.TimeoutExpired) as caught:
                    commands.run(['docker', 'ps'], 'docker.session-list')
            self.assertIs(caught.exception, error)
            self.assertTrue(commands.last_failure['diagnosticWriteFailed'])
            self.assertEqual(commands.last_failure['operation'], 'docker.session-list')
            self.assertNotIn('secret disk error', json.dumps(commands.last_failure))

    def test_partial_bytes_are_bounded_and_invalid_context_is_redacted(self):
        # Oracle: explicit4KiB bound; arbitrary JSON/text and numeric secrets must not be retained.
        output = partial_output(b'{"token":"ghp_fixture"}\n' + b'x' * 5000, SESSION)
        self.assertTrue(output['truncated'])
        self.assertLessEqual(len(output['text']), 4096)
        self.assertNotIn('ghp_fixture', output['text'])
        self.assertLessEqual(len(partial_output(b'\n' * 4096, SESSION)['text']), 4096)
        with tempfile.TemporaryDirectory() as root:
            commands = self.commands(root, Child(returncode=1), context={
                'session': 'secret', 'phase': 'secret', 'containerStatus': 'secret', 'controllerPid': 'secret'})
            with self.assertRaises(RuntimeError): commands.run(['docker'], 'docker.session-inspect')
            self.assertEqual(commands.last_failure['observerPhase'], 'unknown')
            self.assertIsNone(commands.last_failure['lastContainerStatus'])
            self.assertIsNone(commands.last_failure['controllerPid'])
            self.assertNotIn('secret', commands.output.read_text())

if __name__ == '__main__': unittest.main()
