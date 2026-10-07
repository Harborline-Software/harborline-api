"""Normalize downloaded hosted artifacts; approval of the resulting digest remains external."""
import argparse
import datetime
import json
from pathlib import Path
import shutil
import subprocess
from evidence import ROOT, MODULE, native_context, required_artifacts, validate_native_raw
from manifest import digest, require

def main(args):
    source=Path(args.artifact_root).resolve();out=Path(args.output).resolve();out.mkdir(mode=0o700)
    expected=json.loads(Path(args.expected).read_bytes())
    require(expected['kind']=='native-full' and expected['os'] in ('windows','macos'),'Full native expectation required')
    evidence=source/'.claude/gate-evidence'
    require(evidence.is_dir() and not evidence.is_symlink(),'Complete original hosted artifact layout required')
    (out/'out/gate-evidence').mkdir(parents=True)
    for name in required_artifacts(expected):
        if not name.startswith('out/gate-evidence/'):continue
        raw=evidence/Path(name).name
        require(raw.is_file() and not raw.is_symlink() and raw.resolve().is_relative_to(source),'Unsafe native artifact')
        shutil.copyfile(raw,out/name)
    shutil.copyfile(source/'.git/harborline-api-verify-receipt.json',out/'out/harborline-api-verify-receipt.json')
    (out/'hosted-job.json').write_text(json.dumps({'runId':args.run_id,'jobId':args.job_id})+'\n')
    context=native_context(out,expected)
    require(subprocess.check_output(['git','-C',str(ROOT),'rev-parse','HEAD'],text=True).strip()==expected['api'],'Use reviewed exact-candidate reader')
    subprocess.run(['node',str(MODULE/'raw-evidence.mjs'),'native-full',str(out/'out'),str(ROOT)],check=True,timeout=60)
    validate_native_raw(out,expected)
    from release import verify_native_job
    job=verify_native_job(expected['os'],out,expected['api'],expected)
    receipt={'fingerprint':expected,'status':'passed','cleanup':True,'oom':0,'swapMiB':0,
             'completedAt':job['completed_at'],'suite':'full','criticalCheckSet':'api-required-v1',
             'artifacts':{name:digest(out/name) for name in required_artifacts(expected)}}
    (out/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps({'receiptSha256':digest(out/'receipt.json'),'platform':expected['os'],'output':str(out)}))

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ('artifact-root','expected','output'):parser.add_argument('--'+name,required=True)
    for name in ('run-id','job-id'):parser.add_argument('--'+name,required=True,type=int)
    main(parser.parse_args())
