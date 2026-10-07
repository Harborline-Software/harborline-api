"""Immutable local-pilot hook; a GitHub job cannot turn on the local admission path."""
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as E
sys.path.insert(0,str(Path(__file__).resolve().parent))
import manifest as m
import private_admission
sys.path.insert(0,'/opt/mini')
import controller as c

ROOT=Path('/runner/gate')
def admit(kind):
    if any(k.startswith('GITHUB_') for k in os.environ):
        binding=json.loads(Path('/opt/trusted/private-binding.json').read_text())
        event=json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
        assignment=json.loads(Path('/opt/trusted/private-assignment.json').read_text())
        private_admission.admit(event,os.environ,binding,assignment)
    else:
        m.require(not Path('/opt/trusted/private-binding.json').exists(),'Private image cannot enter local admission')
    value=m.load('/runner/approved-manifest.json',os.environ.get('HARBORLINE_APPROVED_MANIFEST_SHA256'))
    task=next(t for t in value['tasks'] if t['id']==os.environ.get('HARBORLINE_TASK_ID'))
    m.require(task['kind']==kind,'Task kind differs from approved manifest')
    policy=json.loads(Path('/opt/mini/policy.json').read_text())
    sources=json.loads(Path('/opt/mini/sources.json').read_text())
    m.require({k:v['head'] for k,v in sources.items()}==value['sources'],'Source approval differs from image')
    m.require(policy['head']==value['sources']['api'] and policy['tree']==value['tree'] and policy['sdk']==value['sdk'],'Source/tree/SDK changed')
    m.require(value['base']==policy['head'],'Snapshot base differs from restored origin/main')
    m.require(policy['inputDigests']==value['inputDigests'],'Verification input profile changed')
    m.require(os.environ.get('XDG_DATA_HOME')==policy['environment']['xdgData'] and
              os.environ.get('XDG_CONFIG_HOME')==policy['environment']['xdgConfig'],'Writable private runtime profile differs')
    m.require(c.digest('/opt/mini/sources.json')==policy['sourcesSha256'],'Source bundle manifest changed')
    return value,task

def finish(kind):
    value,task=admit(kind)
    for name,head in value['sources'].items():
        m.require(c.git(ROOT/name,'rev-parse','HEAD')==head,'Source head changed')
        m.require(not c.git(ROOT/name,'status','--porcelain'),'Dirty tested source: '+name)
    m.require(c.git(ROOT/'api','rev-parse','HEAD^{tree}')==value['tree'],'Tree changed')
    m.require(c.git(ROOT/'api','rev-parse','refs/remotes/origin/main')==value['base'],'Measured comparison base differs')
    m.require(subprocess.check_output(['dotnet','--version'],text=True).strip()==value['sdk'],'SDK changed')
    if kind.startswith('portable'):
        c.validate_receipt(ROOT/'out',value['sources']['api'],value['tree'],kind=='portable-coverage')
        if kind=='portable-coverage':
            receipt=json.loads((ROOT/'out/harborline-api-verify-receipt.json').read_text())
            for name in ('host','contracts'):
                tree=E.parse(ROOT/'out/quality'/(name+'.cobertura.xml')).getroot()
                lines=tree.findall('.//class/lines/line')
                covered=sum(int(line.attrib['hits'])>0 for line in lines)
                m.require(len(lines)==int(tree.attrib['lines-valid']) and covered==int(tree.attrib['lines-covered']),'Coverage XML counters disagree with lines')
                m.require(receipt['coverage'][name]['validLines']==len(lines) and receipt['coverage'][name]['coveredLines']==covered,'Coverage receipt/XML mismatch')
    subprocess.run(['node','/opt/trusted/raw-evidence.mjs',kind,str(ROOT/'out'),str(ROOT/'api')],check=True)
    (ROOT/'out/immutable-completion.json').write_text(json.dumps({'manifestSha256':m.digest('/runner/approved-manifest.json'),
        'task':task,'head':value['sources']['api'],'tree':value['tree'],'verdict':'passed',
        'privateAssignment':json.loads(Path('/opt/trusted/private-assignment.json').read_text()) if Path('/opt/trusted/private-assignment.json').exists() else None},indent=2)+'\n')

if __name__=='__main__':
    if sys.argv[1]=='admit':admit(sys.argv[2])
    elif sys.argv[1]=='finish':finish(sys.argv[2])
    elif sys.argv[1]=='private-kind':
        binding=json.loads(Path('/opt/trusted/private-binding.json').read_text())
        event=json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
        private_admission.admit(event,os.environ,binding,json.loads(Path('/opt/trusted/private-assignment.json').read_text()))
        value=m.load('/runner/approved-manifest.json',binding['manifestSha256'])
        print(next(t['kind'] for t in value['tasks'] if t['id']==binding['taskId']))
    else:raise SystemExit('Unknown hook action')
