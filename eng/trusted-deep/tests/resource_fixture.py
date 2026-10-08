"""Literal host/cgroup corpus: 15 MiB stable pre-existing swap is operational only."""
import hashlib
import json
import time

def fixture(root,fingerprint,used=0):
    mode=fingerprint['resourceProfile'];session='a'*32;start=time.time()-40
    def host(at):return {'time':at,'swapUsedMiB':used,'freePercent':79,'pressureLevel':1,'swapins':12,'swapouts':960,'pageins':1000}
    base={'profile':mode,'session':session,'hostSamples':[host(start+i*3) for i in range(11)] if mode=='operational-stable-swap-v1' else [host(start+30)]}
    folder=root/'resources';folder.mkdir(exist_ok=True,parents=True)
    (folder/'baseline.json').write_text(json.dumps(base))
    sha=hashlib.sha256((folder/'baseline.json').read_bytes()).hexdigest();at=start+31
    ready={'session':session,'profile':mode,'initialHost':base['hostSamples'][-1],'baselineSha256':sha}
    (folder/'ready.json').write_text(json.dumps(ready))
    (folder/'admission.json').write_text(json.dumps(dict(ready,admittedAt=at)))
    rows=[{'host':host(start+i),'memorySwapCurrent':0,'memorySwapMax':'0','memoryMax':'10737418240',
           'cpuMax':'500000 100000','memoryEvents':{'oom':0,'oom_kill':0},'memoryPeak':123456,'vmMemAvailable':3221225472} for i in (32,35)]
    (folder/'telemetry.jsonl').write_text(''.join(json.dumps(row)+'\n' for row in rows))
    (folder/'summary.json').write_text(json.dumps({'session':session,'profile':mode,'alarm':None,'baselineSha256':sha,
        'samples':2,'initialHostSwapMiB':used,'maxHostSwapMiB':used,'maxHostSwapGrowthMiB':0,
        'swapoutsDelta':0,'swapinsDelta':0,'pageinsDelta':0,'peakMemoryBytes':123456,'minVmAvailableBytes':3221225472,'minHostFreePercent':79}))
    (root/'session.json').write_text(json.dumps({'session':session,'fingerprint':fingerprint}))
    return {'profile':mode,'baselineSha256':sha,'baseline':base,'admittedAt':at,'initialHostSwapMiB':used,
            'maxHostSwapMiB':used,'maxHostSwapGrowthMiB':0,'swapoutsDelta':0,'swapinsDelta':0,'pageinsDelta':0}
