"""Independent two-test TRX/Vitest corpus and literal baseline acceptance/refusal oracles."""
import datetime as dt
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import evidence as e
import manifest as m

# Native raw-evidence contract: a literal inventory independent of its producer.
NATIVE_ARTIFACTS = {
    'out/gate-evidence/native-context.json', 'hosted-job.json',
    'out/harborline-api-verify-receipt.json', 'out/gate-evidence/host-tests.trx',
    'out/gate-evidence/named-test-outcomes.json', 'out/gate-evidence/capability-tests.json',
    'out/gate-evidence/exact-clone-report.json', 'out/raw-validation.json',
}

class NativeProof(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.source=self.root/'source';self.source.mkdir()
        self.git('init','-q');self.git('config','user.name','Fixture');self.git('config','user.email','fixture@example.invalid')
        baseline=self.source/'eng/baselines';baseline.mkdir(parents=True)
        host={'totals':{'total':2},'knownTests':['host pass','host allowed failure'],'permittedFailures':[{'test':'host allowed failure'}]}
        cap={'totals':{'total':2},'knownTests':['cap.test.ts :: pass','cap.test.ts :: skipped'],'policyRemovals':[{'test':'cap.test.ts :: skipped'}]}
        for name,value in [('host-test-baseline.json',host),('host-test-baseline.macos.json',host),('hull-test-baseline.json',cap),('hull-test-baseline.macos.json',cap)]:
            (baseline/name).write_text(json.dumps(value))
        self.git('add','.');self.git('commit','-qm','Independent literal raw-report fixture')
        self.sha=self.git('rev-parse','HEAD');self.tree=self.git('rev-parse','HEAD^{tree}')
        self.now=dt.datetime.now(dt.timezone.utc)
    def git(self,*args):return subprocess.check_output(['git','-C',str(self.source),*args],text=True).strip()
    def fixture(self,platform):
        root=self.root/platform;gate=root/'out/gate-evidence';gate.mkdir(parents=True)
        fingerprint={key:'1'*64 for key in m.FINGERPRINT_KEYS}
        fingerprint.update(api=self.sha,tree=self.tree,base=self.sha,platform='b'*40,quality='c'*40,control='d'*40,image='sha256:'+'f'*64,
                           resourceProfile='zero-used-swap-v1',sdk='11.0.100',os=platform,architecture='arm64',kind='native-full')
        source={k:fingerprint[k] for k in ('api','platform','quality','control')};inputs={k:fingerprint[k] for k in ('scripts','dependencyInputs','testSelection','coverageProfile','environment','baselines','testInventory')}
        context={'sources':source,'inputDigests':inputs,'sha':self.sha,'tree':self.tree,'base':self.sha,'sdk':'11.0.100','image':fingerprint['image'],
                 'platform':platform,'architecture':'arm64','runId':'42','attempt':'1','jobKey':'verify-windows-hosted' if platform=='windows' else 'verify-macos',
                 'workflowSha':self.sha,'repositoryId':'1360432948','event':'workflow_dispatch'}
        (gate/'native-context.json').write_text(json.dumps(context));(root/'hosted-job.json').write_text('{"runId":42,"jobId":100}')
        (gate/'host-tests.trx').write_text('<TestRun><Results><UnitTestResult testName="host pass" outcome="Passed"/><UnitTestResult testName="host allowed failure" outcome="Failed"/></Results><ResultSummary><Counters total="2" passed="1" failed="1" notExecuted="0"/></ResultSummary></TestRun>')
        counts={'total':2,'passed':1,'failed':1,'notExecuted':0};capcounts={'total':2,'passed':1,'failed':0,'notExecuted':1}
        host={'counts':counts,'problems':[],'results':[{'testName':'host pass','rosterId':'host pass','outcome':'Passed'},{'testName':'host allowed failure','rosterId':'host allowed failure','outcome':'Failed'}]}
        capability={'counts':capcounts,'problems':[],'results':[{'testName':'cap.test.ts :: pass','rosterId':'cap.test.ts :: pass','outcome':'Passed'},{'testName':'cap.test.ts :: skipped','rosterId':'cap.test.ts :: skipped','outcome':'NotExecuted'}]}
        hostbase='eng/baselines/host-test-baseline'+('.macos' if platform=='macos' else '')+'.json'
        capbase='eng/baselines/hull-test-baseline.macos.json' if platform=='macos' else 'eng/baselines/hull-test-baseline.json'
        named={'apiCommit':self.sha,'runtime':{'platform':'win32' if platform=='windows' else 'darwin','architecture':'arm64'},'runId':'42','attempt':'1',
               'hostBaseline':hostbase,'capabilityBaseline':capbase,'evidenceRoots':{'clone':'/fixture/clone','scratch':'/fixture'},'host':host,'capability':capability}
        (gate/'named-test-outcomes.json').write_text(json.dumps(named))
        capraw={'numTotalTests':2,'numPassedTests':1,'numFailedTests':0,'numPendingTests':1,'numTodoTests':0,'testResults':[{'name':'/fixture/clone/cap.test.ts','assertionResults':[{'title':'pass','status':'passed'},{'title':'skipped','status':'pending'}]}]}
        (gate/'capability-tests.json').write_text(json.dumps(capraw))
        provenance={}
        for key,name in [('host',hostbase),('capability',capbase)]:
            blob=self.git('rev-parse',self.sha+':'+name);provenance[key]={'committedBlob':blob,'workingBlob':blob,'matchesCommitted':True}
        report={'status':'PASS','apiCommit':self.sha,'repository':'harborline-api','gate':'destination-exact-clone','baselineProvenance':provenance,
                'steps':[{'id':'host-baseline-match','passed':True,'rescuedByRetry':0},{'id':'capability-baseline-match','passed':True},
                         {'id':'host-flake-retry','passed':True,'retries':[],'rescued':0,'unexpectedFailures':[],'retriedUnderKnownFlaky':[]}]}
        (gate/'exact-clone-report.json').write_text(json.dumps(report))
        (root/'out/raw-validation.json').write_text(json.dumps({'host':counts,'skippedResultRows':0,'capability':capcounts}))
        (root/'out/harborline-api-verify-receipt.json').write_text(json.dumps({'schemaVersion':1,'repository':'harborline-api','lane':'host','baseHead':self.sha,'testedTree':self.tree,'hostBaseline':hostbase,'steps':['exact-clone']}))
        return root,fingerprint
    def receipt(self,root,fingerprint):
        receipt={'fingerprint':fingerprint,'status':'passed','cleanup':True,'oom':0,'swapMiB':0,'completedAt':self.now.isoformat(),'suite':'full','criticalCheckSet':'api-required-v1',
                 'artifacts':{name:m.digest(root/name) for name in NATIVE_ARTIFACTS}}
        raw=json.dumps(receipt).encode();return raw,hashlib.sha256(raw).hexdigest()
    def test_native_raw_and_measured_context_are_revalidated(self):
        for platform in ('windows','macos'):
            root,expected=self.fixture(platform);raw,digest=self.receipt(root,expected)
            with patch.object(e,'ROOT',self.source):self.assertEqual(e.equivalent(raw,expected,root,digest,self.now)['status'],'passed')
            for name in ['host-tests.trx','capability-tests.json','named-test-outcomes.json','exact-clone-report.json']:
                p=root/'out/gate-evidence'/name;before=p.read_bytes();p.write_text('not a report');raw,digest=self.receipt(root,expected)
                with patch.object(e,'ROOT',self.source),self.subTest(platform=platform,raw=name),self.assertRaises((ValueError,KeyError,json.JSONDecodeError)):e.equivalent(raw,expected,root,digest,self.now)
                p.write_bytes(before)
            p=root/'out/gate-evidence/native-context.json';context=json.loads(p.read_text())
            for key in ('sha','sdk','workflowSha','jobKey','repositoryId','architecture','tree','image'):
                p.write_text(json.dumps(dict(context,**{key:'wrong'})));raw,digest=self.receipt(root,expected)
                with self.subTest(platform=platform,key=key),self.assertRaises(ValueError):e.equivalent(raw,expected,root,digest,self.now)
            p.write_text(json.dumps(context))
    def test_missing_native_raw_artifact_and_wrong_hosted_job_fail(self):
        root,expected=self.fixture('windows');raw,digest=self.receipt(root,expected)
        self.assertEqual(e.required_artifacts(expected),NATIVE_ARTIFACTS)
        for name in NATIVE_ARTIFACTS:
            record=json.loads(raw);del record['artifacts'][name];missing=json.dumps(record).encode()
            with self.subTest(missing=name),self.assertRaises(ValueError):e.equivalent(missing,expected,root,hashlib.sha256(missing).hexdigest(),self.now)
        (root/'hosted-job.json').write_text('{"runId":43,"jobId":100}');raw,digest=self.receipt(root,expected)
        with self.assertRaises(ValueError):e.equivalent(raw,expected,root,digest,self.now)

if __name__=='__main__':unittest.main()
