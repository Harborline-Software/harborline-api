import errno
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch,Mock
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import pilot
import time
from resource_fixture import fixture as resource_fixture

class Teardown(unittest.TestCase):
    def test_real_docker_filter_grammar_and_all_resource_absence_checks(self):
        removed=set();calls=[]
        def engine(*args):
            calls.append(args)
            if '--filter' in args:
                self.assertEqual(args[args.index('--filter')+1],'label=org.harborline.mini.session=approved')
                kind='container' if args[0]=='ps' else args[0]
                return '' if kind in removed else 'owned-name\n'
            kind='container' if args[0]=='rm' else args[0];removed.add(kind);return ''
        with patch.object(pilot,'docker',side_effect=engine):
            self.assertEqual(pilot.cleanup('approved','owned-name'),{'session':'approved','clean':True,'failures':[]})
        self.assertEqual(removed,{'container','network','volume'});self.assertEqual(len(calls),9)

    def test_resource_admission_requires_valid_explicit_ready_before_start(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'resources').mkdir();observer=Mock();observer.poll.return_value=None
            ready=root/'resources/ready.json'
            def slow_baseline(_):
                resource_fixture(root,{'resourceProfile':'zero-used-swap-v1'})
                value=json.loads((root/'resources/baseline.json').read_text());value['session']='approved';value['hostSamples'][-1]['time']=time.time();(root/'resources/baseline.json').write_text(json.dumps(value))
                ready.write_text(json.dumps({'session':'approved','profile':'zero-used-swap-v1','baselineSha256':pilot.digest(root/'resources/baseline.json'),'initialHost':value['hostSamples'][-1]}))
            with patch.object(pilot.time,'sleep',side_effect=slow_baseline) as wait:
                pilot.await_resource_admission(observer,root,'approved')
                self.assertEqual(wait.call_count,1)
            with self.assertRaises(ValueError):pilot.await_resource_admission(observer,root,'other')
            ready.unlink();observer.poll.return_value=1
            with self.assertRaises(ValueError):pilot.await_resource_admission(observer,root,'approved')

    def test_enospc_cannot_skip_either_drain_and_returns_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            child=Mock();observer=Mock()
            with patch.object(pilot,'cleanup',return_value={'clean':True,'failures':[]}),patch.object(pilot,'stop_and_wait') as drain,patch.object(pilot,'write',side_effect=OSError(errno.ENOSPC,'Full')):
                clean,errors=pilot.teardown('session','name',Path(tmp),child,observer,Path(tmp)/'claim',False)
            self.assertEqual([c.args[0] for c in drain.call_args_list],[child,observer]);self.assertEqual(len(errors),1);self.assertTrue(clean['clean'])
    def test_cleanup_and_first_drain_failure_cannot_skip_second_drain(self):
        with tempfile.TemporaryDirectory() as tmp:
            child=Mock();observer=Mock()
            with patch.object(pilot,'cleanup',side_effect=RuntimeError('cleanup error')),patch.object(pilot,'stop_and_wait',side_effect=[RuntimeError('child error'),None]) as drain:
                clean,errors=pilot.teardown('session','name',Path(tmp),child,observer,Path(tmp)/'claim',False)
            self.assertEqual(drain.call_count,2);self.assertEqual(len(errors),2);self.assertFalse(clean['clean'])

if __name__=='__main__':unittest.main()
