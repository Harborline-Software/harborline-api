"""Immutable, exact-run admission; denial terminates the container via its init."""
import json
import os
from pathlib import Path
import re
import signal
import time

REPO = 'Harborline-Software/harborline-api'
WORKFLOW = '.github/workflows/mini-portable-gate.yml'
CANDIDATE_WORKFLOW = '.github/workflows/mini-candidate-gate.yml'


def candidate_shape(candidate):
    """Small, deliberately restricted contract: owner PR or one-PR squash group.

    This validates structure, not authorization. The host independently checks
    the reviewed candidate against live GitHub state before issuing credentials.
    """
    if not isinstance(candidate, dict) or set(candidate) != {
            'event', 'prNumber', 'prHead', 'prMerge', 'head', 'base', 'ref', 'headBranch',
            'workflowHead', 'workflowRef'}:
        return False
    if type(candidate['prNumber']) is not int or candidate['prNumber'] < 1:
        return False
    if any(not isinstance(candidate[k], str) or not re.fullmatch('[0-9a-f]{40}', candidate[k])
           for k in ('prHead', 'prMerge', 'head', 'base', 'workflowHead')):
        return False
    if candidate['base'] in (candidate['head'], candidate['prHead']):
        return False
    event = candidate['event']
    if event == 'pull_request':
        if candidate['prMerge'] != candidate['head']:
            return False
        ref = f"refs/pull/{candidate['prNumber']}/merge"
        if not isinstance(candidate['headBranch'], str) or not re.fullmatch(
                r'pipeline/mini-[A-Za-z0-9._/-]+', candidate['headBranch']):
            return False
    elif event == 'merge_group':
        branch = f"gh-readonly-queue/main/pr-{candidate['prNumber']}-{candidate['base']}"
        if candidate['headBranch'] != branch:
            return False
        ref = 'refs/heads/'+branch
    else:
        return False
    # This qualification workflow is sourced from the tested synthetic commit.
    # Other workflow-authority shapes need a separately reviewed contract.
    return (candidate['ref'] == ref and candidate['workflowHead'] == candidate['head']
            and candidate['workflowRef'] == f'{REPO}/{CANDIDATE_WORKFLOW}@{ref}')


def candidate_admitted(policy, env, event):
    candidate = policy.get('candidate')
    if not candidate_shape(candidate) or policy.get('head') != candidate['head']:
        return False
    if policy.get('coverage') is not (candidate['event'] == 'merge_group'):
        return False
    run_id = policy.get('runId')
    if not isinstance(run_id, str) or not re.fullmatch('[1-9][0-9]*', run_id):
        return False
    expected = {
        'GITHUB_EVENT_NAME': candidate['event'], 'GITHUB_REPOSITORY': REPO,
        'GITHUB_REPOSITORY_ID': '1360432948', 'GITHUB_REF': candidate['ref'],
        'GITHUB_ACTOR': 'ctwoodwa', 'GITHUB_ACTOR_ID': '1328090',
        'GITHUB_TRIGGERING_ACTOR': 'ctwoodwa', 'GITHUB_JOB': 'portable',
        'GITHUB_WORKFLOW_REF': candidate['workflowRef'],
        'GITHUB_SHA': candidate['head'], 'GITHUB_WORKFLOW_SHA': candidate['workflowHead'],
        'GITHUB_RUN_ID': run_id, 'GITHUB_RUN_ATTEMPT': '1',
    }
    if any(env.get(k) != v for k, v in expected.items()) or not isinstance(event, dict):
        return False
    if (event.get('repository', {}).get('id') != 1360432948
            or event.get('repository', {}).get('full_name') != REPO
            or event.get('repository', {}).get('fork') is not False
            or event.get('sender', {}).get('id') != 1328090
            or event.get('sender', {}).get('login') != 'ctwoodwa'
            or 'workflow_run' in event or 'inputs' in event):
        return False
    if candidate['event'] == 'pull_request':
        pr = event.get('pull_request', {})
        return (event.get('action') in ('opened', 'synchronize', 'reopened', 'ready_for_review')
                and event.get('number') == candidate['prNumber']
                and pr.get('number') == candidate['prNumber']
                and pr.get('state') == 'open' and pr.get('draft') is False
                and pr.get('user', {}).get('id') == 1328090
                and pr.get('user', {}).get('login') == 'ctwoodwa'
                and pr.get('head', {}).get('sha') == candidate['prHead']
                and pr.get('head', {}).get('ref') == candidate['headBranch']
                and pr.get('head', {}).get('repo', {}).get('id') == 1360432948
                and pr.get('head', {}).get('repo', {}).get('fork') is False
                and pr.get('base', {}).get('sha') == candidate['base']
                and pr.get('base', {}).get('ref') == 'main'
                and pr.get('base', {}).get('repo', {}).get('id') == 1360432948
                and env.get('GITHUB_BASE_REF') == 'main'
                and env.get('GITHUB_HEAD_REF') == candidate['headBranch']
                and 'merge_group' not in event)
    group = event.get('merge_group', {})
    return (event.get('action') == 'checks_requested'
            and group.get('head_sha') == candidate['head']
            and group.get('head_ref') == candidate['ref']
            and group.get('base_sha') == candidate['base']
            and group.get('base_ref') == 'refs/heads/main'
            and 'pull_request' not in event)


def admitted(policy, env, event):
    if isinstance(policy, dict) and policy.get('version') == 2:
        return candidate_admitted(policy, env, event)
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
