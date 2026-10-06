"""Immutable, exact-run admission; denial terminates the container via its init."""
import json
import os
from pathlib import Path
import signal
import time

REPO = 'Harborline-Software/harborline-api'
WORKFLOW = '.github/workflows/mini-portable-gate.yml'


def admitted(policy, env, event):
    if not isinstance(policy, dict) or policy.get('version') != 1:
        return False
    head, run_id = policy.get('head'), policy.get('runId')
    if not isinstance(head, str) or len(head) != 40 or any(c not in '0123456789abcdef' for c in head):
        return False
    if not isinstance(run_id, str) or not run_id.isascii() or not run_id.isdecimal() or int(run_id) < 1:
        return False
    expected = {
        'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REPOSITORY': REPO,
        'GITHUB_REPOSITORY_ID': '1360432948', 'GITHUB_REF': 'refs/heads/main',
        'GITHUB_ACTOR': 'ctwoodwa', 'GITHUB_ACTOR_ID': '1328090',
        'GITHUB_TRIGGERING_ACTOR': 'ctwoodwa', 'GITHUB_JOB': 'portable',
        'GITHUB_WORKFLOW_REF': f'{REPO}/{WORKFLOW}@refs/heads/main',
        'GITHUB_SHA': head, 'GITHUB_WORKFLOW_SHA': head,
        'GITHUB_RUN_ID': run_id, 'GITHUB_RUN_ATTEMPT': '1',
    }
    if any(env.get(k) != v for k, v in expected.items()) or not isinstance(event, dict):
        return False
    return (event.get('repository', {}).get('id') == 1360432948
            and event.get('repository', {}).get('full_name') == REPO
            and event.get('repository', {}).get('fork') is False
            and event.get('sender', {}).get('id') == 1328090
            and event.get('sender', {}).get('login') == 'ctwoodwa'
            and event.get('ref') == 'refs/heads/main'
            and not event.get('inputs')
            and 'pull_request' not in event and 'workflow_run' not in event)


def reject(*_):
    # docker --init forwards TERM to entrypoint.sh. Its trap exits immediately,
    # so container teardown kills the whole PID namespace, including detached
    # children. No process ancestry scanning or host process signals.
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    try:
        Path('/runner/ADMISSION_CLOSED').touch()
    except OSError:
        pass  # Marker is diagnostic; a full volume must not bypass teardown.
    try:
        os.kill(1, signal.SIGTERM)
    except OSError:
        pass  # Never return from a refused hook, even if init signalling fails.
    while True:
        time.sleep(1)


def main():
    signal.signal(signal.SIGTERM, reject)
    signal.signal(signal.SIGINT, reject)
    try:
        policy = json.loads(Path('/opt/mini/policy.json').read_text())
        event_path = Path(os.environ['GITHUB_EVENT_PATH'])
        if event_path.stat().st_size > 1048576:
            raise ValueError('oversize event')
        if admitted(policy, os.environ, json.loads(event_path.read_text())):
            print('HARBORLINE_TRUSTED_RUN_ADMITTED', flush=True)
            return
    except (OSError, KeyError, ValueError, TypeError, AttributeError):
        pass
    reject()


if __name__ == '__main__':
    main()
