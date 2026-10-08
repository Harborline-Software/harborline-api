"""Reclaim build servers only inside an admitted, isolated private portable job."""
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
import manifest as m
from private_admission import binding_shape

PHASE = 'exact-clone-host-tests'
DOTNET = '/usr/share/dotnet/dotnet'
OUTPUT = Path('/runner/gate/out/private-build-server-reclamation.json')


def protected_file(path, target):
    path = Path(path)
    m.require(path.resolve() == Path(target), 'Unexpected private authority target')
    info = path.stat()
    parent = path.resolve().parent.stat()
    m.require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and info.st_nlink == 1
              and not info.st_mode & 0o022, 'Writable or unowned private authority')
    m.require(parent.st_uid == 0 and not parent.st_mode & 0o022, 'Writable authority directory')
    return json.loads(path.read_bytes())


def validate(binding, assignment, value, policy, env, uid, platform, limits):
    binding_shape(binding)
    m.require(uid == 1001 and platform == 'linux', 'Private container user required')
    m.require(limits == ('10737418240', '0', '500000 100000'), 'Private cgroup profile differs')
    m.require(assignment.get('verified') is True and assignment.get('binding') == binding
              and type(assignment.get('runnerId')) is int and assignment['runnerId'] > 0
              and assignment.get('runnerName') == 'hl-trusted-' + binding['runId'] + '-' + binding['jobId'],
              'Verified private assignment required')
    m.require(env.get('HARBORLINE_PRIVATE_BUILD_RECLAIM') == '1'
              and env.get('HARBORLINE_TASK_ID') == binding['taskId']
              and env.get('HARBORLINE_APPROVED_MANIFEST_SHA256') == binding['manifestSha256']
              and env.get('RUNNER_NAME') == assignment['runnerName'],
              'Private resource hook differs from binding')
    m.require(env.get('DOTNET_CLI_HOME') == '/runner/gate/cache/dotnet'
              and env.get('TMPDIR') == '/runner/gate/tmp/', 'Job-private server endpoints required')
    task = next((t for t in value['tasks'] if t['id'] == binding['taskId']), None)
    m.require(task is not None and task['kind'] in ('portable', 'portable-coverage'), 'Portable private task required')
    m.require(value['sources']['control'] == binding['workflowSha']
              and policy['head'] == value['sources']['api'] and policy['tree'] == value['tree']
              and policy['sdk'] == value['sdk'] and policy['inputDigests'] == value['inputDigests'],
              'Approved resource source/profile differs')
    m.require(policy['environment'].get('privateBuildServerReclamation') == PHASE,
              'Resource profile does not approve server reclamation')


def authority():
    binding = protected_file('/opt/trusted/private-binding.json', '/runner/control/binding.json')
    assignment = protected_file('/opt/trusted/private-assignment.json', '/runner/control/assignment.json')
    protected_file('/runner/approved-manifest.json', '/runner/approved-manifest.json')
    value = m.load('/runner/approved-manifest.json', binding['manifestSha256'])
    policy = protected_file('/opt/mini/policy.json', '/opt/mini/policy.json')
    limits = tuple((Path('/sys/fs/cgroup') / name).read_text().strip()
                   for name in ('memory.max', 'memory.swap.max', 'cpu.max'))
    validate(binding, assignment, value, policy, os.environ, os.getuid(), sys.platform, limits)
    binary = Path(DOTNET).stat()
    m.require(stat.S_ISREG(binary.st_mode) and binary.st_uid == 0
              and not binary.st_mode & 0o022, 'Immutable SDK binary required')
    return binding, value


def owned_servers(proc=Path('/proc'), uid=1001):
    """Observe only SDK compiler/MSBuild worker nodes; never signal any PID."""
    result = []
    for folder in proc.iterdir():
        if not folder.name.isdigit():
            continue
        try:
            status = dict(line.split(':', 1) for line in (folder / 'status').read_text().splitlines() if ':' in line)
            if any(int(x) != uid for x in status['Uid'].split()):
                continue
            argv = (folder / 'cmdline').read_bytes().split(b'\0')
            names = [Path(x.decode()).name.lower() for x in argv if x]
            kind = ('compiler' if 'vbcscompiler.dll' in names else
                    'msbuild-worker' if 'msbuild.dll' in names and any(x.lower().startswith(b'/nodemode:') for x in argv) else None)
            if kind:
                result.append({'pid': int(folder.name), 'kind': kind,
                               'rssKiB': int(status.get('VmRSS', '0 kB').split()[0])})
        except (FileNotFoundError, ProcessLookupError):
            continue
    return sorted(result, key=lambda x: x['pid'])


def shutdown(binding, value, runner=subprocess.run, observe=owned_servers, clock=time.monotonic, pause=time.sleep):
    before = observe()
    started = clock()
    # Fixed SDK executable/command, private CLI home and TMPDIR. No shell, PID
    # signals, host CLI call, endpoint override or credential operation.
    result = runner([DOTNET, 'build-server', 'shutdown'], capture_output=True, text=True,
                    check=True, timeout=45)
    deadline = started + 55
    remaining = observe()
    while remaining:
        m.require(clock() < deadline, 'Job-owned build servers did not drain')
        pause(.2)
        remaining = observe()
    return {'phase': PHASE, 'binding': binding, 'apiHead': value['sources']['api'],
            'sdk': value['sdk'], 'uid': 1001, 'before': before, 'after': remaining,
            'command': [DOTNET, 'build-server', 'shutdown'], 'exitCode': result.returncode,
            'elapsedSeconds': clock() - started, 'stdout': result.stdout[-4000:],
            'stderr': result.stderr[-4000:], 'signalsSent': False}


def validate_result(record, binding, value):
    m.require(record.get('phase') == PHASE and record.get('binding') == binding
              and record.get('apiHead') == value['sources']['api'] and record.get('sdk') == value['sdk']
              and record.get('uid') == 1001 and record.get('after') == []
              and record.get('command') == [DOTNET, 'build-server', 'shutdown']
              and record.get('exitCode') == 0 and record.get('signalsSent') is False
              and isinstance(record.get('elapsedSeconds'), (int, float))
              and 0 <= record['elapsedSeconds'] <= 55, 'Missing or invalid private server reclamation')


if __name__ == '__main__':
    m.require(sys.argv[1:] == [PHASE], 'Unapproved reclamation phase')
    binding, value = authority()
    m.require(not OUTPUT.exists(), 'Duplicate reclamation phase')
    record = shutdown(binding, value)
    validate_result(record, binding, value)
    with OUTPUT.open('x') as out:
        json.dump(record, out, indent=2)
        out.write('\n')
    OUTPUT.chmod(0o600)
