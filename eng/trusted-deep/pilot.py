"""Explicit local trusted tasks, not a scheduler or a GitHub registration service."""
import argparse
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import uuid
from manifest import digest, fingerprint, load, require
import slots
import resource_profile as rp

ROOT=Path(__file__).resolve().parents[2]
OBSERVER=Path(__file__).with_name('observer.py')
LABEL='org.harborline.mini.session'
SLOT_LABEL='org.harborline.trusted.slot'

def command(argv,timeout=60):
    result=subprocess.run(argv,capture_output=True,text=True,timeout=timeout)
    if result.returncode:raise RuntimeError(Path(argv[0]).name+' operation failed; inspect scoped evidence')
    return result.stdout

def docker(*argv):return command(['docker',*argv])
def write(path,value):Path(path).write_text(json.dumps(value,indent=2)+'\n')
def stamp(pid):return command(['ps','-p',str(pid),'-o','lstart=']).strip()
def locked(path):
    fd=slots.opened(path)
    try:
        try:fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except BlockingIOError:return True
        return False
    finally:os.close(fd)

def inventory(root=slots.DEFAULT):
    """A surviving Docker workload is not evidence of a surviving host lease."""
    names=docker('ps','--format','{{.Names}}').splitlines()
    require(len(names)<=2,'More than two active workloads; refuse admission')
    for name in names:
        require(re.fullmatch('hl-mini-[0-9a-f]{32}-a',name),'Unrelated active container; refuse overlap')
        row=json.loads(docker('inspect',name))[0]
        number=row['Config']['Labels'].get(SLOT_LABEL)
        require(number in ('0','1'),'Unmanaged/orphan mini workload')
        claim_path=Path(root)/f'trusted-deep-slot-{number}.json'
        require(claim_path.is_file() and not claim_path.is_symlink(),'Missing slot journal')
        claim=json.loads(claim_path.read_text())
        require(claim['container']==name and claim['session']==row['Config']['Labels'].get(LABEL),'Slot journal/container mismatch')
        require(locked(Path(root)/f'trusted-deep-slot-{number}.lock'),'Orphan container has no live slot lease')
        require(stamp(claim['pid'])==claim['processStamp'],'Orphan owner process changed')
        require(row['HostConfig']['Memory']==10*2**30 and row['HostConfig']['MemorySwap']==10*2**30,'Unexpected memory/swap limits')
    return names

def cleanup(session,name):
    failures=[]
    for kind,scan,remove in [('container',('ps','--all'),('rm','-f')),('network',('network','ls'),('network','rm')),('volume',('volume','ls'),('volume','rm'))]:
        try:
            names=set(docker(*scan,'--filter','label='+LABEL+'='+session,'--format','{{.Name}}' if kind!='container' else '{{.Names}}').splitlines())
            require(names<={name},'Unexpected session resource names')
            if names:docker(*remove,name)
            remaining=docker(*scan,'--filter','label='+LABEL+'='+session,'--format','{{.Name}}' if kind!='container' else '{{.Names}}').strip()
            require(not remaining,'Scoped resource remains after cleanup')
        except Exception as error:failures.append(kind+':'+type(error).__name__)
    return {'session':session,'clean':not failures,'failures':failures}

def incident(session,manifest_digest,task,error,state='open'):
    folder=slots.DEFAULT/'trusted-deep-incidents';folder.mkdir(mode=0o700,exist_ok=True)
    target=folder/(session+'.json');temporary=folder/(session+'.tmp')
    with temporary.open('x') as out:
        json.dump({'owner':'ctwoodwa','state':state,'manifestSha256':manifest_digest,'task':task,
                   'cause':str(error)[:240],'createdAt':datetime.datetime.now(datetime.timezone.utc).isoformat()},out,indent=2)
        out.flush();os.fsync(out.fileno())
    temporary.replace(target)

def stop_and_wait(process):
    if process is not None and process.poll() is None:
        os.killpg(process.pid,signal.SIGTERM)
        try:process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid,signal.SIGKILL)
            process.wait(timeout=10)

def await_resource_admission(observer,output,session,mode=rp.ZERO):
    deadline=time.monotonic()+60;path=output/'resources/ready.json'
    while not path.exists():
        require(observer.poll() is None,'Resource admission failed before ready')
        require(time.monotonic()<deadline,'Resource admission deadline')
        time.sleep(.2)
    ready=json.loads(path.read_bytes());initial=ready['initialHost']
    require(observer.poll() is None and ready['session']==session,'Resource admission identity/liveness differs')
    base=json.loads((output/'resources/baseline.json').read_bytes())
    measured=rp.baseline(base,mode,session);at=time.time()
    require(ready.get('profile')==mode and ready.get('baselineSha256')==digest(output/'resources/baseline.json')
            and initial==measured and 0<=at-initial['time']<=15, 'Resource baseline admission failed/stale')
    write(output/'resources/admission.json',dict(ready,admittedAt=at))

def teardown(session,name,output,child,observer,claim_path,claim_published):
    """Resource/process draining precedes optional evidence I/O, even on ENOSPC."""
    failures=[];clean={'session':session,'clean':False,'failures':['cleanup not completed']}
    try:clean=cleanup(session,name)
    except BaseException as error:failures.append(error)
    finally:
        for process in (child,observer):
            try:stop_and_wait(process)
            except BaseException as error:failures.append(error)
    try:
        if clean['clean'] and not failures and claim_published and claim_path.exists():
            require(json.loads(claim_path.read_text())['session']==session,'Slot journal ownership changed')
            claim_path.unlink()
    except BaseException as error:failures.append(error)
    try:write(output/'cleanup.json',clean)
    except BaseException as error:failures.append(error)
    return clean,failures

def run(args):
    value=load(args.manifest,args.approved_digest)
    require(command(['git','-C',str(ROOT),'rev-parse','HEAD']).strip()==value['sources']['api'] and
            not command(['git','-C',str(ROOT),'status','--porcelain']).strip(),'Host controller/observer must match clean approved source')
    task=next(t for t in value['tasks'] if t['id']==args.task)
    output=Path(args.output).resolve();output.mkdir(mode=0o700)
    session=uuid.uuid4().hex;name='hl-mini-'+session+'-a'
    observer=child=None;claim_path=None;claim_published=False;clean=None;error=None;terminal=None
    def interrupted(*_):raise KeyboardInterrupt('Trusted local task interrupted')
    signal.signal(signal.SIGINT,interrupted);signal.signal(signal.SIGTERM,interrupted)
    with slots.lease(exclusive=task['kind']=='mutation-benchmark') as lease:
        fds=slots.inherited(lease)
        require(int(docker('info','--format','{{.MemTotal}}'))>=23*2**30,'Insufficient VM memory')
        image=json.loads(docker('image','inspect',value['image']))[0]
        require(image['Id']==value['image'] and image['Architecture']=='arm64' and image['Config']['User']=='1001:1001','Image identity/platform changed')
        record={'session':session,'container':name,'task':task,'slot':lease['slot'],'pid':os.getpid(),'processStamp':stamp(os.getpid()),
                'manifestSha256':args.approved_digest,'image':value['image'],'fingerprint':fingerprint(value,task)}
        claim_path=slots.DEFAULT/f"trusted-deep-slot-{lease['slot']}.json"
        # A crash or ENOSPC after this point holds promotion, even without a journal.
        incident(session,args.approved_digest,task,'Awaiting completed evidence and cleanup',state='pending')
        try:
            write(output/'session.json',record)
            admission=slots.opened(slots.DEFAULT/'trusted-deep-admission.lock',create=True)
            try:
                fcntl.flock(admission,fcntl.LOCK_EX)
                inventory()
                require(not claim_path.exists(),'Stale slot journal requires scoped recovery')
                write(claim_path,record)
                claim_published=True
                for kind in ('volume','network'):docker(kind,'create','--label',LABEL+'='+session,name)
                docker('create','--name',name,'--label',LABEL+'='+session,'--label',SLOT_LABEL+'='+str(lease['slot']),
                       '--init','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--memory','10g','--memory-swap','10g',
                       '--cpus','5','--pids-limit','1024','--network',name,'--mount','type=volume,src='+name+',dst=/runner',
                       '--tmpfs','/tmp:rw,nosuid,nodev,size=512m,mode=1777','--env','HARBORLINE_APPROVED_MANIFEST_SHA256='+args.approved_digest,
                       '--env','XDG_DATA_HOME=/runner/gate/cache/xdg-data','--env','XDG_CONFIG_HOME=/runner/gate/cache/xdg-config',
                       '--env','DOTNET_GCHeapHardLimitPercent=0x32',
                       '--env','HARBORLINE_TASK_ID='+task['id'],value['image'],task['kind'])
                docker('cp',str(Path(args.manifest).resolve()),name+':/runner/approved-manifest.json')
            finally:os.close(admission)
            with (output/'observer.log').open('x') as obs,(output/'container.log').open('x') as log:
                observer=subprocess.Popen(['python3','-B',str(OBSERVER),'--session-file',str(output/'session.json'),'--output',str(output/'resources'),'--resource-profile',value['resourceProfile']],
                                          stdout=obs,stderr=subprocess.STDOUT,pass_fds=fds,start_new_session=True)
                await_resource_admission(observer,output,session,value['resourceProfile'])
                child=subprocess.Popen(['docker','start','-a',name],stdout=log,stderr=subprocess.STDOUT,pass_fds=fds,start_new_session=True)
                started=time.monotonic();deadline=started+3400;next_inventory=started+15
                while child.poll() is None:
                    slots.inherited(lease)
                    if time.monotonic()>=next_inventory:
                        inventory();next_inventory=time.monotonic()+15
                    if observer.poll() not in (None,0):raise RuntimeError('Resource alarm')
                    if time.monotonic()>deadline:raise RuntimeError('Local task deadline')
                    time.sleep(3)
                terminal=json.loads(docker('inspect',name))[0]['State'];write(output/'terminal.json',terminal)
                docker('cp',name+':/runner/gate/out',str(output/'out'))
                require(terminal['ExitCode']==0 and terminal['OOMKilled'] is False,'Trusted workload failed')
                write(output/'result.json',{'success':False,'awaitingCleanup':True})
                observer.wait(timeout=60)
                resources=json.loads((output/'resources/summary.json').read_text())
                resource_proof=rp.validate(output,value['resourceProfile'],session)
                write(output/'timing.json',{'executionSeconds':time.monotonic()-started})
        except BaseException as caught:
            error=caught
        finally:
            signal.signal(signal.SIGINT,signal.SIG_IGN);signal.signal(signal.SIGTERM,signal.SIG_IGN)
            clean,failures=teardown(session,name,output,child,observer,claim_path,claim_published)
            error=error or (failures[0] if failures else None)
            if error is not None or not clean['clean']:
                incident(session,args.approved_digest,task,error or 'Scoped cleanup incomplete')
                write(output/'result.json',{'success':False,'failure':type(error).__name__ if error else 'CleanupFailure'})
            else:
                try:
                    artifacts={str(p.relative_to(output)):digest(p) for p in sorted(output.rglob('*')) if p.is_file() and p.name!='result.json'}
                    receipt={'fingerprint':fingerprint(value,task),'status':'passed','cleanup':True,'oom':0,'swapMiB':resource_proof['maxHostSwapMiB'],'resourceProof':resource_proof,
                             'completedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'suite':'full' if task['kind'].startswith('portable') else 'mutation-benchmark',
                             'criticalCheckSet':'api-portable-all17-v1','artifacts':artifacts,'manifestSha256':args.approved_digest}
                    write(output/'receipt.json',receipt)
                    write(output/'result.json',{'success':True,'receiptSha256':digest(output/'receipt.json')})
                    (slots.DEFAULT/'trusted-deep-incidents'/(session+'.json')).unlink()
                    print(json.dumps({'task':task['id'],'slot':lease['slot'],'success':True,'output':str(output)}),flush=True)
                except BaseException as caught:
                    error=caught
                    incident(session,args.approved_digest,task,error)
    if error is not None:raise error
    require(clean['clean'],'Cleanup failed')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('action',choices=['run'])
    for name in ('manifest','approved-digest','task','output'):p.add_argument('--'+name,required=True)
    run(p.parse_args())
