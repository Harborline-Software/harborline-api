#!/usr/bin/env python3
"""Explicit prepare/run/recover CLI. Never dispatches or installs a service."""
import argparse
import base64
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import time
import uuid

from admission import REPO, WORKFLOW

ROOT = Path(__file__).resolve().parent
LABEL = 'org.harborline.mini.session'
RUNNER_SHA = '628b4a7258487b80c1d3c221095a7ded349f5ae0175fd3dfab708d07428f041b'


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def command(args, input=None):
    # Neither command lines nor captured failures are logged: registration uses
    # stdin, and gh errors can contain account data. Never enable shell tracing.
    try:
        result = subprocess.run(args, input=input, text=True, capture_output=True, timeout=90)
    except (subprocess.TimeoutExpired, OSError):
        raise RuntimeError(f'{Path(args[0]).name} operation unavailable or timed out') from None
    require(result.returncode == 0, f'{Path(args[0]).name} operation failed')
    return result.stdout


def docker(*args, input=None):
    return command(['docker', *args], input)


def api(path, method='GET'):
    output = command(['gh', 'api', '--method', method, 'repos/'+REPO+'/'+path])
    try:
        return json.loads(output) if output.strip() else None
    except ValueError:
        raise RuntimeError('GitHub returned an invalid response') from None


def runners():
    try:
        pages = json.loads(command(['gh', 'api', '--paginate', '--slurp', 'repos/'+REPO+'/actions/runners?per_page=100']))
        records = [r for page in pages for r in page['runners']]
        require(all(isinstance(r['name'], str) and isinstance(r['id'], int) for r in records), 'Invalid runner identity')
        return records
    except (ValueError, KeyError, TypeError):
        raise RuntimeError('GitHub runner inventory is invalid') from None


def write(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix+'.tmp')
    with temporary.open('w') as handle:
        json.dump(value, handle, indent=2)
        handle.write('\n')
        handle.flush()
        os.fsync(handle.fileno())
    temporary.replace(path)


def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as handle:
        for block in iter(lambda: handle.read(1048576), b''):
            h.update(block)
    return h.hexdigest()


def validate_run(run, jobs, main, workflow, reviewed):
    require(run['repository']['id'] == 1360432948 and run['head_repository']['id'] == 1360432948, 'Wrong repository')
    require(run['event'] == 'workflow_dispatch' and run['run_attempt'] == 1, 'Only first manual attempt allowed')
    require(run['head_branch'] == 'main' and run['head_sha'] == main and re.fullmatch('[0-9a-f]{40}', main), 'Run is not current main')
    require(run['path'] == WORKFLOW and workflow == reviewed, 'Workflow bytes require review')
    require(run['status'] in ('queued', 'waiting', 'pending', 'in_progress') and run['conclusion'] is None, 'Run is not pending')
    for actor in ('actor', 'triggering_actor'):
        require(run[actor]['id'] == 1328090 and run[actor]['login'] == 'ctwoodwa', 'Actor not admitted')
    require(len(jobs) == 1 and {j['name'] for j in jobs} == {'portable (a)'}, 'Unexpected job set')
    for job in jobs:
        require(job['status'] == 'queued' and not job.get('runner_id'), 'Job already assigned')
        lane = job['name'][-2]
        require(set(job['labels']) == {'self-hosted', 'harborline-api-mini-portable-v1', 'linux-arm64-orbstack', 'lane-'+lane}, 'Unexpected labels')
    return {'version': 1, 'runId': str(run['id']), 'head': main}


def pending(run_id):
    require(re.fullmatch('[1-9][0-9]*', run_id), 'Invalid run ID')
    run = api('actions/runs/'+run_id)
    require(str(run['id']) == run_id, 'Run ID mismatch')
    jobs = api('actions/runs/'+run_id+'/attempts/1/jobs?per_page=100')
    require(jobs['total_count'] == len(jobs['jobs']), 'Incomplete job inventory')
    main = api('git/ref/heads/main')['object']['sha']
    remote = api('contents/'+WORKFLOW+'?ref='+main)
    workflow = base64.b64decode(remote['content'])
    reviewed = (ROOT.parent.parent/WORKFLOW).read_bytes()
    policy = validate_run(run, jobs['jobs'], main, workflow, reviewed)
    config = json.loads(base64.b64decode(api('contents/global.json?ref='+main)['content']))['sdk']
    require(config['rollForward'] == 'disable', 'Review changed SDK policy')
    policy['sdk'] = config['version']
    policy['tree'] = api('git/commits/'+main)['tree']['sha']
    return policy


def git(path, *args):
    return command(['git', '-C', str(path), *args]).strip()


def prepare(args):
    policy = pending(args.run_id)
    require(re.fullmatch('[0-9a-f]{40}', args.control_head), 'An explicitly reviewed control head is required')
    require(digest(args.runner_archive) == RUNNER_SHA, 'Runner archive digest mismatch')
    paths = {name: Path(getattr(args, name)).resolve() for name in ('api', 'platform', 'quality', 'control')}
    require(git(paths['api'], 'rev-parse', 'HEAD') == policy['head'], 'API checkout must be at admitted main')
    expected = {'api': policy['head'], 'control': args.control_head}
    for name in ('platform', 'quality'):
        expected[name] = json.loads(git(paths['api'], 'show', 'HEAD:eng/'+name+'-pin.json'))['commit']
    for name, path in paths.items():
        require(git(path, 'rev-parse', 'HEAD') == expected[name], name+' checkout must be at approved pin')
    sdk = json.loads(git(paths['api'], 'show', 'HEAD:global.json'))['sdk']
    require(sdk['rollForward'] == 'disable', 'Review changed SDK policy')
    policy['sdk'] = sdk['version']
    policy['tree'] = git(paths['api'], 'rev-parse', 'HEAD^{tree}')
    target = Path(args.output)
    target.mkdir(mode=0o700)  # refuse overwrite / any prior session
    (target/'bundles').mkdir()
    sources = {}
    for name, path in paths.items():
        bundle = target/'bundles'/(name+'.bundle')
        git(path, 'bundle', 'create', str(bundle.resolve()), 'HEAD', '--tags')
        sources[name] = {'head': expected[name], 'bundleSha256': digest(bundle)}
    for name in ('Dockerfile', 'admission.py', 'entrypoint.sh', 'start-hook.sh', 'portable-gate.sh'):
        shutil.copyfile(ROOT/name, target/name)
    shutil.copyfile(args.runner_archive, target/'runner.tar.gz')
    write(target/'sources.json', sources)
    policy['sourcesSha256'] = digest(target/'sources.json')
    write(target/'policy.json', policy)
    print('Prepared immutable build context; no runner registered: '+str(target))


def resources(session):
    require(re.fullmatch('[0-9a-f]{32}', session), 'Invalid session identity')
    return [{'name': f'hl-mini-{session}-{lane}', 'lane': lane} for lane in ('a',)]


def inventory(kind, session):
    args = {'container': ('ps', '-a'), 'volume': ('volume', 'ls'), 'network': ('network', 'ls')}[kind]
    field = '{{.Names}}' if kind == 'container' else '{{.Name}}'
    # Successful full inventory is required; an inspect/daemon error is NOT absence.
    return set(docker(*args, '--filter', f'label={LABEL}={session}', '--format', field).splitlines())


def cleanup(session):
    rows = resources(session)
    names = {r['name'] for r in rows}
    failures = []
    for kind in ('container', 'volume', 'network'):
        try:
            found = inventory(kind, session)
            require(found <= names, 'Unexpected labelled resource; manual review required')
            for name in sorted(found):
                try:
                    # rm -f kills the entire container PID namespace. No host PID scan.
                    docker(*(('rm', '-f', name) if kind == 'container' else (kind, 'rm', name)))
                except RuntimeError:
                    failures.append(kind+' removal failed')
            require(not inventory(kind, session), kind+' remains')
        except RuntimeError as error:
            failures.append(str(error))
    try:
        for runner in runners():
            if runner['name'] in names:
                api('actions/runners/'+str(runner['id']), 'DELETE')
        require(not any(r['name'] in names for r in runners()), 'Runner registration remains')
    except RuntimeError:
        failures.append('GitHub deregistration unverified; retry recover')
    return {'session': session, 'clean': not failures, 'failures': failures}


def preflight_image(image, policy, session):
    require(re.fullmatch('sha256:[0-9a-f]{64}', image), 'Use an immutable local image ID')
    info = json.loads(docker('image', 'inspect', image))[0]
    require(info['Id'] == image and info['Architecture'] == 'arm64' and info['Os'] == 'linux', 'Image architecture/identity mismatch')
    require(info['Config']['User'] == '1001:1001' and not info['Config'].get('Volumes'), 'Unsafe image user/implicit mounts')
    require(info['Config']['Entrypoint'] == ['/opt/mini/entrypoint.sh'], 'Unexpected entrypoint')
    env = dict(item.split('=', 1) for item in info['Config'].get('Env', []))
    require(env.get('ACTIONS_RUNNER_HOOK_JOB_STARTED') == '/opt/mini/start-hook.sh'
            and env.get('HOME') == '/runner/home'
            and not env.get('ACTIONS_RUNNER_HOOK_JOB_COMPLETED'), 'Unexpected runner hook configuration')
    # No credentials, bind mounts or sockets in these bounded preflight containers.
    prefix = ('run', '--rm', '--name', resources(session)[0]['name'], '--label', f'{LABEL}={session}', '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--memory', '512m', '--pids-limit', '128', '--entrypoint')
    baked = json.loads(docker(*prefix, 'cat', image, '/opt/mini/policy.json'))
    require(baked == policy, 'Baked policy mismatch')
    sdk_lines = docker(*prefix, 'dotnet', image, '--list-sdks').splitlines()
    require(policy['sdk'] in [line.split()[0] for line in sdk_lines if line.split()], 'Pinned SDK absent; no registration token requested')
    sources = docker(*prefix, 'cat', image, '/opt/mini/sources.json')
    require(hashlib.sha256(sources.encode()).hexdigest() == policy['sourcesSha256'], 'Baked source manifest mismatch')
    # Compare all controller-owned executable bytes, not merely image labels.
    for name in ('admission.py', 'entrypoint.sh', 'start-hook.sh', 'portable-gate.sh'):
        require(docker(*prefix, 'cat', image, '/opt/mini/'+name).encode() == (ROOT/name).read_bytes(), 'Image runtime differs from reviewed code')
    return json.loads(sources)


def validate_receipt(directory, head, tree, coverage=False):
    receipt = json.loads((directory/'harborline-api-verify-receipt.json').read_text())
    decision = json.loads((directory/'harborline-api-quality-decision.json').read_text())
    require((directory/'gate-exit.txt').read_text().strip() == '0', 'Gate failed')
    require(receipt['schemaVersion'] == 1 and receipt['repository'] == 'harborline-api'
            and receipt['lane'] == 'all' and receipt['baseHead'] == head and receipt['testedTree'] == tree
            and receipt['hostBaseline'] == 'eng/baselines/host-test-baseline.ubuntu.json', 'Wrong receipt provenance')
    if coverage:
        for name in ('host', 'contracts'):
            item = receipt['coverage'][name]
            require(item['validLines'] > 0 and 0 <= item['coveredLines'] <= item['validLines'], 'Empty coverage evidence')
            require((directory/'quality'/(name+'.cobertura.xml')).is_file(), 'Coverage artifact absent')
    else:
        require(receipt['coverage'] == 'none', 'Unexpected coverage mode')
    # v1 qualification contract, independent of production-generated receipt.
    required = set('boundaries dependency-ledger identity-r3 codegen-check codegen-guard-suite contracts-typescript contracts-csharp localfirst-csharp rule-engine-conformance contracts-rust operator-cli-headless install-artefact removal-exercise exact-clone quality quality-baseline packages'.split())
    steps = receipt['steps']
    require({s if isinstance(s, str) else s['id'] for s in steps} >= required, 'Incomplete receipt')
    quality = next(s for s in steps if isinstance(s, dict) and s.get('id') == 'quality')
    for key in ('decisionId', 'policyDigest'):
        require(re.fullmatch('sha256:[0-9a-f]{64}', decision[key]), 'Invalid quality digest')
    require(quality['decisionDigest'] == decision['decisionId'] and quality['policyDigest'] == decision['policyDigest'], 'Quality decision mismatch')


def reserve(path):
    fd = os.open(path, os.O_RDWR | os.O_NOFOLLOW)
    try:
        require(stat.S_ISREG(os.fstat(fd).st_mode) and os.fstat(fd).st_ino == os.stat(path).st_ino, 'Reservation identity changed')
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return fd
    except BaseException:
        os.close(fd)
        raise


def execute(args):
    policy = json.loads(Path(args.policy).read_text())
    current = pending(policy['runId'])
    require(all(policy.get(k) == v for k, v in current.items()), 'Stale admission policy')
    session = uuid.uuid4().hex
    evidence = Path(args.output)
    evidence.mkdir(mode=0o700)
    write(evidence/'session.json', {'session': session, 'policy': policy, 'image': args.image, 'ttlSeconds': 3600})
    lock = reserve(args.lock)
    clean = None
    result = {'success': False}
    try:
        sources = preflight_image(args.image, policy, session)
        write(evidence/'sources.json', sources)
        # Recheck after slow preflight, before credentials. The run stays exact-main.
        require(pending(policy['runId']) == current, 'Admission changed during preflight')
        deadline = time.monotonic()+3300
        for row in resources(session):
            name = row['name']
            docker('volume', 'create', '--label', f'{LABEL}={session}', name)
            docker('network', 'create', '--label', f'{LABEL}={session}', name)
            docker('run', '-d', '--name', name, '--label', f'{LABEL}={session}', '--init', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--memory', '7g', '--memory-swap', '7g', '--cpus', '5', '--pids-limit', '1024', '--network', name, '--mount', f'type=volume,src={name},dst=/runner', '--tmpfs', '/tmp:rw,nosuid,nodev,size=512m,mode=1777', args.image)
            docker('exec', name, 'timeout', '30', 'bash', '-c', 'until test -f /runner/READY; do sleep 1; done')
            require(docker('exec', name, '/runner/bin/Runner.Listener', '--version').strip() == '2.338.0', 'Runner version changed')
            token = api('actions/runners/registration-token', 'POST')['token']
            script = 'IFS= read -r token; ./config.sh --unattended --ephemeral --disableupdate --url https://github.com/'+REPO+' --token "$token" --name '+name+' --no-default-labels --labels self-hosted,harborline-api-mini-portable-v1,linux-arm64-orbstack,lane-'+row['lane']+' --work _work >/runner/configure.log 2>&1; result=$?; unset token; exit "$result"'
            try:
                docker('exec', '-i', '--workdir', '/runner', name, 'bash', '-c', script, input=token+'\n')
            finally:
                token = None
        require(pending(policy['runId']) == current, 'Admission changed during registration')
        # One explicit listener activation after final queued-run verification.
        for row in resources(session):
            docker('exec', row['name'], 'touch', '/runner/START')
        while time.monotonic() < deadline:
            run = api('actions/runs/'+policy['runId'])
            require(run['run_attempt'] == 1 and run['head_sha'] == policy['head'], 'Run provenance changed')
            if run['status'] == 'completed':
                jobs = api('actions/runs/'+policy['runId']+'/attempts/1/jobs?per_page=100')
                names = {r['name'] for r in resources(session)}
                require(jobs['total_count'] == 1 and len(jobs['jobs']) == 1, 'Unexpected completed jobs')
                require({j['runner_name'] for j in jobs['jobs']} == names, 'Job executed outside this session')
                result = {'success': run['conclusion'] == 'success' and all(j['conclusion'] == 'success' for j in jobs['jobs']), 'runId': policy['runId'], 'head': policy['head'], 'jobs': jobs['jobs']}
                break
            time.sleep(5)
        else:
            raise RuntimeError('Bounded run deadline exceeded')
    finally:
        try:
            # Only gate artifacts, never runner config/listener logs/credentials.
            for row in resources(session):
                target = evidence/row['lane']
                target.mkdir(exist_ok=True)
                try:
                    docker('cp', row['name']+':/runner/gate/out/.', str(target))
                except RuntimeError:
                    pass  # Missing evidence makes a successful run fail below.
            if result['success']:
                for row in resources(session):
                    validate_receipt(evidence/row['lane'], policy['head'], policy['tree'], row['lane'] == 'b')
        except (RuntimeError, OSError, ValueError, KeyError, TypeError, StopIteration):
            result['success'] = False
            result['evidenceFailure'] = 'Missing or invalid exact-head gate/quality evidence'
        finally:
            # Evidence I/O can fail (including ENOSPC); teardown still runs.
            try:
                clean = cleanup(session)
            finally:
                os.close(lock)
        write(evidence/'cleanup.json', clean)
        write(evidence/'result.json', result)
    require(result['success'] and clean['clean'], 'Run or verified cleanup failed; inspect evidence and recover')


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='action', required=True)
    prep = sub.add_parser('prepare')
    for name in ('run-id', 'api', 'platform', 'quality', 'control', 'control-head', 'runner-archive', 'output'):
        prep.add_argument('--'+name, required=True)
    run = sub.add_parser('run')
    for name in ('policy', 'image', 'output'):
        run.add_argument('--'+name, required=True)
    run.add_argument('--lock', default='/Users/Shared/Harborline-workloads/heavy.lock')
    recovery = sub.add_parser('recover')
    recovery.add_argument('--session-file', required=True)
    args = parser.parse_args()
    if args.action == 'prepare':
        prepare(args)
    elif args.action == 'run':
        def interrupted(*_):
            raise KeyboardInterrupt('Container run interrupted')
        signal.signal(signal.SIGTERM, interrupted)
        execute(args)
    else:
        record = json.loads(Path(args.session_file).read_text())
        result = cleanup(record['session'])
        print(json.dumps(result))
        require(result['clean'], 'Cleanup is incomplete')


if __name__ == '__main__':
    try:
        main()
    except (Exception, KeyboardInterrupt) as error:
        # No traceback/raw subprocess result (credential safety).
        print('MINI_CONTROLLER_FAILED: '+(str(error) if isinstance(error, RuntimeError) else type(error).__name__))
        raise SystemExit(1)
