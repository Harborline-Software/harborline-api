import os,pathlib,json,time,collections
P=pathlib.Path
cg=P('/sys/fs/cgroup')
def val(file):
 try:return (cg/file).read_text().strip()
 except OSError:return None
def pairs(text):return {k:int(v) for k,v in (line.split() for line in (text or '').splitlines())}
classes=collections.defaultdict(lambda:{'count':0,'rssBytes':0,'threads':0})
processes=[]
for p in P('/proc').iterdir():
 if not p.name.isdecimal() or int(p.name)==os.getpid():continue
 try:
  cmd=(p/'cmdline').read_bytes().replace(b'\0',b' ').decode(errors='replace')
  status=(p/'status').read_text();fields={line.split(':',1)[0]:line.split(':',1)[1].strip() for line in status.splitlines() if ':' in line}
  rss=int(fields.get('VmRSS','0 kB').split()[0])*1024;threads=int(fields.get('Threads','0'))
  name=fields.get('Name','unknown')
  kind=next((label for token,label in [('MSBuild.dll','msbuild'),('VBCSCompiler.dll','compiler-server'),('testhost.dll','testhost'),('vstest.console.dll','vstest'),('Harborline','harborline'),('vitest','vitest')] if token in cmd),name)
  classes[kind]['count']+=1;classes[kind]['rssBytes']+=rss;classes[kind]['threads']+=threads
  processes.append({'pid':int(p.name),'class':kind,'rssBytes':rss,'threads':threads})
 except (OSError,ValueError):pass
mem={line.split(':',1)[0]:int(line.split(':',1)[1].split()[0])*1024 for line in P('/proc/meminfo').read_text().splitlines() if line.endswith('kB')}
phase='bootstrap'
log=P('/runner/gate/out/gate.log')
if log.exists():
 for line in log.read_text(errors='replace').splitlines():
  if '── ' in line:phase=line.split('── ',1)[1].replace('\x1b[0m','')
  if line.startswith('[exact-clone] '):
   try:row=json.loads(line[len('[exact-clone] '):]);phase='exact-clone/'+row['id']+'/'+row['state']
   except (ValueError,KeyError):pass
print(json.dumps({'time':time.time(),'phase':phase,'memoryCurrent':int(val('memory.current')),'memoryPeak':int(val('memory.peak')),'memoryMax':val('memory.max'),'memorySwapMax':val('memory.swap.max'),'memoryEvents':pairs(val('memory.events')),'memoryStat':{k:v for k,v in pairs(val('memory.stat')).items() if k in ('anon','file','kernel','slab','sock','shmem')},'cpuMax':val('cpu.max'),'pidsCurrent':val('pids.current'),'vmMemAvailable':mem.get('MemAvailable'),'vmMemTotal':mem.get('MemTotal'),'classes':dict(classes),'topProcesses':sorted(processes,key=lambda p:p['rssBytes'],reverse=True)[:12]}))
