"""Prepare immutable committed inputs for explicit local pilot tasks; no runner registration."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
from manifest import digest, require

ROOT = Path(__file__).resolve().parents[2]
TOOLCHAIN = 'sha256:a0f23ee21d5b3b689451bd09219342b7f985d893babf8fdce71cbb87b98f44c0'

def git(root, *args):
    return subprocess.check_output(['git','-C',str(root),*args]).decode().strip()

def files_digest(root, files):
    result = hashlib.sha256()
    for name in sorted(files):
        data = subprocess.check_output(['git','-C',str(root),'show','HEAD:'+name])
        result.update(name.encode()+b'\0'+hashlib.sha256(data).digest())
    return result.hexdigest()

def prepare(args):
    paths = {name:Path(getattr(args,name)).resolve() for name in ('api','platform','quality','control')}
    heads = {name:git(path,'rev-parse','HEAD') for name,path in paths.items()}
    require(git(ROOT,'rev-parse','HEAD')==heads['api'] and not git(ROOT,'status','--porcelain'), 'Controller bytes must be the same clean approved revision')
    require(not git(paths['api'],'status','--porcelain'), 'Commit the reviewed pilot before bundling; dirty source is refused')
    for name in ('platform','quality'):
        pin = json.loads(git(paths['api'],'show','HEAD:eng/'+name+'-pin.json'))['commit']
        require(heads[name] == pin, name+' is not at the recorded pin')
    require(heads['control'] == args.control_head, 'Control differs from explicitly selected revision')
    sdk = json.loads(git(paths['api'],'show','HEAD:global.json'))['sdk']
    require(sdk['rollForward'] == 'disable', 'Unreviewed SDK policy')
    tree = git(paths['api'],'rev-parse','HEAD^{tree}')
    tracked = git(paths['api'],'ls-files').splitlines()
    environment = {'memoryBytes':10*2**30,'swapBytes':0,'cpus':5,'pids':1024,'user':'1001:1001',
                   'readOnly':True,'hostBinds':False,'dockerSocket':False,'privateNetwork':True,'toolchain':TOOLCHAIN,
                   'xdgData':'/runner/gate/cache/xdg-data','xdgConfig':'/runner/gate/cache/xdg-config'}
    selection = {'portable':'existing all17','portable-coverage':'existing all17+coverage',
                 'mutation-benchmark':'full tests/Harborline.Api.Tests/Harborline.Api.Tests.csproj','native-full':'full existing host and capability suites'}
    categories = {
        'scripts':[f for f in tracked if f.startswith(('eng/','.github/'))],
        'dependencyInputs':[f for f in tracked if f.endswith(('.csproj','.props','.targets','lock.yaml','package.json','global.json','nuget.config','NuGet.Config','.slnx'))],
        'baselines':[f for f in tracked if f.startswith('eng/baselines/')],
        'testInventory':[f for f in tracked if '/tests/' in f or f.startswith('tests/')],
        'coverageProfile':['eng/coverage.runsettings'],
    }
    input_digests = {name:files_digest(paths['api'],files) for name,files in categories.items()}
    for name,value in [('testSelection',selection),('environment',environment)]:
        input_digests[name] = hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    target = Path(args.output).resolve();target.mkdir(mode=0o700);(target/'bundles').mkdir()
    sources = {}
    for name,path in paths.items():
        bundle = target/'bundles'/(name+'.bundle')
        git(path,'bundle','create',str(bundle),'HEAD','--tags')
        sources[name] = {'head':heads[name],'bundleSha256':digest(bundle)}
    policy = {'version':1,'runId':'1','head':heads['api'],'tree':tree,'sdk':sdk['version'],'coverage':False,
              'sourcesSha256':None,'inputDigests':input_digests,'environment':environment}
    (target/'sources.json').write_text(json.dumps(sources,indent=2)+'\n')
    policy['sourcesSha256'] = digest(target/'sources.json')
    (target/'policy.json').write_text(json.dumps(policy,indent=2)+'\n')
    # All execution/validation bytes come from this reviewed controller checkout, not the tested clone.
    for name in ('controller.py','admission.py','checkout-sources.py','portable-gate.sh'):
        shutil.copyfile(ROOT/'eng/mini-runner'/name,target/name)
    for name in ('run.sh','job.py','coverage_evidence.py','manifest.py','private_admission.py','private-job.sh','evidence.py','raw-evidence.mjs','Dockerfile','observer.py','telemetry.py'):
        shutil.copyfile(ROOT/'eng/trusted-deep'/name,target/name)
    shutil.copyfile(ROOT/'eng/host-baseline.mjs',target/'host-baseline.mjs')
    (target/'manifest-inputs.json').write_text(json.dumps({'version':1,'repositoryId':1360432948,'owner':'ctwoodwa',
        'sources':heads,'tree':tree,'base':heads['api'],'sdk':sdk['version'],'inputDigests':input_digests},indent=2)+'\n')
    print(target)

if __name__ == '__main__':
    p=argparse.ArgumentParser()
    for name in ('api','platform','quality','control','control-head','output'):p.add_argument('--'+name,required=True)
    prepare(p.parse_args())
