"""Pure private-run checks for the future ephemeral adapter; no credentials or dispatch."""
import re
from manifest import CONTROL_ID, DIGEST, SHA, require
WORKFLOW='.github/workflows/api-trusted-deep.yml'
REPO='Harborline-Software/harborline-control'

def binding_shape(binding):
    require(set(binding)=={'runId','jobId','jobKey','workflowSha','workflowRef','taskId','manifestSha256'},'Incomplete private job binding')
    require(all(isinstance(binding[k],str) and re.fullmatch('[1-9][0-9]*',binding[k]) for k in ('runId','jobId')),'Invalid run/job ID')
    require(binding['jobKey']=='deep' and SHA.fullmatch(binding['workflowSha']),'Wrong job/workflow identity')
    require(binding['workflowRef']==REPO+'/'+WORKFLOW+'@refs/heads/main','Unreviewed workflow authority')
    require(re.fullmatch('[a-z][a-z0-9-]{0,39}',binding['taskId']) and DIGEST.fullmatch(binding['manifestSha256']),'Invalid task/manifest binding')
    return binding

def validate_run(run,jobs,approved_workflow_bytes,live_workflow_bytes,manifest):
    require(run['repository']['id']==CONTROL_ID and run['repository']['private'] is True and run['head_repository']['id']==CONTROL_ID,'Wrong/private repository required')
    require(run['event'] in ('schedule','workflow_dispatch') and run['run_attempt']==1,'Only trusted first attempts')
    require(run['path']==WORKFLOW and run['head_branch']=='main' and SHA.fullmatch(run['head_sha']),'Wrong workflow source')
    require(run['status'] in ('queued','waiting','pending','in_progress') and run['conclusion'] is None,'Run is not pending')
    require(live_workflow_bytes==approved_workflow_bytes,'Private workflow bytes require review')
    for name in ('actor','triggering_actor'):
        require(run[name]['id']==1328090 and run[name]['login']=='ctwoodwa','Untrusted actor')
    expected={'deep ('+task['id']+')':task for task in manifest['tasks']}
    require(len(jobs)==len(expected) and {job['name'] for job in jobs}==set(expected),'Unexpected private job inventory')
    bindings=[]
    for job in jobs:
        require(job['status']=='queued' and not job.get('runner_id'),'Private job already assigned')
        task=expected[job['name']]
        require(set(job['labels'])=={'self-hosted','harborline-api-trusted-deep-v1','linux-arm64-orbstack','harborline-run-'+str(run['id']),'task-'+task['id']},'Run/task labels changed')
        bindings.append(binding_shape({'runId':str(run['id']),'jobId':str(job['id']),'jobKey':'deep',
            'workflowSha':run['head_sha'],'workflowRef':REPO+'/'+WORKFLOW+'@refs/heads/main',
            'taskId':task['id'],'manifestSha256':manifest['approvedDigest']}))
    return bindings

def validate_assignment(binding,job,runner):
    """Host uses fresh GitHub job/runner API rows before permitting payload execution."""
    binding_shape(binding)
    require(str(job['id'])==binding['jobId'] and str(job['run_id'])==binding['runId'],'Numeric job/run assignment differs')
    require(job['status']=='in_progress' and job['conclusion'] is None and job['name']=='deep ('+binding['taskId']+')','Unexpected assigned job')
    expected_name='hl-trusted-'+binding['runId']+'-'+binding['jobId']
    require(job['runner_id']==runner['id'] and job['runner_name']==runner['name']==expected_name,'Job assigned to another runner')
    require(runner['status']=='online' and runner['busy'] is True,'Runner assignment not live')
    return {'binding':binding,'runnerId':runner['id'],'runnerName':expected_name,'verified':True}

def admit(event,env,binding,assignment):
    binding_shape(binding)
    from manifest import admit_event
    admit_event(event,env,binding['workflowSha'])
    expected={'GITHUB_RUN_ID':binding['runId'],'GITHUB_JOB':binding['jobKey'],
              'GITHUB_WORKFLOW_REF':binding['workflowRef'],'GITHUB_SHA':binding['workflowSha'],
              'HARBORLINE_APPROVED_MANIFEST_SHA256':binding['manifestSha256'],'HARBORLINE_TASK_ID':binding['taskId']}
    require(all(env.get(k)==v for k,v in expected.items()),'Runtime differs from immutable private-job binding')
    require(assignment['verified'] is True and assignment['binding']==binding and type(assignment['runnerId']) is int and assignment['runnerId']>0,'Host has not verified numeric job-to-runner assignment')
    require(assignment['runnerName']=='hl-trusted-'+binding['runId']+'-'+binding['jobId'] and env.get('RUNNER_NAME')==assignment['runnerName'],'Runtime runner differs from verified assignment')
