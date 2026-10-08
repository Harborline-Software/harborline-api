"""Execute the actual observer against a literal mocked OS/Docker corpus; no payload."""
import json
from pathlib import Path
import runpy
import subprocess
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
            clock=[1000.];n=[0];vmreads=[0];calls=[];inspects=[0]
            def read(argv,**kwargs):
                calls.append(argv)
                if argv[0]=='/usr/bin/memory_pressure':value='System-wide memory free percentage: 79%'
                elif argv[0]=='/usr/sbin/sysctl':value='total = 100.00M used = 15.00M free = 85.00M' if argv[-1]=='vm.swapusage' else '1'
                elif argv[0]=='/usr/bin/vm_stat':
                    vmreads[0]+=1
                    if change=='session' and vmreads[0]==11:path.write_text(json.dumps({'session':'b'*32,'fingerprint':{'resourceProfile':'operational-stable-swap-v1'}}))
                    writes=961 if change=='writes' and n[0]>=2 else 960
                    value=f'Pageins: {1000+n[0]}.\nSwapins: {12+n[0]}.\nSwapouts: {writes}.\n'
                elif argv[:2]==['docker','ps']:
                    if change=='command-timeout':
                        clock[0]+=15
                        raise subprocess.TimeoutExpired('secret command',15,output=b'secret output',stderr=b'context deadline exceeded')
                    value='hl-mini-'+session+'-a\n'
                elif argv[:2]==['docker','inspect']:
                    inspects[0]+=1
                    if change=='inspect-timeout' and inspects[0]==2:
                        clock[0]+=15
                        raise subprocess.TimeoutExpired('secret inspect argv',15,output=b'unmarked secret',stderr=b'context deadline exceeded')
                    created=(change=='inspect-timeout' and inspects[0]==1) or (change=='created-running' and inspects[0]<=20)
                    value=json.dumps({'limit':10737418240,'state':{'Running':not created,'Status':'created' if created else 'running','OOMKilled':False}})
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
            class Process:
                pid=27182
                returncode=None
                def __init__(self,argv,**kwargs):self.argv=argv
                def __enter__(self):return self
                def __exit__(self,*args):return False
                def communicate(self,timeout):
                    result=read(self.argv);self.returncode=result.returncode
                    return result.stdout,''
                def kill(self):self.returncode=-9
                def wait(self):return self.returncode
            observer=Path(__file__).resolve().parents[1]/'observer.py'
            argv=['observer.py','--session-file',str(path),'--output',str(out),'--resource-profile','operational-stable-swap-v1']
            with patch.object(sys,'argv',argv),patch('observer_commands.subprocess.Popen',side_effect=Process),patch.object(time,'time',side_effect=lambda:clock[0]),patch.object(time,'monotonic',side_effect=lambda:clock[0]),patch.object(time,'sleep',side_effect=sleep),patch('builtins.print'):
                if change and change!='created-running':
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

    def test_actual_observer_retains_command_timeout_without_retry_or_secret_text(self):
        summary,calls=self.observe('command-timeout')
        self.assertEqual(summary['alarm'],'TimeoutExpired')
        self.assertEqual(summary['samples'],0)
        failure=summary['commandFailure']
        self.assertEqual((failure['operation'],failure['observerPhase']),('docker.session-list','container-discovery'))
        self.assertEqual((failure['timeoutSeconds'],failure['elapsedSeconds'],failure['childPid']),(15,15,27182))
        self.assertEqual(failure['stderr']['text'],'context deadline exceeded')
        self.assertNotIn('secret',json.dumps(failure))
        self.assertEqual(sum(argv[:2]==['docker','ps'] for argv in calls),1)
        self.assertFalse(any(argv[:2]==['docker','exec'] for argv in calls))

    def test_created_status_then_inspect_timeout_retains_exact_failure_without_exec(self):
        summary,calls=self.observe('inspect-timeout')
        self.assertEqual(summary['alarm'],'TimeoutExpired');self.assertEqual(summary['samples'],0)
        f=summary['commandFailure']
        self.assertEqual((f['operation'],f['observerPhase'],f['lastContainerStatus']),('docker.session-inspect','container-status','created'))
        self.assertEqual((f['timeoutSeconds'],f['elapsedSeconds']),(15,15))
        self.assertEqual(sum(a[:2]==['docker','inspect'] for a in calls),2)
        self.assertFalse(any(a[:2]==['docker','exec'] for a in calls))
        self.assertNotIn('secret',json.dumps(f))
    def test_created_wait_does_not_execute_telemetry_until_running(self):
        summary,calls=self.observe('created-running')
        self.assertIsNone(summary['alarm']);self.assertEqual(summary['samples'],2)
        first_exec=next(i for i,a in enumerate(calls) if a[:2]==['docker','exec'])
        self.assertEqual(sum(a[:2]==['docker','inspect'] for a in calls[:first_exec]),21)
        self.assertEqual(sum(a[:2]==['docker','exec'] for a in calls),2)

if __name__=='__main__':unittest.main()
