"""Literal Apple SDK structure corpus and blocked probe oracle; no payload."""
import ctypes
from pathlib import Path
import sys
import threading
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from host_diagnostics import BsdInfo, TaskInfo, HostProcesses, memory_facts, read_processes, safe_name

class Library:
    def __init__(self, count=100, changed=False, short=False):
        self.count, self.changed, self.short = count, changed, short
        self.reads = {}
    def proc_listallpids(self, buffer, size):
        assert size == 16384
        for n in range(min(self.count, 4096)): buffer[n] = n + 1
        return self.count
    def proc_pidinfo(self, pid, flavor, arg, buffer, size):
        assert arg == 0
        if flavor == 3:
            assert size == 136
            row = ctypes.cast(buffer, ctypes.POINTER(BsdInfo)).contents
            self.reads[pid] = self.reads.get(pid, 0) + 1
            row.pid, row.parentPid, row.name = pid, 7, b'Example App'
            row.startSeconds = 500 + (1 if self.changed and self.reads[pid] > 1 else 0)
        else:
            assert flavor == 4 and size == 96
            row = ctypes.cast(buffer, ctypes.POINTER(TaskInfo)).contents
            row.residentBytes = pid * 1024
        return size - 1 if self.short else size

class HostDiagnosticTests(unittest.TestCase):
    def test_memory_units_and_missing_fields_are_literal(self):
        text = 'Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages wired down: 3.\nPages occupied by compressor: 7.\nPages stored in compressor: 11.\nFile-backed pages: 5.\n'
        value = memory_facts(text)
        self.assertEqual(value['wiredBytes'], 49152)
        self.assertEqual(value['compressorBytes'], 114688)
        self.assertEqual(value['uncompressedStoredBytes'], 180224)
        self.assertEqual(value['fileBackedBytes'], 81920)
        self.assertIsNone(value['freeBytes'])
        self.assertEqual(memory_facts('secret unrelated text'), {'available': False})
    def test_sdk_literal_layouts_and_name_redaction(self):
        self.assertEqual(ctypes.sizeof(BsdInfo), 136)
        self.assertEqual(ctypes.sizeof(TaskInfo), 96)
        self.assertEqual((BsdInfo.pid.offset, BsdInfo.parentPid.offset, BsdInfo.comm.offset, BsdInfo.name.offset), (12, 16, 48, 64))
        self.assertEqual((BsdInfo.startSeconds.offset, TaskInfo.residentBytes.offset), (120, 8))
        self.assertEqual(safe_name(b'Example App'), 'Example App')
        self.assertEqual(safe_name(b'ghs_fixture'), '<redacted>')
        self.assertEqual(safe_name(b'bad\nname'), 'bad?name')
    def test_pid_parent_rss_bounds_and_highest_rss_first(self):
        value = read_processes(Library(), clock=lambda: 100)
        self.assertEqual(len(value['processes']), 32)
        self.assertEqual(value['processes'][0], {'pid': 100, 'parentPid': 7, 'rssBytes': 102400, 'name': 'Example App'})
        self.assertEqual(value['processes'][-1]['pid'], 69)
        self.assertTrue(value['truncated'])
        self.assertEqual(value['rowsSeen'], 100)
        self.assertFalse(value['collectionBudgetExhausted'])
    def test_short_reads_and_changed_process_identity_are_omitted(self):
        for library in [Library(count=1, short=True), Library(count=1, changed=True)]:
            self.assertEqual(read_processes(library, clock=lambda: 1)['processes'], [])
    def test_fixed_buffer_and_budget_do_not_expand(self):
        value = read_processes(Library(count=5000), clock=iter([1, 1.25]).__next__)
        self.assertEqual(value['processes'], [])
        self.assertTrue(value['collectionBudgetExhausted'])
        self.assertTrue(value['truncated'])
    def test_probe_failure_is_optional_without_exception_text(self):
        def collect(): raise OSError('secret exception')
        value = HostProcesses(collect=collect, clock=iter([1, 1.25]).__next__)
        value._collect()
        self.assertEqual(value.snapshot(), {'startedAt': 1, 'completedAt': 1.25,
                                          'status': 'unavailable', 'reason': 'probe-failed'})
    def test_thread_start_failure_is_optional_without_exception_text(self):
        for target in ['host_diagnostics.threading.Thread', 'host_diagnostics.threading.Thread.start']:
            with self.subTest(target=target):
                value = HostProcesses()
                with patch(target, side_effect=RuntimeError('secret exhaustion')):
                    value.refresh()
                self.assertEqual(value.snapshot(), {'status': 'unavailable', 'reason': 'probe-start-failed'})
    def test_blocked_probe_is_not_joined_and_never_duplicated(self):
        entered, release = threading.Event(), threading.Event()
        calls = []
        def collect():
            calls.append(1); entered.set(); release.wait()
            return {'processes': []}
        value = HostProcesses(collect=collect)
        try:
            value.refresh(); self.assertTrue(entered.wait(1))
            first = value.worker
            for _ in range(100):
                value.refresh()
                self.assertEqual(value.snapshot(), {'status': 'not-yet-collected'})
            self.assertIs(value.worker, first)
            self.assertTrue(first.daemon)
            self.assertEqual(calls, [1])
        finally:
            release.set(); value.worker.join(1)
if __name__ == '__main__': unittest.main()
