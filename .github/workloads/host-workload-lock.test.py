import os, subprocess, sys, tempfile, unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name('host-workload-lock.py')
class LockTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.env={**os.environ,'HARBORLINE_HOST_LOCK_DIR':self.temp.name,'HARBORLINE_HOST_LOCK_WAIT_SECONDS':'2'}
    def tearDown(self): self.temp.cleanup()
    def launch(self, code, env=None):
        return subprocess.Popen([sys.executable,str(SCRIPT),'--',sys.executable,'-c',code],env=env or self.env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    def test_cross_repository_commands_serialize(self):
        marker=Path(self.temp.name)/'active'
        first=self.launch(f'import pathlib,time; p=pathlib.Path({str(marker)!r}); p.write_text("active"); print("started",flush=True); time.sleep(.3); p.unlink()')
        self.assertEqual(first.stdout.readline().strip(),'started')
        second=self.launch(f'import pathlib; assert not pathlib.Path({str(marker)!r}).exists(); print("second")')
        out,err=second.communicate(timeout=5)
        self.assertEqual(second.returncode,0,err)
        self.assertIn('Waiting:',err)
        self.assertEqual(out.strip(),'second')
        first.communicate(timeout=5)
        self.assertEqual(first.returncode,0)
    def test_timeout_does_not_execute_command(self):
        first=self.launch('import time; print("started",flush=True); time.sleep(.3)')
        first.stdout.readline()
        second=self.launch('print("MUST NOT RUN")',{**self.env,'HARBORLINE_HOST_LOCK_WAIT_SECONDS':'0'})
        out,err=second.communicate(timeout=5)
        self.assertNotEqual(second.returncode,0)
        self.assertNotIn('MUST NOT RUN',out)
        self.assertIn('command did not start',err)
        first.communicate(timeout=5)
    def test_exit_code_and_release(self):
        first=self.launch('raise SystemExit(7)')
        first.communicate(timeout=5)
        self.assertEqual(first.returncode,7)
        second=self.launch('print("released")')
        out,err=second.communicate(timeout=5)
        self.assertEqual(second.returncode,0,err)
        self.assertIn('released',out)
if __name__=='__main__': unittest.main()
