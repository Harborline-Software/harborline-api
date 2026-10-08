"""Literal negative corpus based on operator-approved operational admission rules."""
import copy
import datetime
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import Mock
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import resource_profile as rp
import pilot
import evidence
import manifest
from resource_fixture import fixture
import test_contract

class ResourceProfile(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.mode='operational-stable-swap-v1';self.session='a'*32
        self.proof=fixture(self.root,{'resourceProfile':self.mode},15)
        self.base=json.loads((self.root/'resources/baseline.json').read_text())
        self.rows=[json.loads(l) for l in (self.root/'resources/telemetry.jsonl').read_text().splitlines()]
    def test_stable_preexisting_swap_is_operational_only(self):
        proof=rp.validate(self.root,self.mode,self.session)
        self.assertEqual(proof['initialHostSwapMiB'],15)
        self.assertEqual(proof['maxHostSwapGrowthMiB'],0)
        self.assertEqual(proof['swapoutsDelta'],0)
        with self.assertRaises(ValueError):rp.validate(self.root,'zero-used-swap-v1',self.session)
    def test_swapins_and_generic_pageins_are_telemetry(self):
        self.rows[-1]['host'].update(swapins=13,pageins=1100)
        measured=rp.measures(self.base,self.rows,self.mode,self.session)
        self.assertEqual(measured['swapinsDelta'],1)
        self.assertEqual(measured['pageinsDelta'],100)
        self.assertEqual(measured['swapoutsDelta'],0)
    def test_new_swap_growth_writes_and_resets_refuse(self):
        for key,value in [('swapUsedMiB',15.1),('swapouts',961),('swapins',11),('swapouts',959),('pageins',999),('time',0)]:
            rows=copy.deepcopy(self.rows);rows[-1]['host'][key]=value
            with self.subTest(key=key,value=value),self.assertRaises(ValueError):rp.measures(self.base,rows,self.mode,self.session)
    def test_rebound_below_original_baseline_is_still_new_growth(self):
        rows=copy.deepcopy(self.rows);rows[0]['host']['swapUsedMiB']=14;rows[-1]['host']['swapUsedMiB']=14.1
        with self.assertRaises(ValueError):rp.measures(self.base,rows,self.mode,self.session)
    def test_oom_container_swap_missing_counters_and_limits_refuse(self):
        for key,value in [('memorySwapCurrent',1),('memorySwapMax','max'),('memoryMax','1'),('cpuMax','max 100000'),('memoryEvents',{'oom':1,'oom_kill':0}),('memoryEvents',{'oom':0}),('vmMemAvailable',None),('memoryPeak',-1)]:
            rows=copy.deepcopy(self.rows);rows[-1][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):rp.measures(self.base,rows,self.mode,self.session)
        for key in ('swapins','swapouts','pageins'):
            rows=copy.deepcopy(self.rows);del rows[-1]['host'][key]
            with self.subTest(missing=key),self.assertRaises(ValueError):rp.measures(self.base,rows,self.mode,self.session)
    def test_nonfinite_or_malformed_host_refuses(self):
        for key,value in [('swapUsedMiB',float('nan')),('time',float('inf')),('freePercent',101),('pressureLevel',True),('swapouts',-1)]:
            rows=copy.deepcopy(self.rows);rows[-1]['host'][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):rp.measures(self.base,rows,self.mode,self.session)
    def test_existing_two_sample_pressure_guard_remains(self):
        for field,value in [('pressureLevel',2),('freePercent',19)]:
            rows=copy.deepcopy(self.rows)
            for row in rows:row['host'][field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):rp.measures(self.base,rows,self.mode,self.session)
        rows=copy.deepcopy(self.rows)
        for row in rows:row['vmMemAvailable']=2147483647
        with self.assertRaises(ValueError):rp.measures(self.base,rows,self.mode,self.session)
    def test_unstable_short_gapped_copied_or_pressure_baseline_refuses(self):
        changes=[lambda b:b['hostSamples'][-1].update(swapUsedMiB=16),lambda b:b['hostSamples'][-1].update(swapouts=961),
                 lambda b:b['hostSamples'][-1].update(pressureLevel=2),lambda b:b['hostSamples'][0].update(freePercent=29),
                 lambda b:b.update(hostSamples=b['hostSamples'][:2]),lambda b:b['hostSamples'].pop(1),
                 lambda b:b.update(session='b'*32),lambda b:b.update(profile='unknown')]
        for i,change in enumerate(changes):
            base=copy.deepcopy(self.base);change(base)
            with self.subTest(change=i),self.assertRaises(ValueError):rp.baseline(base,self.mode,self.session)
    def test_admission_rejects_stale_changed_profile_digest_and_session(self):
        observer=Mock();observer.poll.return_value=None
        # Fixture completed ten seconds ago, inside the explicit fifteen-second admission limit.
        pilot.await_resource_admission(observer,self.root,self.session,self.mode)
        path=self.root/'resources/ready.json';original=json.loads(path.read_text())
        for key,value in [('profile','zero-used-swap-v1'),('baselineSha256','0'*64),('session','b'*32),('initialHost',{})]:
            path.write_text(json.dumps(dict(original,**{key:value})))
            with self.subTest(key=key),self.assertRaises(ValueError):pilot.await_resource_admission(observer,self.root,self.session,self.mode)
        path.write_text(json.dumps(original))
        from unittest.mock import patch
        with patch.object(pilot.time,'time',return_value=original['initialHost']['time']+16),self.assertRaises(ValueError):pilot.await_resource_admission(observer,self.root,self.session,self.mode)
    def test_completion_rederives_raw_proof_not_claimed_summary(self):
        for filename,mutate in [('summary.json',lambda v:v.update(maxHostSwapMiB=0)),('admission.json',lambda v:v.update(admittedAt=v['initialHost']['time']+16)),
                                ('baseline.json',lambda v:v.update(session='b'*32))]:
            p=self.root/'resources'/filename;original=p.read_bytes();v=json.loads(original);mutate(v);p.write_text(json.dumps(v))
            with self.subTest(file=filename),self.assertRaises(ValueError):rp.validate(self.root,self.mode,self.session)
            p.write_bytes(original)
        self.rows[-1]['host']['swapouts']=961
        (self.root/'resources/telemetry.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in self.rows))
        with self.assertRaises(ValueError):rp.validate(self.root,self.mode,self.session)
    def test_benchmark_zero_mode_and_final_absolute_zero_remain(self):
        fingerprint={'resourceProfile':'zero-used-swap-v1'};fixture(self.root,fingerprint,0)
        self.assertEqual(rp.validate(self.root,'zero-used-swap-v1',self.session)['maxHostSwapMiB'],0)
        base=json.loads((self.root/'resources/baseline.json').read_text());rows=[json.loads(l) for l in (self.root/'resources/telemetry.jsonl').read_text().splitlines()]
        rows[-1]['host']['swapUsedMiB']=1
        with self.assertRaises(ValueError):rp.measures(base,rows,'zero-used-swap-v1',self.session)
    def test_vm_counter_parser_distinguishes_swap_writes(self):
        self.assertEqual(rp.counters('Pageins: 1000.\nSwapins: 12.\nSwapouts: 960.\n'),{'pageins':1000,'swapins':12,'swapouts':960})
        for text in ('Pageins: 1000.\nSwapins: 12.\n','Pageins: 1000.\nSwapins: 12.\nSwapouts: 960.\nSwapouts: 960.\n'):
            with self.assertRaises(ValueError):rp.counters(text)

class OperationalReuse(unittest.TestCase):
    setUp=test_contract.Contract.setUp
    fixture=test_contract.Contract.fixture
    load=test_contract.Contract.load
    def test_operational_actual_swap_and_full_baseline_are_bound(self):
        self.value['resourceProfile']='operational-stable-swap-v1'
        fp,receipt,now=self.fixture();receipt['resourceProof']=fixture(self.root,fp,15);receipt['swapMiB']=15
        completion=json.loads((self.root/'out/immutable-completion.json').read_text());completion['resourceProfile']='operational-stable-swap-v1';(self.root/'out/immutable-completion.json').write_text(json.dumps(completion))
        def check(record,expected=fp):
            record=copy.deepcopy(record);record['artifacts']={name:manifest.digest(self.root/name) for name in record['artifacts']}
            raw=json.dumps(record).encode();return evidence.equivalent(raw,expected,self.root,hashlib.sha256(raw).hexdigest(),now)
        self.assertEqual(check(receipt)['swapMiB'],15)
        for mutate in [lambda r:r.update(swapMiB=0),lambda r:r['resourceProof'].update(baselineSha256='0'*64),lambda r:r.pop('resourceProof'),lambda r:r['resourceProof']['baseline'].update(session='b'*32)]:
            changed=copy.deepcopy(receipt);mutate(changed)
            with self.assertRaises(ValueError):check(changed)
        other=dict(fp,resourceProfile='zero-used-swap-v1');forged=copy.deepcopy(receipt);forged['fingerprint']=other
        with self.assertRaises(ValueError):check(forged,other)
        expected={p:dict(fp,os=p,kind='portable-coverage' if p=='linux' else 'native-full') for p in ('linux','windows','macos')}
        with self.assertRaisesRegex(ValueError,'Operational nightly proof'):evidence.release_ready(fp['api'],expected,{p:b'' for p in expected},{p:self.root for p in expected},{p:'0'*64 for p in expected},[],now)
    def test_operational_mutation_reuse_refuses_even_externally_indexed_claim(self):
        self.value['resourceProfile']='operational-stable-swap-v1'
        fp,receipt,now=self.fixture();fp['kind']='mutation-benchmark';receipt['fingerprint']=fp
        raw=json.dumps(receipt).encode()
        with self.assertRaisesRegex(ValueError,'Native/benchmark resource profile differs'):
            evidence.equivalent(raw,fp,self.root,hashlib.sha256(raw).hexdigest(),now)
    def test_unknown_profile_and_operational_mutation_manifest_refuse(self):
        for mode in ('unknown',None):
            value=copy.deepcopy(self.value);value['resourceProfile']=mode
            with self.assertRaises(ValueError):self.load(value)
        value=copy.deepcopy(self.value);value.update(resourceProfile='operational-stable-swap-v1',tasks=[{'id':'mutation','kind':'mutation-benchmark'}])
        with self.assertRaises(ValueError):self.load(value)

if __name__=='__main__':unittest.main()
