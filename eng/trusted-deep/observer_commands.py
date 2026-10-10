"""Failure-only diagnostics for the observer's fixed read-only operations.

Never retain argv, environment, exception text or arbitrary captured output.
Child timeout cleanup mirrors subprocess.run; this adds no retry or deadline.
"""
import datetime
import json
import os
import re
import subprocess
import time

OPERATIONS = {
    'host.memory-pressure': 'memory_pressure', 'host.swap-usage': 'sysctl',
    'host.pressure-level': 'sysctl', 'host.vm-stat': 'vm_stat',
    'docker.session-list': 'docker', 'docker.session-inspect': 'docker',
    'docker.running-list': 'docker', 'docker.telemetry-exec': 'docker',
    'docker.aggregate-stats': 'docker',
}
PHASES = {'baseline', 'container-discovery', 'container-status', 'process-sample',
          'host-sample', 'docker-stats'}
STATES = {'created', 'running', 'paused', 'restarting', 'removing', 'exited', 'dead'}


def partial_output(value, session):
    """Allowlist benign fragments; incomplete JSON/free text stays redacted.

    Unlike token-pattern replacement, this also suppresses unmarked secrets.
    Byte counts and truncation retain the shape of unavailable partial output.
    """
    raw = value if isinstance(value, bytes) else (value or '').encode('utf-8', errors='replace')
    text = raw[:4096].decode('utf-8', errors='replace')
    container = 'hl-mini-' + session + '-a' if session else None
    lines = []
    for line in text.splitlines():
        if (line in {'context deadline exceeded', 'request canceled', 'operation timed out'}
                or line == container):
            lines.append(line)
        elif container and line == 'Error response from daemon: Container ' + container + ' is not running':
            lines.append('owned container is not running')
        else:
            lines.append('<redacted>')
    return {'bytesObserved': len(raw), 'truncated': len(raw) > 4096,
            'text': '\n'.join(lines)[:4096]}


class Commands:
    def __init__(self, output, context, *, spawn=None, clock=None):
        self.output, self.context = output, context
        self.spawn = subprocess.Popen if spawn is None else spawn
        self.clock = time.monotonic if clock is None else clock
        self.last_failure = None

    def failed(self, operation, timeout, elapsed, error_type, child, stdout, stderr, errno=None):
        context = self.context
        session = context.get('session')
        session = session if isinstance(session, str) and re.fullmatch('[0-9a-f]{32}', session) else None
        operation = operation if operation in OPERATIONS else 'unknown'
        phase = context.get('phase') if context.get('phase') in PHASES else 'unknown'
        state = context.get('containerStatus') if context.get('containerStatus') in STATES else None
        pid = context.get('controllerPid')
        record = {'observedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  'operation': operation, 'executable': OPERATIONS.get(operation, 'unknown'),
                  'timeoutSeconds': timeout, 'elapsedSeconds': elapsed, 'errorType': error_type,
                  'observerPid': os.getpid(), 'childPid': child.pid if child else None,
                  'childReturnCode': child.returncode if child else None,
                  'controllerPid': pid if type(pid) is int and pid > 0 else None,
                  'session': session, 'container': 'hl-mini-' + session + '-a' if session else None,
                  'observerPhase': phase, 'lastContainerStatus': state,
                  'stdout': partial_output(stdout, session), 'stderr': partial_output(stderr, session)}
        if type(errno) is int:
            record['errno'] = errno
        self.last_failure = record
        try:
            fd = os.open(self.output, os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, 'w') as stream:
                stream.write(json.dumps(record) + '\n')
        except OSError:
            # The existing summary still retains this record; never mask the alarm.
            record['diagnosticWriteFailed'] = True

    def run(self, argv, operation, timeout=15):
        started = self.clock()
        child = None
        try:
            with self.spawn(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as child:
                try:
                    stdout, stderr = child.communicate(timeout=timeout)
                except subprocess.TimeoutExpired as error:
                    elapsed = self.clock() - started
                    child.kill()
                    # This observer runs on macOS; match subprocess.run's POSIX cleanup.
                    child.wait()
                    self.failed(operation, timeout, elapsed, 'TimeoutExpired', child, error.output, error.stderr)
                    raise
                except BaseException:
                    child.kill()
                    raise
                if child.returncode:
                    self.failed(operation, timeout, self.clock() - started, 'NonzeroExit', child, stdout, stderr)
                    raise RuntimeError('Read-only telemetry command failed: ' + OPERATIONS.get(operation, 'unknown'))
                return stdout
        except OSError as error:
            self.failed(operation, timeout, self.clock() - started, 'OSError', child, None, None, error.errno)
            raise
