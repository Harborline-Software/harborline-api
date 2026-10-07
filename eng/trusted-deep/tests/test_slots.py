import fcntl
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import slots
import pilot

MODULE=Path(__file__).resolve().parents[1]
WORKER='''import sys,json
sys.path.insert(0,sys.argv[1])
import slots
try:
 with slots.lease(sys.argv[2],exclusive=sys.argv[3]=='exclusive') as record:
  slots.inherited(record,sys.argv[2])
  print(json.dumps({'slot':record['slot']}),flush=True)
  sys.stdin.readline()
except BlockingIOError:
 print('REFUSED',flush=True);raise SystemExit(3)
'''

class MachineSlots(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.canonical=self.root/'heavy.lock';self.canonical.write_text('canonical owner journal')
        self.inode=self.canonical.stat().st_ino
    def worker(self,exclusive=False):
        child=subprocess.Popen([sys.executable,'-B','-c',WORKER,str(MODULE),str(self.root),'exclusive' if exclusive else 'shared'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        self.addCleanup(self.stop,child)
        return child
    @staticmethod
    def stop(child):
        if child.poll() is None:
            child.stdin.write('release\n');child.stdin.flush();child.wait(timeout=5)
        for stream in (child.stdin,child.stdout,child.stderr):
            if stream:stream.close()
    def test_two_independent_slots_third_and_legacy_refused(self):
        first=self.worker();second=self.worker()
        self.assertEqual({json.loads(first.stdout.readline())['slot'],json.loads(second.stdout.readline())['slot']},{0,1})
        third=self.worker();self.assertEqual(third.stdout.readline().strip(),'REFUSED');self.assertEqual(third.wait(timeout=5),3)
        legacy=slots.opened(self.canonical)
        try:
            with self.assertRaises(BlockingIOError):fcntl.flock(legacy,fcntl.LOCK_EX|fcntl.LOCK_NB)
            self.stop(first)
            with self.assertRaises(BlockingIOError):fcntl.flock(legacy,fcntl.LOCK_EX|fcntl.LOCK_NB)
            self.stop(second);fcntl.flock(legacy,fcntl.LOCK_EX|fcntl.LOCK_NB)
        finally:os.close(legacy)
        self.assertEqual(self.canonical.stat().st_ino,self.inode);self.assertEqual(self.canonical.read_text(),'canonical owner journal')
    def test_legacy_and_mutation_exclusive_exclude_portable_readers(self):
        legacy=slots.opened(self.canonical)
        try:
            fcntl.flock(legacy,fcntl.LOCK_EX|fcntl.LOCK_NB)
            blocked=self.worker();self.assertEqual(blocked.stdout.readline().strip(),'REFUSED');self.assertEqual(blocked.wait(timeout=5),3)
        finally:os.close(legacy)
        mutation=self.worker(True);json.loads(mutation.stdout.readline())
        blocked=self.worker();self.assertEqual(blocked.stdout.readline().strip(),'REFUSED');self.assertEqual(blocked.wait(timeout=5),3)
    def test_child_retains_both_descriptors_after_parent_closes(self):
        ready=self.root/'ready';release=self.root/'release'
        program='''import sys,json,subprocess,time,pathlib
sys.path.insert(0,sys.argv[1]);import slots
child_code="""import sys,json,time,pathlib
sys.path.insert(0,sys.argv[1]);import slots
record=json.loads(sys.argv[3]);slots.inherited(record,sys.argv[2])
pathlib.Path(sys.argv[4]).write_text('ready')
while not pathlib.Path(sys.argv[5]).exists():time.sleep(.02)
"""
with slots.lease(sys.argv[2]) as record:
 child=subprocess.Popen([sys.executable,'-B','-c',child_code,sys.argv[1],sys.argv[2],json.dumps(record),sys.argv[3],sys.argv[4]],pass_fds=(record['heavyFd'],record['slotFd']),stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 deadline=time.monotonic()+5
 while not pathlib.Path(sys.argv[3]).exists():
  assert time.monotonic()<deadline;time.sleep(.02)
 print(child.pid,flush=True)
'''
        parent=subprocess.run([sys.executable,'-B','-c',program,str(MODULE),str(self.root),str(ready),str(release)],text=True,capture_output=True,timeout=6)
        self.assertEqual(parent.returncode,0,parent.stderr);pid=int(parent.stdout.strip())
        try:
            self.assertTrue(ready.exists())
            heavy=slots.opened(self.canonical)
            try:
                with self.assertRaises(BlockingIOError):fcntl.flock(heavy,fcntl.LOCK_EX|fcntl.LOCK_NB)
            finally:os.close(heavy)
            with slots.lease(self.root) as peer:self.assertEqual(peer['slot'],1)
            probe=slots.opened(self.root/'trusted-deep-slot-0.lock')
            try:
                with self.assertRaises(BlockingIOError):fcntl.flock(probe,fcntl.LOCK_EX|fcntl.LOCK_NB)
            finally:os.close(probe)
        finally:
            release.write_text('release')
            deadline=time.monotonic()+5
            while time.monotonic()<deadline:
                probe=slots.opened(self.canonical)
                try:
                    fcntl.flock(probe,fcntl.LOCK_EX|fcntl.LOCK_NB);break
                except BlockingIOError:time.sleep(.02)
                finally:os.close(probe)
            else:os.kill(pid,signal.SIGTERM);self.fail('Inherited reservation was not released')
    def test_wrong_slot_descriptor_and_replaced_inode_refused(self):
        with slots.lease(self.root) as record:
            wrong=slots.opened(self.root/f"trusted-deep-slot-{record['slot']}.lock")
            try:
                with self.assertRaises(BlockingIOError):slots.inherited(dict(record,slotFd=wrong),self.root)
            finally:os.close(wrong)
            replacement=self.root/'replacement';replacement.write_text('other');replacement.replace(self.canonical)
            with self.assertRaises(RuntimeError):slots.inherited(record,self.root)
    def test_orphan_or_unmanaged_container_refused(self):
        name='hl-mini-'+'a'*32+'-a'
        row={'Config':{'Labels':{'org.harborline.trusted.slot':'0','org.harborline.mini.session':'a'*32}},'HostConfig':{'Memory':10*2**30,'MemorySwap':10*2**30}}
        with patch.object(pilot,'docker',side_effect=[name,json.dumps([row])]),self.assertRaises(ValueError):pilot.inventory(self.root)
        journal={'container':name,'session':'a'*32,'pid':os.getpid(),'processStamp':'trusted'}
        (self.root/'trusted-deep-slot-0.json').write_text(json.dumps(journal));(self.root/'trusted-deep-slot-0.lock').touch()
        with patch.object(pilot,'docker',side_effect=[name,json.dumps([row])]),self.assertRaises(ValueError):pilot.inventory(self.root)
        with patch.object(pilot,'docker',return_value='unrelated-user-container'),self.assertRaises(ValueError):pilot.inventory(self.root)
    def test_cleanup_refuses_peer_names(self):
        with patch.object(pilot,'docker',return_value='unrelated-user-container') as docker:
            result=pilot.cleanup('a'*32,'hl-mini-'+'a'*32+'-a')
            self.assertFalse(result['clean']);self.assertEqual(len(result['failures']),3)
            self.assertTrue(all(call.args[0] not in ('rm',) and call.args[1]!='rm' for call in docker.call_args_list))

if __name__=='__main__':unittest.main()
