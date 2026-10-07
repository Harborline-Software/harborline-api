"""Read-only observer for one explicitly admitted controller session.

Never starts containers, registers runners, reads credential files, or cancels jobs.
An alarm exits nonzero; the operator must stop the exact run and invoke controller
recovery. The production controller independently owns the lock, TTL and cleanup.
"""
import argparse
import json
import pathlib
import re
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--session-file', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
session_path = pathlib.Path(args.session_file)
out = pathlib.Path(args.output)
out.mkdir(mode=0o700)
script = pathlib.Path(__file__).with_name('telemetry.py').read_text()

def command(argv, timeout=15):
    result = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError('Read-only telemetry command failed: ' + pathlib.Path(argv[0]).name)
    return result.stdout

def host():
    free = command(['/usr/bin/memory_pressure', '-Q'])
    swap = command(['/usr/sbin/sysctl', '-n', 'vm.swapusage'])
    level = command(['/usr/sbin/sysctl', '-n', 'kern.memorystatus_vm_pressure_level'])
    return {'time': time.time(), 'freePercent': int(re.search(r'free percentage: (\d+)', free)[1]),
            'swapUsedMiB': float(re.search(r'used = ([\d.]+)M', swap)[1]), 'pressureLevel': int(level)}

initial = host()
if initial['swapUsedMiB'] != 0 or initial['freePercent'] < 30 or initial['pressureLevel'] != 1:
    raise RuntimeError('Initial host headroom is insufficient')
(out / 'initial-host.json').write_text(json.dumps(initial, indent=2) + '\n')
admitted=json.loads(session_path.read_text())
if not re.fullmatch('[0-9a-f]{32}',admitted['session']):
    raise RuntimeError('Invalid initial session identity')
ready=out/'ready.tmp'
ready.write_text(json.dumps({'session':admitted['session'],'initialHost':initial})+'\n')
ready.replace(out/'ready.json')
deadline = time.monotonic() + 3500
samples = []
session = None
alarm = None
pressured = 0
try:
    while time.monotonic() < deadline:
        if not session_path.exists():
            time.sleep(1)
            continue
        record = json.loads(session_path.read_text())
        if session is None:
            session = record['session']
            if not re.fullmatch('[0-9a-f]{32}', session):
                raise RuntimeError('Invalid session identity')
            (out / 'admitted-session.json').write_text(json.dumps(record, indent=2) + '\n')
        if record['session'] != session:
            raise RuntimeError('Session identity changed')
        if (session_path.parent / 'result.json').exists():
            break
        name = 'hl-mini-' + session + '-a'
        rows = command(['docker', 'ps', '--all', '--filter', 'label=org.harborline.mini.session=' + session,
                        '--format', '{{.Names}}']).splitlines()
        if any(row != name for row in rows):
            raise RuntimeError('Unexpected session container; single lane only')
        if not rows:
            time.sleep(1)
            continue
        # The reviewed controller briefly runs a 512-MiB image preflight first.
        try:
            info = json.loads(command(['docker', 'inspect', '--format',
                '{"limit":{{.HostConfig.Memory}},"state":{{json .State}}}', name]))
        except RuntimeError:
            if not command(['docker', 'ps', '--all', '--filter', 'name=^/' + name + '$', '--format', '{{.Names}}']).strip():
                time.sleep(1)
                continue
            raise
        limit = str(info['limit'])
        if limit == str(512 * 1024**2):
            time.sleep(1)
            continue
        if limit != str(10 * 1024**3):
            raise RuntimeError('Unexpected live memory cap')
        if not info['state']['Running']:
            (out / 'observed-terminal-state.json').write_text(json.dumps(info['state'], indent=2) + '\n')
            if info['state']['OOMKilled']:
                raise RuntimeError('Terminal Docker OOM alarm')
            time.sleep(1)
            continue
        try:
            sample = json.loads(command(['docker', 'exec', name, 'python3', '-c', script]))
        except RuntimeError:
            if not command(['docker', 'ps', '--filter', 'name=^/' + name + '$', '--format', '{{.Names}}']).strip():
                time.sleep(1)
                continue
            raise
        sample['host'] = host()
        sample['aggregateDockerStats'] = [json.loads(line) for line in
            command(['docker', 'stats', '--no-stream', '--format', '{{json .}}']).splitlines()]
        samples.append(sample)
        with (out / 'telemetry.jsonl').open('a') as stream:
            stream.write(json.dumps(sample) + '\n')
        danger = sample['host']['freePercent'] < 20 or sample['host']['pressureLevel'] != 1 or sample['vmMemAvailable'] < 2 * 1024**3
        pressured = pressured + 1 if danger else 0
        if sample['memoryEvents'].get('oom', 0) or sample['memoryEvents'].get('oom_kill', 0):
            raise RuntimeError('Cgroup OOM alarm')
        if sample['host']['swapUsedMiB'] - initial['swapUsedMiB'] > 64 or pressured >= 2:
            raise RuntimeError('Host or VM pressure alarm')
        time.sleep(3)
    else:
        raise RuntimeError('Observer deadline exceeded')
except Exception as error:
    alarm = str(error) if isinstance(error, RuntimeError) else type(error).__name__
finally:
    result = {'session': session, 'alarm': alarm, 'samples': len(samples),
              'peakIsSampledCgroupHighWater': True,
              'limitation': 'Container may exit between samples; no final cgroup sample is guaranteed.'}
    if samples:
        result.update(peakMemoryBytes=max(s['memoryPeak'] for s in samples),
                      maxHostSwapMiB=max(s['host']['swapUsedMiB'] for s in samples),
                      minVmAvailableBytes=min(s['vmMemAvailable'] for s in samples),
                      minHostFreePercent=min(s['host']['freePercent'] for s in samples))
    (out / 'summary.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
if alarm:
    raise SystemExit(1)
