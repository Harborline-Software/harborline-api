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
import xml.etree.ElementTree as ET

from admission import REPO, WORKFLOW, CANDIDATE_WORKFLOW, REQUIRED_WORKFLOW, candidate_shape

ROOT = Path(__file__).resolve().parent
LABEL = 'org.harborline.mini.session'
RUNNER_SHA = '628b4a7258487b80c1d3c221095a7ded349f5ae0175fd3dfab708d07428f041b'
VALIDATORS = ROOT/'validators' if (ROOT/'validators').is_dir() else ROOT.parent


def selected_candidate_job(jobs, candidate):
    if candidate.get('required') is not True:
        require(len(jobs) == 1 and jobs[0]['name'] == 'portable', 'Unexpected candidate job set')
        selected = jobs[0]
        label = 'harborline-api-mini-candidate-v2'
    else:
        # Dependent jobs are not materialized until their prerequisites finish.
        # Permit only the reviewed workflow's named jobs; require the one mini.
        allowed = {'verify', 'verify-route', 'verify-shared', 'verify-macos', 'verify-linux', 'verify-mini',
                   'verify-windows', 'verify-windows-hosted', 'verify-perf', 'verify-perf-hosted', 'stryker'}
        require(len({j['name'] for j in jobs}) == len(jobs)
                and {j['name'] for j in jobs} <= allowed, 'Unexpected verify job set')
        matches = [j for j in jobs if j['name'] == 'verify-mini']
        require(len(matches) == 1, 'Required mini job absent or duplicated')
        selected = matches[0]
        for job in jobs:
            if job is not selected:
                require(not any(label.startswith('harborline-api-mini-') for label in job['labels']), 'Another job targets mini')
        require(type(selected.get('id')) is int and selected['id'] > 0, 'Invalid mini job ID')
        label = 'harborline-api-mini-required-v3'
    require(set(selected['labels']) == {'self-hosted', label, 'linux-arm64-orbstack', 'lane-a'}, 'Unexpected candidate labels')
    return selected


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


def candidate_state(candidate, *, allow_landed=False):
    """Resolve the approved PR and synthetic revision independently of the file.

    Initial merge-group scope is one owner-authored PR, first in the queue,
    squashed directly onto current main. Batches/other actors fail closed.
    """
    require(candidate_shape(candidate), 'Invalid reviewed candidate contract')
    number = candidate['prNumber']
    pr = api('pulls/'+str(number))
    landed = (allow_landed and candidate['event'] == 'merge_group'
              and pr['state'] == 'closed' and pr.get('merged') is True
              and pr.get('merge_commit_sha') == candidate['head'])
    require(pr['number'] == number and (pr['state'] == 'open' or landed) and not pr['draft'], 'PR is not ready and open')
    require(pr['user']['id'] == 1328090 and pr['user']['login'] == 'ctwoodwa', 'PR author not admitted')
    require(re.fullmatch(r'pipeline/mini-[A-Za-z0-9._/-]+', pr['head']['ref']), 'PR branch not admitted')
    require(pr['head']['repo']['id'] == 1360432948 and pr['head']['repo']['fork'] is False
            and pr['base']['repo']['id'] == 1360432948, 'Cross-repository candidate refused')
    require(pr['head']['sha'] == candidate['prHead'] and (landed or pr['base']['sha'] == candidate['base'])
            and pr['base']['ref'] == 'main', 'PR head or base changed')
    require(api('git/ref/heads/main')['object']['sha'] == (candidate['head'] if landed else candidate['base']),
            'Main changed; review a new candidate')
    merge = candidate['prMerge']
    if not landed:
        require(api('git/ref/pull/'+str(number)+'/merge')['object']['sha'] == merge, 'PR merge ref replaced')
    merge_commit = api('git/commits/'+merge)
    require([p['sha'] for p in merge_commit['parents']] == [candidate['base'], candidate['prHead']],
            'PR merge parents do not bind reviewed head/base')
    tested = api('git/commits/'+candidate['head'])
    require(tested['sha'] == candidate['head'], 'Tested commit mismatch')
    if candidate['event'] == 'pull_request':
        require(pr['head']['ref'] == candidate['headBranch'] and merge == candidate['head'], 'PR merge ref replaced')
    else:
        require([p['sha'] for p in tested['parents']] == [candidate['base']]
                and tested['tree']['sha'] == merge_commit['tree']['sha'], 'Group is not the one-PR squash tree')
        if landed:
            return tested['tree']['sha']
        require(api('git/ref/heads/'+candidate['headBranch'])['object']['sha'] == candidate['head'], 'Merge group replaced')
        query = ('query { repository(owner:"Harborline-Software", name:"harborline-api") { '
                 'pullRequest(number:'+str(number)+') { mergeQueueEntry { position baseCommit { oid } '
                 'headCommit { oid } mergeQueue { entries(first:100) { pageInfo { hasNextPage } '
                 'nodes { pullRequest { number headRefOid } headCommit { oid } } } } } } } }')
        data = json.loads(command(['gh', 'api', 'graphql', '-f', 'query='+query]))
        require(not data.get('errors'), 'Queue identity unavailable')
        entry = data['data']['repository']['pullRequest']['mergeQueueEntry']
        require(entry is not None and entry['position'] == 1
                and entry['baseCommit']['oid'] == candidate['base']
                and entry['headCommit']['oid'] == candidate['head'], 'Queue entry changed')
        entries = entry['mergeQueue']['entries']
        require(not entries['pageInfo']['hasNextPage'], 'Incomplete queue inventory')
        members = [e['pullRequest'] for e in entries['nodes']
                   if e.get('headCommit') and e['headCommit']['oid'] == candidate['head']]
        require(members == [{'number': number, 'headRefOid': candidate['prHead']}], 'Unexpected merge group membership')
    return tested['tree']['sha']


def validate_candidate_run(run, jobs, candidate, workflow, reviewed, *, queued=True):
    require(candidate_shape(candidate), 'Invalid reviewed candidate contract')
    require(run['repository']['id'] == 1360432948 and run['head_repository']['id'] == 1360432948, 'Wrong repository')
    require(run['event'] == candidate['event'] and run['run_attempt'] == 1, 'Wrong event/attempt')
    # REST head_sha is the PR source head; runtime GITHUB_SHA is its merge commit.
    expected_head = candidate['prHead'] if candidate['event'] == 'pull_request' else candidate['head']
    require(run['head_sha'] == expected_head and run['head_branch'] == candidate['headBranch'], 'Run source changed')
    required = candidate.get('required') is True
    require(run['path'] == (REQUIRED_WORKFLOW if required else CANDIDATE_WORKFLOW) and workflow == reviewed, 'Workflow bytes require review')
    for actor in ('actor', 'triggering_actor'):
        require(run[actor]['id'] == 1328090 and run[actor]['login'] == 'ctwoodwa', 'Actor not admitted')
    selected = selected_candidate_job(jobs, candidate)
    if queued:
        require(run['status'] in ('queued', 'waiting', 'pending', 'in_progress') and run['conclusion'] is None, 'Run is not pending')
        require(selected['status'] == 'queued' and not selected.get('runner_id'), 'Job already assigned')
    result = {'version': 3 if required else 2, 'runId': str(run['id']), 'head': candidate['head'], 'candidate': candidate,
              'coverage': candidate['event'] == 'merge_group'}
    if required:
        result.update(jobId=str(selected['id']), jobKey='verify-mini')
    return result


def candidate_pending(run_id, candidate, *, queued=True):
    tree = candidate_state(candidate, allow_landed=not queued)
    run = api('actions/runs/'+run_id)
    require(str(run['id']) == run_id, 'Run ID mismatch')
    jobs = api('actions/runs/'+run_id+'/attempts/1/jobs?per_page=100')
    require(jobs['total_count'] == len(jobs['jobs']), 'Incomplete job inventory')
    workflow = REQUIRED_WORKFLOW if candidate.get('required') is True else CANDIDATE_WORKFLOW
    remote = api('contents/'+workflow+'?ref='+candidate['workflowHead'])
    policy = validate_candidate_run(run, jobs['jobs'], candidate, base64.b64decode(remote['content']),
                                    (ROOT.parent.parent/workflow).read_bytes(), queued=queued)
    sdk = json.loads(base64.b64decode(api('contents/global.json?ref='+candidate['head'])['content']))['sdk']
    require(sdk['rollForward'] == 'disable', 'Review changed SDK policy')
    policy.update(sdk=sdk['version'], tree=tree)
    return policy


def pending(run_id, candidate=None):
    require(re.fullmatch('[1-9][0-9]*', run_id), 'Invalid run ID')
    if candidate is not None:
        return candidate_pending(run_id, candidate)
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
    candidate_file = getattr(args, 'candidate', None)
    candidate = json.loads(Path(candidate_file).read_text()) if candidate_file else None
    policy = pending(args.run_id, candidate)
    require(re.fullmatch('[0-9a-f]{40}', args.control_head), 'An explicitly reviewed control head is required')
    require(digest(args.runner_archive) == RUNNER_SHA, 'Runner archive digest mismatch')
    paths = {name: Path(getattr(args, name)).resolve() for name in ('api', 'platform', 'quality', 'control')}
    require(git(paths['api'], 'rev-parse', 'HEAD') == policy['head'], 'API checkout must be at admitted tested commit')
    if candidate:
        require(git(paths['api'], 'merge-base', candidate['base'], policy['head']) == candidate['base'], 'Missing or unrelated base history')
        if policy['version'] == 3:
            policy['comparisonDiff'] = git(paths['api'], 'diff', '--name-status', '-z', '--find-renames', candidate['base'], 'HEAD')
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
        if name == 'api' and candidate:
            sources[name]['comparisonBase'] = candidate['base']
    for name in ('Dockerfile', 'admission.py', 'controller.py', 'finish-gate.py', 'checkout-sources.py', 'entrypoint.sh', 'start-hook.sh', 'portable-gate.sh'):
        shutil.copyfile(ROOT/name, target/name)
    (target/'validators').mkdir()
    for name in ('focused-mode-policy.mjs', 'host-baseline.mjs', 'coverage.mjs'):
        shutil.copyfile(VALIDATORS/name, target/'validators'/name)
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
    for name in ('admission.py', 'controller.py', 'finish-gate.py', 'checkout-sources.py', 'entrypoint.sh', 'start-hook.sh', 'portable-gate.sh'):
        require(docker(*prefix, 'cat', image, '/opt/mini/'+name).encode() == (ROOT/name).read_bytes(), 'Image runtime differs from reviewed code')
    for name in ('focused-mode-policy.mjs', 'host-baseline.mjs', 'coverage.mjs'):
        require(docker(*prefix, 'cat', image, '/opt/mini/validators/'+name).encode() == (VALIDATORS/name).read_bytes(), 'Image validator differs from reviewed code')
    return json.loads(sources)


def coverage_summary(path):
    xml = ET.parse(path).getroot()
    require(xml.tag == 'coverage', 'Invalid coverage root')
    lines = {}
    for cls in xml.iter('class'):
        filename = cls.attrib['filename']
        require(bool(filename), 'Missing coverage filename')
        for line in cls.iter('line'):
            number, hits = line.attrib['number'], line.attrib['hits']
            require(re.fullmatch('[0-9]+', number) and re.fullmatch('[0-9]+', hits), 'Invalid coverage line')
            key = (filename, int(number))
            lines[key] = max(int(hits), lines.get(key, 0))
    return {'validLines': len(lines), 'coveredLines': sum(hits > 0 for hits in lines.values()),
            'paths': sorted({filename for filename, _ in lines})}


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
            require(item['path'] == 'artifacts/quality/'+name+'.cobertura.xml', 'Wrong coverage artifact path')
            measured = coverage_summary(directory/'quality'/(name+'.cobertura.xml'))
            require(all(item[key] == measured[key] for key in ('validLines', 'coveredLines')), 'Coverage contents mismatch')
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


def measure_focused_runtime(inputs, gate):
    """Measure independently of receipts, inside the admitted execution container."""
    policy = json.loads((inputs/'policy.json').read_text())
    feed = gate/'api'/'.feed'
    files = sorted((p for p in feed.iterdir() if p.name.endswith('.nupkg') or p.name == 'packed-version.props'),
                   key=lambda p: p.name.encode('utf-16-be'))
    require(any(p.name == 'packed-version.props' for p in files)
            and any(p.name.endswith('.nupkg') for p in files), 'Focused dependency feed absent')
    require(all(p.is_file() and not p.is_symlink() for p in files), 'Invalid focused dependency file')
    payload = json.dumps([[p.name, digest(p)] for p in files], ensure_ascii=False, separators=(',', ':'))
    host = command(['node', '-e', 'process.stdout.write(require("node:os").hostname()+"/"+process.platform+"/"+process.arch)'])
    return {'policySha256': digest(inputs/'policy.json'), 'runId': policy['runId'],
            'jobId': policy.get('jobId'), 'head': policy['head'], 'host': host,
            'dependencies': 'sha256:'+hashlib.sha256(payload.encode()).hexdigest()}


def validate_runtime_anchor(runtime, policy, policy_digest):
    require(isinstance(runtime, dict), 'Independent focused runtime measurement absent')
    require(all(runtime.get(key) == value for key, value in {
        'policySha256': policy_digest, 'runId': policy['runId'],
        'jobId': policy.get('jobId'), 'head': policy['head']}.items()), 'Focused runtime policy mismatch')
    require(isinstance(runtime.get('host'), str) and bool(runtime['host'])
            and re.fullmatch('sha256:[0-9a-f]{64}', runtime.get('dependencies', '')), 'Invalid focused runtime measurement')


def validate_candidate_evidence(directory, policy, *, runtime=None):
    value = json.loads((directory/'candidate-provenance.json').read_text())
    for key in ('runId', 'head', 'tree', 'candidate', 'coverage', 'sourcesSha256'):
        require(value.get(key) == policy[key], 'Candidate evidence binding mismatch: '+key)
    require(value.get('comparisonBase') == policy['candidate']['base'], 'Wrong comparison base')
    if policy['version'] == 3:
        require(value.get('jobId') == policy['jobId'] and value.get('jobKey') == 'verify-mini', 'Wrong job evidence')
        require(value.get('comparisonDiff') == policy['comparisonDiff'], 'Wrong source diff evidence')
    require(isinstance(value.get('changedPaths'), list)
            and (policy['version'] == 3 or bool(value['changedPaths'])), 'No changed-path qualification evidence')
    # Initial candidate qualification deliberately requires a non-documentation
    # delta and actual completed focused OFF/ON evidence, not just selection.
    files = list((directory/'gate-evidence'/'focused-modes').glob('run-*/receipts.json'))
    expected_plan = None
    if policy['version'] == 3:
        script = ('import {readFileSync} from "node:fs"; import {classifyFocusedModes,parseChangedFiles,requireRunnableSelection} from '
                  +json.dumps((VALIDATORS/'focused-mode-policy.mjs').as_uri())+'; '
                  'const plan=classifyFocusedModes(parseChangedFiles(readFileSync(0,"utf8"))); '
                  'requireRunnableSelection(plan); console.log(JSON.stringify(plan));')
        expected_plan = json.loads(command(['node', '--input-type=module', '-e', script], input=policy['comparisonDiff']))
        if not expected_plan['requiredModes']:
            require(not files, 'Unexpected focused evidence for unchanged/documentation-only source')
            return
    require(bool(files), 'Focused OFF/ON receipts absent')
    if policy['version'] == 3:
        require(isinstance(runtime, dict), 'Independent focused runtime measurement absent')
    required_names = {
        'ck-10 fence: no production code runs its own loop over the ADR 0038 stage order',
        'ck-10 fence: only the executor calls a KernelWrite stage',
        'ck-10 fence: every admitted-write commit is a KernelWrite commit stage or a reviewed not-yet-moved path',
        'ck-10 fence: the record writer commits only from its KernelWrite commit stages, EF saves included',
        'ck-10 fence: a planted bypass outside the writer is caught by every check',
    }
    for path in files:
        data = json.loads(path.read_text())
        if expected_plan is not None:
            require(data['plan'] == expected_plan, 'Focused plan does not match admitted source diff')
        require(data['plan']['requiredModes'] == ['coverage-off', 'coverage-on'], 'Focused modes not selected')
        if policy['version'] == 3:
            # The anchor is measured by immutable code, never read from these receipts.
            for key, expected in {'host': runtime['host'], 'dependencies': runtime['dependencies'],
                                  'run': path.parent.name}.items():
                require(data['expected'].get(key) == expected, 'Focused runtime binding mismatch: '+key)
        rows = data['receipts']
        require(len(rows) == 2 and {r['mode'] for r in rows} == {'coverage-off', 'coverage-on'}, 'Missing focused mode')
        for row in rows:
            require(row['commit'] == policy['head'] and row['tree'] == policy['tree']
                    and row['sdk'] == policy['sdk'] and row['executed'] is True and row['exitCode'] == 0,
                    'Focused execution provenance failed')
            counts, tests = row['counts'], row['results']
            require(counts['total'] == counts['passed'] == len(tests) >= 5
                    and counts['failed'] == counts['notExecuted'] == 0
                    and all(t['outcome'] == 'Passed' for t in tests)
                    and len({t['rosterId'] for t in tests}) == len(tests)
                    and {t['testName'] for t in tests} >= required_names, 'Focused test evidence incomplete')
            if row['mode'] == 'coverage-on':
                require(row['coverage']['validLines'] > 0
                        and row['coverageDigest'] == 'sha256:'+digest(path.parent/'coverage-on'/'focused.cobertura.xml'),
                        'Focused coverage missing or changed')
                measured = coverage_summary(path.parent/'coverage-on'/'focused.cobertura.xml')
                require(row['coverage'] == measured, 'Focused coverage contents mismatch')
            else:
                require(row['coverageDigest'] is None and row['coverage'] is None, 'Unexpected focused OFF coverage')
        # Execute the reviewed host-side validator, never import candidate code
        # from the container's exported source. Re-read the archived raw TRX too.
        validator = (VALIDATORS/'focused-mode-policy.mjs').as_uri()
        trx_reader = (VALIDATORS/'host-baseline.mjs').as_uri()
        script = ('import {readFileSync} from "node:fs"; import path from "node:path"; '
                  'import assert from "node:assert/strict"; '
                  'import {validateFocusedModeEvidence} from '+json.dumps(validator)+'; '
                  'import {readHostTrx} from '+json.dumps(trx_reader)+'; '
                  'const file=process.argv[1], d=JSON.parse(readFileSync(file,"utf8")); '
                  'validateFocusedModeEvidence(d.plan,d.expected,d.receipts); '
                  'for(const r of d.receipts){const raw=readHostTrx(path.join(path.dirname(file),r.mode,"focused.trx")); '
                  'assert.deepEqual(raw.problems,[]); assert.deepEqual(raw.counts,r.counts); '
                  'const sort=x=>[...x].sort((a,b)=>a.rosterId.localeCompare(b.rosterId)); '
                  'assert.deepEqual(sort(raw.results),sort(r.results));}')
        command(['node', '--input-type=module', '-e', script, str(path.resolve())])


def execute(args):
    policy = json.loads(Path(args.policy).read_text())
    candidate = policy.get('candidate')
    current = pending(policy['runId'], candidate)
    require(all(policy.get(k) == v for k, v in current.items()), 'Stale admission policy')
    session = uuid.uuid4().hex
    evidence = Path(args.output)
    evidence.mkdir(mode=0o700)
    write(evidence/'session.json', {'session': session, 'policy': policy, 'image': args.image, 'ttlSeconds': 3600})
    lock = reserve(args.lock)
    clean = None
    result = {'success': False}
    focused_runtime = None
    try:
        sources = preflight_image(args.image, policy, session)
        write(evidence/'sources.json', sources)
        # Recheck after slow preflight, before credentials. The run stays exact-main.
        require(pending(policy['runId'], candidate) == current, 'Admission changed during preflight')
        deadline = time.monotonic()+3300
        for row in resources(session):
            name = row['name']
            docker('volume', 'create', '--label', f'{LABEL}={session}', name)
            docker('network', 'create', '--label', f'{LABEL}={session}', name)
            docker('run', '-d', '--name', name, '--label', f'{LABEL}={session}', '--init', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--memory', '10g', '--memory-swap', '10g', '--cpus', '5', '--pids-limit', '1024', '--network', name, '--mount', f'type=volume,src={name},dst=/runner', '--tmpfs', '/tmp:rw,nosuid,nodev,size=512m,mode=1777', args.image)
            docker('exec', name, 'timeout', '30', 'bash', '-c', 'until test -f /runner/READY; do sleep 1; done')
            require(docker('exec', name, '/runner/bin/Runner.Listener', '--version').strip() == '2.338.0', 'Runner version changed')
            token = api('actions/runners/registration-token', 'POST')['token']
            runner_label = ('harborline-api-mini-required-v3' if policy['version'] == 3 else
                            'harborline-api-mini-candidate-v2' if candidate else 'harborline-api-mini-portable-v1')
            script = 'IFS= read -r token; ./config.sh --unattended --ephemeral --disableupdate --url https://github.com/'+REPO+' --token "$token" --name '+name+' --no-default-labels --labels self-hosted,'+runner_label+',linux-arm64-orbstack,lane-'+row['lane']+' --work _work >/runner/configure.log 2>&1; result=$?; unset token; exit "$result"'
            try:
                docker('exec', '-i', '--workdir', '/runner', name, 'bash', '-c', script, input=token+'\n')
            finally:
                token = None
        require(pending(policy['runId'], candidate) == current, 'Admission changed during registration')
        # One explicit listener activation after final queued-run verification.
        for row in resources(session):
            docker('exec', row['name'], 'touch', '/runner/START')
        next_candidate_check = 0
        while time.monotonic() < deadline:
            run = api('actions/runs/'+policy['runId'])
            run_head = candidate['prHead'] if candidate and candidate['event'] == 'pull_request' else policy['head']
            require(run['run_attempt'] == 1 and run['head_sha'] == run_head, 'Run provenance changed')
            if policy['version'] == 3:
                jobs = api('actions/runs/'+policy['runId']+'/attempts/1/jobs?per_page=100')
                require(jobs['total_count'] == len(jobs['jobs']), 'Incomplete job inventory')
                job = selected_candidate_job(jobs['jobs'], candidate)
                require(str(job['id']) == policy['jobId'], 'Selected job changed')
                if job.get('runner_id'):
                    require(job['runner_name'] == resources(session)[0]['name'], 'Job assigned elsewhere')
                if focused_runtime is None and job['status'] == 'in_progress' and job.get('runner_id'):
                    # The ephemeral listener may exit before GitHub reports completion.
                    # Capture a host-held anchor while execution is active. The readiness
                    # marker only schedules measurement; it supplies none of its values.
                    measured = docker('exec', resources(session)[0]['name'], 'sh', '-c',
                                      'if test -f /runner/gate/FOCUSED_READY; then python3 -I /opt/mini/finish-gate.py --measure-focused; fi')
                    if measured.strip():
                        focused_runtime = json.loads(measured)
                        validate_runtime_anchor(focused_runtime, policy, digest(args.policy))
                if job['status'] == 'completed':
                    require(job['runner_name'] == resources(session)[0]['name'], 'Job completed elsewhere')
                    # This candidate-specific verdict does not wait on the verify
                    # aggregate (which depends on this job) or on protected landing.
                    result = {'success': job['conclusion'] == 'success', 'runId': policy['runId'],
                              'head': policy['head'], 'jobs': [job], 'selectedJobOnly': True}
                    break
            if candidate and run['status'] != 'completed' and time.monotonic() >= next_candidate_check:
                # A superseded candidate must not continue consuming capacity or
                # acquire a success verdict merely because the old job exits 0.
                require(candidate_state(candidate, allow_landed=candidate['event'] == 'merge_group') == policy['tree'],
                        'Candidate superseded during execution')
                next_candidate_check = time.monotonic()+30
            if run['status'] == 'completed':
                if candidate:
                    require(candidate_pending(policy['runId'], candidate, queued=False) == current, 'Candidate superseded before completion')
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
                    validate_receipt(evidence/row['lane'], policy['head'], policy['tree'], policy.get('coverage', row['lane'] == 'b'))
                    if candidate:
                        if policy['version'] == 3:
                            validate_runtime_anchor(focused_runtime, policy, digest(args.policy))
                            write(evidence/'focused-runtime.json', focused_runtime)
                        validate_candidate_evidence(evidence/row['lane'], policy, runtime=focused_runtime)
        except (RuntimeError, OSError, ValueError, KeyError, TypeError, StopIteration, ET.ParseError):
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
    prep.add_argument('--candidate', help='Explicit reviewed candidate JSON; omit for manual/main v1')
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
