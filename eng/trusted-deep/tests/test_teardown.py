import errno
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch,Mock
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import pilot

class Teardown(unittest.TestCase):
    def test_resource_admission_requires_valid_explicit_ready_before_start(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'resources').mkdir();observer=Mock();observer.poll.return_value=None
            ready=root/'resources/ready.json'
            def slow_baseline(_):
                ready.write_text(json.dumps({'session':'approved','initialHost':{'swapUsedMiB':0,'freePercent':30,'pressureLevel':1}}))
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
