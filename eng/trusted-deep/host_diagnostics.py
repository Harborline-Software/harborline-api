"""Optional bounded facts; never awaited by resource admission or stopping."""
import ctypes
import re
import threading
import time


def memory_facts(text):
    match = re.search(r'page size of (\d+) bytes', text)
    if not match:
        return {'available': False}
    size = int(match[1])
    keys = {'Pages wired down': 'wiredBytes', 'Pages occupied by compressor': 'compressorBytes',
            'Pages stored in compressor': 'uncompressedStoredBytes', 'File-backed pages': 'fileBackedBytes',
            'Pages free': 'freeBytes', 'Pages active': 'activeBytes', 'Pages inactive': 'inactiveBytes'}
    result = {'available': True, 'pageSizeBytes': size}
    for source, target in keys.items():
        value = re.search(r'^' + re.escape(source) + r':\s+(\d+)\.', text, re.M)
        result[target] = int(value[1]) * size if value else None
    return result


# Apple SDK sys/proc_info.h: proc_bsdinfo (136 bytes), proc_taskinfo (96).
class BsdInfo(ctypes.Structure):
    _fields_ = [('prefix', ctypes.c_uint32 * 3), ('pid', ctypes.c_uint32),
                ('parentPid', ctypes.c_uint32), ('identity', ctypes.c_uint32 * 7),
                ('comm', ctypes.c_char * 16), ('name', ctypes.c_char * 32),
                ('unused', ctypes.c_uint32 * 6), ('startSeconds', ctypes.c_uint64),
                ('startMicroseconds', ctypes.c_uint64)]


class TaskInfo(ctypes.Structure):
    _fields_ = [('virtualBytes', ctypes.c_uint64), ('residentBytes', ctypes.c_uint64),
                ('times', ctypes.c_uint64 * 4), ('unused', ctypes.c_int32 * 12)]


def safe_name(raw):
    name = re.sub(r'[^\w .()+-]', '?', raw.decode(errors='replace'))[:32]
    if any(token in name.lower() for token in ('ghp_', 'ghs_', 'github_pat_', 'bearer')):
        return '<redacted>'
    return name


def read_processes(lib=None, clock=time.monotonic):
    # Read fixed kernel structures, never argv, environment or executable paths.
    if lib is None:
        lib = ctypes.CDLL('/usr/lib/libproc.dylib')
        lib.proc_listallpids.argtypes = [ctypes.c_void_p, ctypes.c_int]
        lib.proc_listallpids.restype = ctypes.c_int
        lib.proc_pidinfo.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64,
                                    ctypes.c_void_p, ctypes.c_int]
        lib.proc_pidinfo.restype = ctypes.c_int
    assert ctypes.sizeof(BsdInfo) == 136 and ctypes.sizeof(TaskInfo) == 96
    pids = (ctypes.c_int * 4096)()
    deadline = clock() + .25
    count = lib.proc_listallpids(pids, ctypes.sizeof(pids))
    if count <= 0:
        raise OSError()
    rows = []
    exhausted = False
    for pid in pids[:min(count, 4096)]:
        if clock() >= deadline:
            exhausted = True
            break
        before, after, task = BsdInfo(), BsdInfo(), TaskInfo()
        if pid <= 0 or lib.proc_pidinfo(pid, 3, 0, ctypes.byref(before), 136) != 136:
            continue
        if lib.proc_pidinfo(pid, 4, 0, ctypes.byref(task), 96) != 96:
            continue
        if lib.proc_pidinfo(pid, 3, 0, ctypes.byref(after), 136) != 136:
            continue
        if (before.pid, before.startSeconds, before.startMicroseconds) != (pid, after.startSeconds, after.startMicroseconds) or after.pid != pid:
            continue
        rows.append({'pid': pid, 'parentPid': after.parentPid,
                     'rssBytes': task.residentBytes, 'name': safe_name(after.name or after.comm)})
    return {'processes': sorted(rows, key=lambda r: r['rssBytes'], reverse=True)[:32],
            'rowsSeen': len(rows), 'truncated': len(rows) > 32 or count >= 4096,
            'collectionBudgetExhausted': exhausted}


class HostProcesses:
    """One daemon worker maximum; snapshots are atomic and never wait or join."""
    def __init__(self, collect=read_processes, clock=time.time):
        self.collect, self.clock = collect, clock
        self.worker = None
        self.latest = {'status': 'not-yet-collected'}

    def snapshot(self):
        return self.latest

    def refresh(self):
        if self.worker is not None and self.worker.is_alive():
            return
        self.worker = threading.Thread(target=self._collect, daemon=True)
        self.worker.start()

    def _collect(self):
        started = self.clock()
        try:
            result = {'status': 'complete', **self.collect()}
        except Exception:
            result = {'status': 'unavailable', 'reason': 'probe-failed'}
        self.latest = {'startedAt': started, 'completedAt': self.clock(), **result}
