#!/usr/bin/env python3
"""Opt-in small local container controls. No GitHub API or registration credentials.
Uses an existing reviewed runner image; does not download or install tools.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

from test_controller import ENV, EVENT, POLICY

ROOT = Path(__file__).resolve().parents[1]


def run(*args, check=True, input=None):
    result = subprocess.run(['docker', *args], input=input, text=True, capture_output=True, timeout=90)
    if check and result.returncode:
        raise RuntimeError('Fixture Docker operation failed: '+result.stderr[-1000:])
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    if not args.base.startswith('sha256:'):
        raise SystemExit('Use the existing runner image ID')
    session = 'mini-fixture-'+uuid.uuid4().hex[:12]
    names = [session+'-a', session+'-b']
    results = []
    image = session+':local'
    base_tag = session+'-base:local'
    try:
        run('image', 'tag', args.base, base_tag)
        with tempfile.TemporaryDirectory() as temp:
            context = Path(temp)
            for name in ('admission.py', 'entrypoint.sh', 'start-hook.sh'):
                shutil.copyfile(ROOT/name, context/name)
            (context/'policy.json').write_text(json.dumps(POLICY))
            (context/'run.sh').write_text('#!/bin/bash\nset -eu\nsetsid bash -c \'trap "" TERM INT; while :; do sleep 1; done\' &\ntouch /runner/FIXTURE_RUNNING\nwait\n')
            (context/'Dockerfile').write_text('FROM '+base_tag+'\nUSER root\nCOPY admission.py entrypoint.sh start-hook.sh policy.json /opt/mini/\nCOPY run.sh /opt/actions-runner/run.sh\nRUN chmod -R a+rX,a-w /opt/mini /opt/actions-runner && chmod 555 /opt/mini/*.sh /opt/actions-runner/run.sh\nUSER 1001:1001\nENTRYPOINT ["/opt/mini/entrypoint.sh"]\n')
            run('build', '--network=none', '-t', image, str(context))
        for name in names:
            run('volume', 'create', '--label', 'harborline.fixture='+session, name)
            run('run', '-d', '--name', name, '--label', 'harborline.fixture='+session, '--init', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--network', 'none', '--memory', '256m', '--cpus', '1', '--pids-limit', '64', '--mount', 'type=volume,src='+name+',dst=/runner', '--tmpfs', '/tmp:rw,nosuid,nodev,size=32m,mode=1777', image)
            run('exec', name, 'timeout', '15', 'bash', '-c', 'until test -f /runner/READY; do sleep .1; done; test -w /runner && test -r /opt/mini/policy.json && ! test -w /opt/mini/admission.py')
            run('exec', name, '/runner/bin/Runner.Listener', '--version')
            run('exec', '-i', name, 'python3', '-c', 'import sys;open("/runner/event.json","w").write(sys.stdin.read())', input=json.dumps(EVENT))
            run('exec', name, 'touch', '/runner/START')
            run('exec', name, 'timeout', '5', 'bash', '-c', 'until test -f /runner/FIXTURE_RUNNING; do sleep .1; done')
        results.append('both nonroot runners initialized with immutable readable policy and writable private volumes')
        env = [item for key, value in {**ENV, 'GITHUB_EVENT_PATH': '/runner/event.json'}.items() for item in ('-e', key+'='+value)]
        allowed = run('exec', *env, names[0], '/opt/mini/start-hook.sh')
        if 'HARBORLINE_TRUSTED_RUN_ADMITTED' not in allowed.stdout:
            raise RuntimeError('Trusted hook did not admit')
        results.append('literal trusted hook admitted')
        refused = env+['-e', 'GITHUB_RUN_ATTEMPT=2']
        run('exec', *refused, names[0], '/opt/mini/start-hook.sh', check=False)
        for _ in range(50):
            if run('inspect', '-f', '{{.State.Running}}', names[0]).stdout.strip() == 'false':
                break
            time.sleep(.1)
        else:
            raise RuntimeError('Admission denial did not stop whole container')
        if run('inspect', '-f', '{{.State.Running}}', names[1]).stdout.strip() != 'true':
            raise RuntimeError('Denial affected independent peer')
        results.append('wrong attempt stopped whole container with detached TERM-ignoring child; peer survived')
        run('stop', '-t', '2', names[1])
        if run('inspect', '-f', '{{.State.Running}}', names[1]).stdout.strip() != 'false':
            raise RuntimeError('Cancellation failed')
        results.append('docker stop terminated peer and detached child')
    finally:
        for name in names:
            run('rm', '-f', name, check=False)
            run('volume', 'rm', name, check=False)
        run('image', 'rm', image, check=False)
        run('image', 'rm', base_tag, check=False)
        remaining = run('ps', '-a', '--filter', 'label=harborline.fixture='+session, '--format', '{{.Names}}').stdout.strip()
        volumes = run('volume', 'ls', '--filter', 'label=harborline.fixture='+session, '--format', '{{.Name}}').stdout.strip()
        if remaining or volumes:
            raise RuntimeError('Fixture cleanup incomplete')
    results.append('independent labelled inventories confirm containers and volumes absent')
    Path(args.output).write_text(json.dumps({'passed': results, 'limits': 'Synthetic listener only; actual GitHub Runner.Worker hook integration and full gate remain unqualified.'}, indent=2)+'\n')
    print(json.dumps(results, indent=2))


if __name__ == '__main__':
    main()
