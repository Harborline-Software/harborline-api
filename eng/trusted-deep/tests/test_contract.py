import copy
import datetime as dt
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import manifest as m
import evidence as e
from resource_fixture import fixture as resource_fixture


class Contract(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.value = {'resourceProfile':'zero-used-swap-v1','version':1,'repositoryId':1360432948,'owner':'ctwoodwa',
                      'sources':{name:digit*40 for name,digit in zip(('api','platform','quality','control'),'abcd')},
                      'tree':'e'*40,'base':'a'*40,'sdk':'11.0.100-rc.1.26425.128','image':'sha256:'+'f'*64,
                      'inputDigests':{name:'1'*64 for name in ('scripts','dependencyInputs','testSelection','coverageProfile','environment','baselines','testInventory')},
                      'tasks':[{'id':'deep-a','kind':'portable'},{'id':'deep-b','kind':'portable-coverage'}]}
    def load(self,value):
        path=self.root/'manifest.json';path.write_text(json.dumps(value))
        return m.load(path, m.digest(path))
    def test_manifest_requires_external_digest_and_full_pins(self):
        self.assertEqual(self.load(self.value), self.value)
        path=self.root/'manifest.json'
        with self.assertRaises(ValueError):m.load(path,'0'*64)
        for key in self.value:
            changed=copy.deepcopy(self.value);del changed[key]
            with self.subTest(missing=key),self.assertRaises(ValueError):self.load(changed)
        for pin in self.value['sources']:
            changed=copy.deepcopy(self.value);changed['sources'][pin]='main'
            with self.subTest(pin=pin),self.assertRaises(ValueError):self.load(changed)
        changed=copy.deepcopy(self.value);changed['tasks'][1]['kind']='mutation-benchmark'
        with self.assertRaises(ValueError):self.load(changed)
        changed=copy.deepcopy(self.value);changed['tasks'][0]['command']='curl attacker'
        with self.assertRaises(ValueError):self.load(changed)
    def test_public_or_untrusted_events_cannot_admit_deep_work(self):
        event={'repository':{'id':1337465220,'private':True,'fork':False}}
        env={'GITHUB_REPOSITORY_ID':'1337465220','GITHUB_REPOSITORY':'Harborline-Software/harborline-control',
             'GITHUB_REF':'refs/heads/main','GITHUB_WORKFLOW_SHA':'d'*40,'GITHUB_ACTOR_ID':'1328090','GITHUB_ACTOR':'ctwoodwa',
             'GITHUB_TRIGGERING_ACTOR':'ctwoodwa','GITHUB_RUN_ATTEMPT':'1','GITHUB_EVENT_NAME':'schedule'}
        m.admit_event(event,env,'d'*40)
        for name in ('pull_request','pull_request_target','workflow_run','push','merge_group'):
            with self.subTest(event=name),self.assertRaises(ValueError):m.admit_event(event,dict(env,GITHUB_EVENT_NAME=name),'d'*40)
        for key,bad in [('GITHUB_REPOSITORY_ID','1360432948'),('GITHUB_RUN_ATTEMPT','2'),('GITHUB_WORKFLOW_SHA','a'*40),('GITHUB_REF','refs/pull/382/merge')]:
            with self.subTest(key=key),self.assertRaises(ValueError):m.admit_event(event,dict(env,**{key:bad}),'d'*40)
        for change in ({'repository':{'id':1337465220,'private':False,'fork':False}},dict(event,inputs={'sha':'a'*40}),dict(event,workflow_run={})):
            with self.assertRaises(ValueError):m.admit_event(change,env,'d'*40)
    def test_classification_never_skips_hosted_critical_proof(self):
        for paths in ([],['docs/readme.md'],['.github/workflows/verify.yml'],['eng/platform-pin.json'],['mystery.bin'],['src/Thing.cs']):
            with self.subTest(paths=paths):self.assertEqual(m.classify(paths)['hostedCritical'],True);self.assertFalse(m.classify(paths)['deepEligible'])
        with self.assertRaises(ValueError):m.classify(['../escape'])
    def fixture(self):
        fingerprint=m.fingerprint(self.value,self.value['tasks'][0]);now=dt.datetime.now(dt.timezone.utc)
        for name in e.required_artifacts(fingerprint):
            path=self.root/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text('retained raw evidence')
        proof=resource_fixture(self.root,fingerprint)
        (self.root/'cleanup.json').write_text(json.dumps({'clean':True,'failures':[]}))

        (self.root/'out/immutable-completion.json').write_text(json.dumps({'verdict':'passed','head':'a'*40,'tree':'e'*40,'task':{'kind':'portable'},'resourceProfile':'zero-used-swap-v1'}))
        (self.root/'out/gc-preflight.json').write_text(json.dumps({'requestedEnv':'0x32','availableBytes':5368709120,'config':{'GCHeapHardLimit':5368709120,'GCHeapHardLimitPercent':50}}))
        receipt={'fingerprint':fingerprint,'status':'passed','cleanup':True,'oom':0,'swapMiB':0,
                 'resourceProof':proof,'completedAt':now.isoformat(),'artifacts':{name:m.digest(self.root/name) for name in e.required_artifacts(fingerprint)},
                 'suite':'full','criticalCheckSet':'api-required-v1'}
        return fingerprint,receipt,now
    def test_reuse_requires_same_approved_bytes_inputs_and_raw_artifacts(self):
        fingerprint,receipt,now=self.fixture()
        raw=json.dumps(receipt).encode();digest=hashlib.sha256(raw).hexdigest()
        self.assertEqual(e.equivalent(raw,fingerprint,self.root,digest,now),receipt)
        # Literal independent changes to every equivalence field must force remeasurement.
        for key in fingerprint:
            changed=dict(fingerprint);changed[key]='other'
            with self.subTest(key=key),self.assertRaises(ValueError):e.equivalent(raw,changed,self.root,digest,now)
        tampered=dict(receipt,status='failed')
        with self.assertRaises(ValueError):e.equivalent(json.dumps(tampered).encode(),fingerprint,self.root,digest,now)
        with self.assertRaises(ValueError):e.equivalent(raw,fingerprint,self.root,digest,now+dt.timedelta(days=4))
        (self.root/'out/gate-evidence/host-tests.trx').write_text('tampered')
        with self.assertRaises(ValueError):e.equivalent(raw,fingerprint,self.root,digest,now)
    def test_private_reuse_requires_hashed_bound_reclamation_and_assignment(self):
        fingerprint,receipt,now=self.fixture()
        binding={'runId':'42','jobId':'99','jobKey':'deep','workflowSha':'d'*40,
                 'workflowRef':'Harborline-Software/harborline-control/.github/workflows/api-trusted-deep.yml@refs/heads/main',
                 'taskId':'deep-a','manifestSha256':'2'*64}
        assignment={'binding':binding,'runnerId':7,'runnerName':'hl-trusted-42-99','verified':True}
        completion={'verdict':'passed','head':'a'*40,'tree':'e'*40,'task':{'id':'deep-a','kind':'portable'},'privateAssignment':assignment,'resourceProfile':'zero-used-swap-v1'}
        reclamation={'phase':'exact-clone-host-tests','binding':binding,'apiHead':'a'*40,'sdk':self.value['sdk'],
                     'uid':1001,'after':[],'command':['/usr/share/dotnet/dotnet','build-server','shutdown'],
                     'exitCode':0,'signalsSent':False,'elapsedSeconds':1}
        records={'session.json':{'session':'a'*32,'fingerprint':fingerprint,'privateBinding':binding,'manifestSha256':'2'*64},
                 'assignment.json':assignment,'out/immutable-completion.json':completion,
                 'out/private-build-server-reclamation.json':reclamation}
        def check(changed=None,omit=None):
            current=copy.deepcopy(records)
            if changed:changed(current)
            for name,value in current.items():(self.root/name).write_text(json.dumps(value))
            proof=copy.deepcopy(receipt)
            for name in current:proof['artifacts'][name]=m.digest(self.root/name)
            if omit:del proof['artifacts'][omit]
            raw=json.dumps(proof).encode()
            return e.equivalent(raw,fingerprint,self.root,hashlib.sha256(raw).hexdigest(),now)
        self.assertEqual(check()['status'],'passed')
        for name in ('assignment.json','out/private-build-server-reclamation.json'):
            with self.subTest(omitted=name),self.assertRaises(ValueError):check(omit=name)
        changes=[lambda r:r['out/private-build-server-reclamation.json'].update(after=[{'pid':10}]),
                 lambda r:r['out/private-build-server-reclamation.json'].update(apiHead='b'*40),
                 lambda r:r['assignment.json'].update(runnerName='another-runner'),
                 lambda r:r['out/immutable-completion.json'].update(privateAssignment=None),
                 lambda r:r['session.json']['privateBinding'].update(workflowSha='c'*40)]
        for i,change in enumerate(changes):
            with self.subTest(mismatch=i),self.assertRaises(ValueError):check(change)

    def test_release_checks_each_native_receipt_and_incident_state_itself(self):
        validator=patch.object(e,'validate_native_raw');validator.start();self.addCleanup(validator.stop)
        fingerprint,receipt,now=self.fixture();expected={};raw={};digests={};roots={}
        for platform in ('linux','windows','macos'):
            profile='portable-coverage' if platform=='linux' else 'native-full'
            expected[platform]=dict(fingerprint,os=platform,kind=profile)
            directory=self.root/platform;directory.mkdir()
            for name in e.required_artifacts(expected[platform]):
                path=directory/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text('retained raw proof')
            if platform=='linux':
                resource_proof=resource_fixture(directory,expected[platform])
                (directory/'cleanup.json').write_text(json.dumps({'clean':True,'failures':[]}))

                (directory/'out/immutable-completion.json').write_text(json.dumps({'verdict':'passed','head':'a'*40,'tree':'e'*40,'task':{'kind':profile},'resourceProfile':'zero-used-swap-v1'}))
                (directory/'out/gc-preflight.json').write_text(json.dumps({'requestedEnv':'0x32','availableBytes':5368709120,'config':{'GCHeapHardLimit':5368709120,'GCHeapHardLimitPercent':50}}))
            else:
                (directory/'out/gate-evidence/native-context.json').write_text(json.dumps({'sha':'a'*40,'platform':platform,'architecture':'arm64'}))
            record=dict(receipt,fingerprint=expected[platform],artifacts={name:m.digest(directory/name) for name in e.required_artifacts(expected[platform])})
            if platform=='linux':record['resourceProof']=resource_proof
            raw[platform]=json.dumps(record).encode();digests[platform]=hashlib.sha256(raw[platform]).hexdigest();roots[platform]=directory
        with patch.object(e,'validate_native_raw') as validate:
            self.assertTrue(e.release_ready('a'*40,expected,raw,roots,digests,[],now))
            self.assertEqual(validate.call_count,2)
        for platform in raw:
            missing=dict(raw);del missing[platform]
            with self.subTest(missing=platform),self.assertRaises(ValueError):e.release_ready('a'*40,expected,missing,roots,digests,[],now)
        with self.assertRaises(ValueError):e.release_ready('a'*40,expected,raw,roots,digests,['unresolved'],now)
        with self.assertRaises(ValueError):e.release_ready('b'*40,expected,raw,roots,digests,[],now)
        for key in ('control','platform','quality','sdk','tree','base','scripts','dependencyInputs','baselines','testInventory','testSelection','coverageProfile'):
            changed=copy.deepcopy(expected);changed['windows'][key]='other'
            with self.subTest(parity=key),self.assertRaises(ValueError):e.release_ready('a'*40,changed,raw,roots,digests,[],now)
        forged=dict(raw);forged['windows']=b'{"validated":true,"status":"passed","suite":"full"}'
        with self.assertRaises(ValueError):e.release_ready('a'*40,expected,forged,roots,digests,[],now)

if __name__=='__main__':unittest.main()
