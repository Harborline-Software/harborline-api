"""Read-only exact-candidate release verdict; never deploys or relaxes required checks."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
from evidence import release_ready, native_context
from manifest import digest, require
import slots

def api(path):return json.loads(subprocess.check_output(['gh','api','repos/Harborline-Software/harborline-api/'+path],text=True,timeout=60))
def verify_native_job(platform,root,candidate,expected):
    context=native_context(root,expected)
    indexed=json.loads((root/'hosted-job.json').read_bytes())
    run=api('actions/runs/'+str(indexed['runId']))
    jobs=api('actions/runs/'+str(indexed['runId'])+'/jobs?per_page=100')
    require(jobs['total_count']==len(jobs['jobs']),'Incomplete native job inventory')
    job,=[j for j in jobs['jobs'] if j['id']==indexed['jobId']]
    require(run['repository']['id']==1360432948 and run['head_sha']==candidate and run['run_attempt']==1,'Native run source changed')
    require(run['path']=='.github/workflows/verify.yml' and run['event']=='workflow_dispatch','Native release must use full manual verify')
    require(job['name']=={'windows':'verify-windows-hosted','macos':'verify-macos'}[platform] and job['conclusion']=='success','Native job did not pass')
    require('self-hosted' not in job['labels'] and not any('mini' in x for x in job['labels']),'Wrong native execution environment')
    require(context['runId']==str(run['id']) and context['attempt']=='1' and context['sha']==candidate and context['platform']==platform,'Native context differs from GitHub')
    require(context['sdk']==expected['sdk'] and context['architecture']==expected['architecture'] and
            context['workflowSha']==run['head_sha'] and context['jobKey']==job['name'] and
            context['repositoryId']==str(run['repository']['id']),'Native measured execution differs from approved/GitHub identity')
    require(job['status']=='completed' and job['completed_at'],'Native job is incomplete')
    return job

def main(args):
    index_path=Path(args.index).resolve();require(digest(index_path)==args.approved_index_digest,'Release index differs from approved bytes')
    index=json.loads(index_path.read_bytes());candidate=index['candidate']
    require(index['owner']=='ctwoodwa','Release ownership changed')
    incidents_dir=slots.DEFAULT/'trusted-deep-incidents'
    require(incidents_dir.is_dir(),'Promotion state has not been initialized by the operator')
    incidents=[p.name for p in incidents_dir.glob('*.json') if json.loads(p.read_bytes())['state']!='resolved']
    require(not list(slots.DEFAULT.glob('trusted-deep-slot-*.json')),'Pending/stale workload journal holds promotion')
    roots={};receipts={};digests={};expected={}
    for platform,item in index['platforms'].items():
        root=(index_path.parent/item['directory']).resolve();require(root.is_relative_to(index_path.parent),'Evidence directory outside trusted index')
        roots[platform]=root;receipts[platform]=(root/'receipt.json').read_bytes();digests[platform]=item['receiptSha256'];expected[platform]=item['fingerprint']
    release_ready(candidate,expected,receipts,roots,digests,incidents,datetime.datetime.now(datetime.timezone.utc))
    for platform in ('windows','macos'):verify_native_job(platform,roots[platform],candidate,expected[platform])
    checks=api('commits/'+candidate+'/check-runs?per_page=100');require(checks['total_count']==len(checks['check_runs']),'Incomplete critical check inventory')
    for name in ('verify','operator-cli-headless','pack-consume','protocol-lane-conformance','sbom'):
        rows=[r for r in checks['check_runs'] if r['name']==name];require(rows,'Required critical context absent')
        row=max(rows,key=lambda r:r['id'])
        require(row['head_sha']==candidate and row['conclusion']=='success' and row['app']['slug']=='github-actions','Required critical context did not pass')
    with Path(args.output).open('x') as out:json.dump({'candidate':candidate,'verdict':'release-evidence-ready','nativePlatforms':['windows','macos'],'promotionIncidents':incidents},out,indent=2)

if __name__=='__main__':
    p=argparse.ArgumentParser()
    for name in ('index','approved-index-digest','output'):p.add_argument('--'+name,required=True)
    main(p.parse_args())
