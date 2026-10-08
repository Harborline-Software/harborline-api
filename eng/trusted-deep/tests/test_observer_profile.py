"""Execute the actual observer against a literal mocked OS/Docker corpus; no payload."""
import json
from pathlib import Path
import runpy
import sys
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))

class ObserverProfile(unittest.TestCase):
    def observe(self,change=None):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);path=root/'session.json';out=root/'resources';session='a'*32
            path.write_text(json.dumps({'session':session,'fingerprint':{'resourceProfile':'operational-stable-swap-v1'}}))
            clock=[1000.];n=[0];vmreads=[0];calls=[]
            def read(argv,**kwargs):
                calls.append(argv)
                if argv[0]=='/usr/bin/memory_pressure':value='System-wide memory free percentage: 79%'
                elif argv[0]=='/usr/sbin/sysctl':value='total = 100.00M used = 15.00M free = 85.00M' if argv[-1]=='vm.swapusage' else '1'
                elif argv[0]=='/usr/bin/vm_stat':
                    vmreads[0]+=1
                    if change=='session' and vmreads[0]==11:path.write_text(json.dumps({'session':'b'*32,'fingerprint':{'resourceProfile':'operational-stable-swap-v1'}}))
                    writes=961 if change=='writes' and n[0]>=2 else 960
                    value=f'Pageins: {1000+n[0]}.\nSwapins: {12+n[0]}.\nSwapouts: {writes}.\n'
                elif argv[:2]==['docker','ps']:value='hl-mini-'+session+'-a\n'
                elif argv[:2]==['docker','inspect']:value=json.dumps({'limit':10737418240,'state':{'Running':True,'OOMKilled':False}})
                elif argv[:2]==['docker','exec']:
                    n[0]+=1
                    value=json.dumps({'memorySwapCurrent':1 if change=='container' else 0,'memorySwapMax':'0','memoryMax':'10737418240',
                      'cpuMax':'500000 100000','memoryEvents':{'oom':0,'oom_kill':0},'memoryPeak':123456,'vmMemAvailable':3221225472})
                elif argv[:2]==['docker','stats']:
                    value='{}'
                    if n[0]>=2:(root/'result.json').write_text('{}')
                else:raise AssertionError('Unexpected OS/payload command: '+str(argv))
                return SimpleNamespace(returncode=0,stdout=value)
            def sleep(value):clock[0]+=value
            observer=Path(__file__).resolve().parents[1]/'observer.py'
            argv=['observer.py','--session-file',str(path),'--output',str(out),'--resource-profile','operational-stable-swap-v1']
            with patch.object(sys,'argv',argv),patch('subprocess.run',side_effect=read),patch.object(time,'time',side_effect=lambda:clock[0]),patch.object(time,'monotonic',side_effect=lambda:clock[0]),patch.object(time,'sleep',side_effect=sleep),patch('builtins.print'):
                if change:
                    with self.assertRaises(SystemExit) as refused:runpy.run_path(str(observer),run_name='__main__')
                    self.assertEqual(refused.exception.code,1)
                else:runpy.run_path(str(observer),run_name='__main__')
            return json.loads((out/'summary.json').read_text()),calls
    def test_real_observer_records_operational_reads_without_zero_claim(self):
        summary,_=self.observe()
        self.assertIsNone(summary['alarm']);self.assertEqual(summary['profile'],'operational-stable-swap-v1')
        self.assertEqual(summary['initialHostSwapMiB'],15);self.assertEqual(summary['maxHostSwapMiB'],15)
        self.assertEqual(summary['swapoutsDelta'],0);self.assertEqual(summary['swapinsDelta'],2);self.assertEqual(summary['pageinsDelta'],2)
    def test_changed_first_record_refuses_before_any_docker_observation(self):
        summary,calls=self.observe('session')
        self.assertEqual(summary['session'],'a'*32);self.assertEqual(summary['alarm'],'Session identity changed')
        self.assertFalse(any(call[0]=='docker' for call in calls))
    def test_actual_observer_refuses_new_writes_and_container_swap(self):
        for change in ('writes','container'):
            with self.subTest(change=change):
                summary,_=self.observe(change)
                self.assertIsNotNone(summary['alarm'])

if __name__=='__main__':unittest.main()
