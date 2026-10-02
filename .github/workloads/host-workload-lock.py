"""One machine lock for heavy background and quiet perf across repositories.
POSIX exec inherits the kernel lock through the complete command tree.
Windows supports orderly completion only; policy permits one agent there.
"""
import os, sys, time, subprocess
from pathlib import Path

def main():
    args = sys.argv[1:]
    if not args or args[0] != '--' or len(args) < 2:
        raise SystemExit('usage: python host-workload-lock.py -- command [args...]')
    default = str(Path.home() / '.harborline-workloads') if os.name == 'nt' else '/Users/Shared/Harborline-workloads' if sys.platform == 'darwin' else '/var/tmp/harborline-workloads'
    directory = Path(os.environ.get('HARBORLINE_HOST_LOCK_DIR', default))
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    descriptor = os.open(directory / 'heavy.lock', os.O_RDWR | os.O_CREAT | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    if os.name == 'nt':
        import msvcrt
        if os.fstat(descriptor).st_size == 0: os.write(descriptor, b'0')
        def acquire():
            os.lseek(descriptor, 0, 0)
            msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
    else:
        import fcntl
        def acquire(): fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
    deadline = time.monotonic() + float(os.environ.get('HARBORLINE_HOST_LOCK_WAIT_SECONDS', '900'))
    waiting = False
    while True:
        try:
            acquire()
            break
        except (BlockingIOError, PermissionError):
            if not waiting:
                print('Waiting: another heavy/perf workload holds this machine lock', file=sys.stderr, flush=True)
                waiting = True
            if time.monotonic() >= deadline:
                raise SystemExit('Machine reservation timeout; command did not start')
            time.sleep(0.1)
    print('Machine reservation acquired', file=sys.stderr, flush=True)
    if os.name != 'nt':
        os.set_inheritable(descriptor, True)
        os.execvp(args[1], args[1:])
    try:
        return subprocess.call(args[1:])
    finally:
        os.close(descriptor)

if __name__ == '__main__': sys.exit(main())
