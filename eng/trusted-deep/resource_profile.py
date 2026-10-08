"""Two explicit resource proofs; operational success is never zero-used-swap proof."""
import hashlib
import json
import math
import re
from manifest import require
ZERO = 'zero-used-swap-v1'
OPERATIONAL = 'operational-stable-swap-v1'
PROFILES = {ZERO, OPERATIONAL}


def profile(value):
    require(value in PROFILES, 'Unknown resource profile')
    return value


def counters(text):
    # Apple vm_stat(1): swapins are reads, swapouts writes, pageins generic pager IO.
    result = {}
    for title, key in [('Swapins', 'swapins'), ('Swapouts', 'swapouts'), ('Pageins', 'pageins')]:
        rows = re.findall(r'^'+title+r':\s+(\d+)\.\s*$', text, re.MULTILINE)
        require(len(rows)==1, 'Missing/ambiguous VM counter: '+title)
        result[key] = int(rows[0])
    return result


def host(value):
    for key in ('time', 'swapUsedMiB'):
        require(type(value.get(key)) in (int,float) and math.isfinite(value[key]) and value[key]>=0, 'Invalid host measurement: '+key)
    for key in ('freePercent','pressureLevel','swapins','swapouts','pageins'):
        require(type(value.get(key)) is int and value[key]>=0, 'Missing/invalid host counter: '+key)
    require(value['freePercent']<=100, 'Invalid free percentage')
    return value


def transition(initial, previous, current, mode):
    profile(mode);host(initial);host(previous);host(current)
    require(current['time']>=previous['time'], 'Host clock reset')
    for key in ('swapins','swapouts','pageins'):
        require(current[key]>=previous[key], 'Host counter reset: '+key)
    require(current['swapouts']==initial['swapouts'], 'New host swap writes')
    # Reads from existing swap and generic page-ins remain recorded telemetry.
    if mode==OPERATIONAL:
        require(current['swapUsedMiB']<=previous['swapUsedMiB'], 'New host used-swap growth')
    limit = 0 if mode==OPERATIONAL else 64
    require(current['swapUsedMiB']-initial['swapUsedMiB']<=limit, 'New host used swap')


def baseline(value, mode, session):
    profile(mode)
    require(value.get('profile')==mode and value.get('session')==session, 'Baseline profile/session differs')
    rows=value.get('hostSamples')
    require(isinstance(rows,list) and rows, 'Missing fresh host baseline')
    first=host(rows[0]);previous=first
    for row in rows:
        host(row);transition(first,previous,row,mode)
        require(row['pressureLevel']==1 and row['freePercent']>=30, 'Baseline host headroom failed')
        require(row['swapUsedMiB']==first['swapUsedMiB'], 'Host used-swap baseline unstable')
        previous=row
    if mode==OPERATIONAL:
        require(len(rows)>=2 and 30<=rows[-1]['time']-first['time']<=45, 'Operational baseline must be fresh stable30 seconds')
        require(all(b['time']-a['time']<=5 for a,b in zip(rows,rows[1:])), 'Baseline sampling gap')
    else:
        require(first['swapUsedMiB']==0, 'Zero-used-swap benchmark baseline required')
    return rows[-1]


def sample(value):
    host(value['host'])
    require(type(value.get('memorySwapCurrent')) is int and value['memorySwapCurrent']==0
            and value.get('memorySwapMax')=='0', 'Container swap or missing no-swap evidence')
    require(value.get('memoryMax')=='10737418240' and value.get('cpuMax')=='500000 100000', 'Container resource limits differ')
    require(type(value.get('memoryPeak')) is int and value['memoryPeak']>=0, 'Missing cgroup high water')
    events=value.get('memoryEvents',{})
    require(all(type(events.get(k)) is int and events[k]==0 for k in ('oom','oom_kill')), 'Cgroup OOM or missing counters')
    require(type(value.get('vmMemAvailable')) is int and value['vmMemAvailable']>=0, 'Missing VM headroom')
    return value


def measures(base, rows, mode, session):
    initial=baseline(base,mode,session)
    require(isinstance(rows,list) and rows, 'Telemetry absent')
    previous=initial;pressured=0
    for row in rows:
        sample(row);current=row['host'];transition(initial,previous,current,mode)
        danger=current['freePercent']<20 or current['pressureLevel']!=1 or row['vmMemAvailable']<2*1024**3
        pressured=pressured+1 if danger else 0
        require(pressured<2, 'Host or VM pressure alarm')
        previous=current
    maximum=max([initial['swapUsedMiB'],*[row['host']['swapUsedMiB'] for row in rows]])
    if mode==ZERO:require(maximum==0, 'Absolute-zero-used-swap benchmark telemetry required')
    return {'session':session,'profile':mode,'samples':len(rows),
            'initialHostSwapMiB':initial['swapUsedMiB'],'maxHostSwapMiB':maximum,
            'maxHostSwapGrowthMiB':max(0,maximum-initial['swapUsedMiB']),
            'swapoutsDelta':rows[-1]['host']['swapouts']-initial['swapouts'],
            'swapinsDelta':rows[-1]['host']['swapins']-initial['swapins'],
            'pageinsDelta':rows[-1]['host']['pageins']-initial['pageins'],
            'peakMemoryBytes':max(row['memoryPeak'] for row in rows),
            'minVmAvailableBytes':min(row['vmMemAvailable'] for row in rows),
            'minHostFreePercent':min(row['host']['freePercent'] for row in rows)}


def validate(root, mode, session):
    """Independently rederive completion/reuse claims from retained raw evidence."""
    record=json.loads((root/'session.json').read_bytes())
    require(record.get('session')==session and record.get('fingerprint',{}).get('resourceProfile')==mode, 'Resource session/profile differs')
    base=json.loads((root/'resources/baseline.json').read_bytes())
    rows=[json.loads(line) for line in (root/'resources/telemetry.jsonl').read_text().splitlines()]
    admitted=json.loads((root/'resources/admission.json').read_bytes())
    initial=baseline(base,mode,session)
    digest=hashlib.sha256((root/'resources/baseline.json').read_bytes()).hexdigest()
    require(admitted.get('session')==session and admitted.get('profile')==mode and admitted.get('baselineSha256')==digest
            and admitted.get('initialHost')==initial, 'Resource admission binding differs')
    at=admitted.get('admittedAt')
    require(type(at) in (int,float) and math.isfinite(at) and 0<=at-initial['time']<=15, 'Stale resource admission')
    require(rows and rows[0]['host']['time']>=at, 'Telemetry predates admission')
    expected=measures(base,rows,mode,session)
    summary=json.loads((root/'resources/summary.json').read_bytes())
    require('alarm' in summary and summary['alarm'] is None and all(summary.get(k)==v for k,v in expected.items()), 'Resource summary disagrees with raw telemetry')
    digest=hashlib.sha256((root/'resources/baseline.json').read_bytes()).hexdigest()
    require(summary.get('baselineSha256')==digest, 'Resource baseline digest differs')
    return {'profile':mode,'baselineSha256':digest,'baseline':base,'admittedAt':at,
            **{k:expected[k] for k in ('initialHostSwapMiB','maxHostSwapMiB','maxHostSwapGrowthMiB','swapoutsDelta','swapinsDelta','pageinsDelta')}}
