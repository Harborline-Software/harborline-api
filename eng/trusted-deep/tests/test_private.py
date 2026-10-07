import copy
import json
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import private_admission as p

class PrivateAdmission(unittest.TestCase):
    def fixture(self):
        run={'id':42,'repository':{'id':1337465220,'private':True},'head_repository':{'id':1337465220},
             'event':'workflow_dispatch','run_attempt':1,'path':'.github/workflows/api-trusted-deep.yml',
             'head_branch':'main','head_sha':'a'*40,'status':'queued','conclusion':None,
             'actor':{'id':1328090,'login':'ctwoodwa'},'triggering_actor':{'id':1328090,'login':'ctwoodwa'}}
        manifest={'approvedDigest':'b'*64,'tasks':[{'id':'portable-a','kind':'portable'},{'id':'coverage-b','kind':'portable-coverage'}]}
        jobs=[{'id':n+100,'name':'deep ('+task['id']+')','status':'queued','runner_id':0,
               'labels':['self-hosted','harborline-api-trusted-deep-v1','linux-arm64-orbstack','harborline-run-42','task-'+task['id']]} for n,task in enumerate(manifest['tasks'])]
        return run,jobs,manifest
    def test_host_requires_exact_private_run_inventory_and_workflow(self):
        run,jobs,manifest=self.fixture();self.assertEqual(len(p.validate_run(run,jobs,b'reviewed',b'reviewed',manifest)),2)
        for key,bad in [('repository',{'id':1360432948,'private':False}),('head_repository',{'id':1360432948}),('event','pull_request'),('head_branch','feature'),('path','other.yml'),('run_attempt',2)]:
            with self.subTest(key=key),self.assertRaises(ValueError):p.validate_run(dict(run,**{key:bad}),jobs,b'reviewed',b'reviewed',manifest)
        for inventory in (jobs[:1],jobs+jobs[:1],[]):
            with self.assertRaises(ValueError):p.validate_run(run,inventory,b'reviewed',b'reviewed',manifest)
        with self.assertRaises(ValueError):p.validate_run(run,jobs,b'reviewed',b'changed',manifest)
        for key,bad in [('runner_id',9),('status','in_progress'),('labels',['self-hosted']),('id',0)]:
            altered=copy.deepcopy(jobs);altered[0][key]=bad
            with self.subTest(key=key),self.assertRaises(ValueError):p.validate_run(run,altered,b'reviewed',b'reviewed',manifest)
    def test_runtime_cannot_switch_run_job_workflow_manifest_or_task(self):
        run,jobs,manifest=self.fixture();binding=p.validate_run(run,jobs,b'x',b'x',manifest)[0]
        env={'GITHUB_REPOSITORY_ID':'1337465220','GITHUB_REPOSITORY':'Harborline-Software/harborline-control',
             'GITHUB_REF':'refs/heads/main','GITHUB_WORKFLOW_SHA':'a'*40,'GITHUB_ACTOR_ID':'1328090','GITHUB_ACTOR':'ctwoodwa',
             'GITHUB_TRIGGERING_ACTOR':'ctwoodwa','GITHUB_RUN_ATTEMPT':'1','GITHUB_EVENT_NAME':'workflow_dispatch',
             'GITHUB_RUN_ID':'42','GITHUB_JOB':'deep','GITHUB_WORKFLOW_REF':binding['workflowRef'],'GITHUB_SHA':'a'*40,
             'HARBORLINE_APPROVED_MANIFEST_SHA256':'b'*64,'HARBORLINE_TASK_ID':'portable-a','RUNNER_NAME':'hl-trusted-42-100'}
        event={'repository':{'id':1337465220,'private':True,'fork':False}}
        assignment=p.validate_assignment(binding,{'id':100,'run_id':42,'status':'in_progress','conclusion':None,'name':'deep (portable-a)','runner_id':7,'runner_name':'hl-trusted-42-100'},{'id':7,'name':'hl-trusted-42-100','status':'online','busy':True})
        p.admit(event,env,binding,assignment)
        with self.assertRaises(ValueError):p.admit(event,env,dict(binding,jobId='999999'),assignment)
        for key in env:
            with self.subTest(key=key),self.assertRaises(ValueError):p.admit(event,dict(env,**{key:'wrong'}),binding,assignment)
        for key in binding:
            altered=dict(binding);del altered[key]
            with self.subTest(missing=key),self.assertRaises(ValueError):p.admit(event,env,altered,assignment)

if __name__=='__main__':unittest.main()
