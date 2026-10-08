"""Read-only observer for one explicitly admitted controller session.

Never starts containers, registers runners, reads credential files, or cancels jobs.
An alarm exits nonzero; the operator must stop the exact run and invoke controller
recovery. The production controller independently owns the lock, TTL and cleanup.
"""
import argparse
import json
import pathlib
import re
import time
import hashlib
import resource_profile as rp
from observer_commands import Commands
from host_diagnostics import HostProcesses, memory_facts

parser = argparse.ArgumentParser()
parser.add_argument('--session-file', required=True)
parser.add_argument('--output', required=True)
parser.add_argument('--resource-profile', required=True, choices=sorted(rp.PROFILES))
args = parser.parse_args()
session_path = pathlib.Path(args.session_file)
out = pathlib.Path(args.output)
out.mkdir(mode=0o700)
script = pathlib.Path(__file__).with_name('telemetry.py').read_text()

context = {'phase': 'baseline'}
commands = Commands(out / 'command-failures.jsonl', context)
command = commands.run
host_processes = HostProcesses()

def host():
    free = command(['/usr/bin/memory_pressure', '-Q'], 'host.memory-pressure')
    swap = command(['/usr/sbin/sysctl', '-n', 'vm.swapusage'], 'host.swap-usage')
    level = command(['/usr/sbin/sysctl', '-n', 'kern.memorystatus_vm_pressure_level'], 'host.pressure-level')
    vm = command(['/usr/bin/vm_stat'], 'host.vm-stat')
    return {'time': time.time(), 'freePercent': int(re.search(r'free percentage: (\d+)', free)[1]),
            'swapUsedMiB': float(re.search(r'used = ([\d.]+)M', swap)[1]), 'pressureLevel': int(level),
            **rp.counters(vm), 'memory': memory_facts(vm)}

admitted=json.loads(session_path.read_text())
session=admitted['session']
context.update(session=session, controllerPid=admitted.get('pid'))
if not re.fullmatch('[0-9a-f]{32}',session) or admitted['fingerprint']['resourceProfile']!=args.resource_profile:
    raise RuntimeError('Initial session/profile differs')
mode=rp.profile(args.resource_profile)
base={'profile':mode,'session':session,'hostSamples':[host()]}
if mode==rp.OPERATIONAL:
    while base['hostSamples'][-1]['time']-base['hostSamples'][0]['time']<30:
        time.sleep(3)
        base['hostSamples'].append(host())
        current=base['hostSamples'][-1]
        rp.transition(base['hostSamples'][0],base['hostSamples'][-2],current,mode)
        if current['swapUsedMiB']!=base['hostSamples'][0]['swapUsedMiB'] or current['pressureLevel']!=1 or current['freePercent']<30:
            raise RuntimeError('Operational host baseline unstable')
initial=rp.baseline(base,mode,session)
(out/'baseline.json').write_text(json.dumps(base,indent=2)+'\n')
baseline_digest=hashlib.sha256((out/'baseline.json').read_bytes()).hexdigest()
(out/'initial-host.json').write_text(json.dumps(initial,indent=2)+'\n')
ready=out/'ready.tmp'
ready.write_text(json.dumps({'session':session,'profile':mode,'initialHost':initial,'baselineSha256':baseline_digest})+'\n')
ready.replace(out/'ready.json')
deadline = time.monotonic() + 3500
samples = []
previous = initial
alarm = None
pressured = 0
try:
    while time.monotonic() < deadline:
        if not session_path.exists():
            time.sleep(1)
            continue
        record = json.loads(session_path.read_text())
        if record['session'] != session or record['fingerprint']['resourceProfile']!=mode:
            raise RuntimeError('Session identity changed')
        if not (out / 'admitted-session.json').exists():
            (out / 'admitted-session.json').write_text(json.dumps(record, indent=2) + '\n')
        if (session_path.parent / 'result.json').exists():
            break
        name = 'hl-mini-' + session + '-a'
        context['phase'] = 'container-discovery'
        rows = command(['docker', 'ps', '--all', '--filter', 'label=org.harborline.mini.session=' + session,
                        '--format', '{{.Names}}'], 'docker.session-list').splitlines()
        if any(row != name for row in rows):
            raise RuntimeError('Unexpected session container; single lane only')
        if not rows:
            time.sleep(1)
            continue
        # The reviewed controller briefly runs a 512-MiB image preflight first.
        try:
            context['phase'] = 'container-status'
            info = json.loads(command(['docker', 'inspect', '--format',
                '{"limit":{{.HostConfig.Memory}},"state":{{json .State}}}', name], 'docker.session-inspect'))
            context['containerStatus'] = info['state'].get('Status')
        except RuntimeError:
            if not command(['docker', 'ps', '--all', '--filter', 'name=^/' + name + '$', '--format', '{{.Names}}'], 'docker.session-list').strip():
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
            context['phase'] = 'process-sample'
            sample = json.loads(command(['docker', 'exec', name, 'python3', '-c', script], 'docker.telemetry-exec'))
        except RuntimeError:
            if not command(['docker', 'ps', '--filter', 'name=^/' + name + '$', '--format', '{{.Names}}'], 'docker.running-list').strip():
                time.sleep(1)
                continue
            raise
        context['phase'] = 'host-sample'
        sample['host'] = host()
        # Optional previous snapshot: asynchronous collection never gates a stop.
        sample['hostProcesses'] = host_processes.snapshot()
        context['phase'] = 'docker-stats'
        sample['aggregateDockerStats'] = [json.loads(line) for line in
            command(['docker', 'stats', '--no-stream', '--format', '{{json .}}'], 'docker.aggregate-stats').splitlines()]
        samples.append(sample)
        with (out / 'telemetry.jsonl').open('a') as stream:
            stream.write(json.dumps(sample) + '\n')
        rp.sample(sample)
        rp.transition(initial,previous,sample['host'],mode)
        previous=sample['host']
        danger = sample['host']['freePercent'] < 20 or sample['host']['pressureLevel'] != 1 or sample['vmMemAvailable'] < 2 * 1024**3
        pressured = pressured + 1 if danger else 0
        if sample['memoryEvents'].get('oom', 0) or sample['memoryEvents'].get('oom_kill', 0):
            raise RuntimeError('Cgroup OOM alarm')
        if pressured >= 2:
            raise RuntimeError('Host or VM pressure alarm')
        host_processes.refresh()
        time.sleep(3)
    else:
        raise RuntimeError('Observer deadline exceeded')
except Exception as error:
    alarm = str(error) if isinstance(error, RuntimeError) else type(error).__name__
finally:
    result = {'session': session, 'alarm': alarm, 'samples': len(samples),
              'peakIsSampledCgroupHighWater': True,
              'limitation': 'Container may exit between samples; no final cgroup sample is guaranteed.'}
    result.update(profile=mode,baselineSha256=baseline_digest)
    if commands.last_failure is not None:
        result['commandFailure'] = commands.last_failure
    if samples:
        try:
            result.update(rp.measures(base,samples,mode,session))
        except Exception as error:
            alarm=alarm or str(error)
            result['alarm']=alarm
    (out / 'summary.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
if alarm:
    raise SystemExit(1)
