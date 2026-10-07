"""Two machine-wide leases; legacy exclusive heavy work excludes both shared readers."""
from contextlib import contextmanager
import fcntl
import os
from pathlib import Path
import stat

DEFAULT = Path("/Users/Shared/Harborline-workloads")


def identity(fd, path):
    opened, current = os.fstat(fd), os.stat(path, follow_symlinks=False)
    if (not stat.S_ISREG(opened.st_mode) or not stat.S_ISREG(current.st_mode)
            or opened.st_nlink != 1 or (opened.st_dev, opened.st_ino) != (current.st_dev, current.st_ino)):
        raise RuntimeError("Machine reservation identity changed")


def opened(path, create=False):
    fd = os.open(path, os.O_RDWR | os.O_NOFOLLOW | (os.O_CREAT if create else 0), 0o600)
    try:
        identity(fd, path)
        return fd
    except BaseException:
        os.close(fd)
        raise


@contextmanager
def lease(root=DEFAULT, exclusive=False):
    root = Path(root)
    # Never create, replace, truncate or delete the existing canonical heavy.lock.
    heavy = opened(root / "heavy.lock")
    slot = None
    try:
        fcntl.flock(heavy, (fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH) | fcntl.LOCK_NB)
        for number in range(2):
            candidate = opened(root / f"trusted-deep-slot-{number}.lock", create=True)
            try:
                fcntl.flock(candidate, fcntl.LOCK_EX | fcntl.LOCK_NB)
                slot = candidate
                break
            except BlockingIOError:
                os.close(candidate)
        if slot is None:
            raise BlockingIOError("Both trusted deep slots are occupied")
        yield {"slot": number, "heavyFd": heavy, "slotFd": slot, "exclusive": exclusive}
    finally:
        # Close only; inherited file descriptions remain locked until all supervisors finish.
        if slot is not None:
            os.close(slot)
        os.close(heavy)


def inherited(record, root=DEFAULT):
    root = Path(root)
    identity(record["heavyFd"], root / "heavy.lock")
    identity(record["slotFd"], root / f"trusted-deep-slot-{record['slot']}.lock")
    # Prove the exclusive slot belongs to this inherited open file description.
    probe = opened(root / f"trusted-deep-slot-{record['slot']}.lock")
    try:
        try:
            fcntl.flock(probe, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            pass
        else:
            raise RuntimeError("Slot lease is not held")
    finally:
        os.close(probe)
    fcntl.flock(record["slotFd"], fcntl.LOCK_EX | fcntl.LOCK_NB)
    # Preserve SH mode. Converting to EX would incorrectly exclude the peer lane.
    fcntl.flock(record["heavyFd"], (fcntl.LOCK_EX if record["exclusive"] else fcntl.LOCK_SH) | fcntl.LOCK_NB)
    return (record["heavyFd"], record["slotFd"])
