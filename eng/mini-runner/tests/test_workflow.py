"""Execute the actual workflow boundaries using literal required-lane oracles."""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT/'.github/workflows/verify.yml'


def script(marker):
    text = WORKFLOW.read_text()
    start = text.index('          # BEGIN '+marker)
    end = text.index('          # END '+marker, start)
    return textwrap.dedent(text[start:end])


class WorkflowAggregate(unittest.TestCase):
    def test_workflow_itself_refuses_every_missing_or_failed_required_lane(self):
        body = script('required mini aggregate')
        def execute(route, needs, native='required', draft='false'):
            env = dict(os.environ, LINUX_ROUTE=route, NEEDS=json.dumps(needs), NATIVE_POLICY=native, DRAFT=draft, GITHUB_STEP_SUMMARY=os.devnull)
            return subprocess.run(['bash','-c',body],env=env,capture_output=True,text=True,timeout=10).returncode
        for route, selected, other in [('mini','verify-mini','verify-linux'),('hosted','verify-linux','verify-mini')]:
            required = ['verify-route','verify-macos','verify-perf-hosted','verify-windows-hosted',selected]
            if route == 'hosted':
                required.append('verify-shared')
            needs = {name:{'result':'success'} for name in required}
            needs.update({other:{'result':'skipped'},'verify-windows':{'result':'skipped'}})
            if route == 'mini':
                needs['verify-shared']={'result':'skipped'}
            self.assertEqual(execute(route,needs),0)
            for name in required:
                for result in ('skipped','failure','cancelled',None):
                    altered=copy.deepcopy(needs);altered[name]={'result':result}
                    with self.subTest(route=route,name=name,result=result):
                        self.assertNotEqual(execute(route,altered),0)
                altered=copy.deepcopy(needs);del altered[name]
                self.assertNotEqual(execute(route,altered),0)
            self.assertNotEqual(execute('',needs),0)
            self.assertNotEqual(execute(route,needs,draft='true'),0)
            needs['verify-windows-hosted']['result']='skipped'
            needs['verify-macos']['result']='skipped'
            self.assertNotEqual(execute(route,needs),0)
            self.assertEqual(execute(route,needs,native='development-suspended'),0)
            for name in ('verify-windows-hosted','verify-windows','verify-macos'):
                for bad in ('success','failure','cancelled',None):
                    altered=copy.deepcopy(needs);altered[name]['result']=bad
                    self.assertNotEqual(execute(route,altered,native='development-suspended'),0)
            for policy in ('','optional'):
                self.assertNotEqual(execute(route,needs,native=policy),0)


class WorkflowSelector(unittest.TestCase):
    def test_workflow_hashes_precede_imports_and_exclude_candidate_siblings(self):
        body=script('immutable mini selector')
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);modules=root/'eng/mini-runner';modules.mkdir(parents=True)
            names=('required-route.py','controller.py','admission.py')
            for name in names:
                shutil.copyfile(ROOT/'eng/mini-runner'/name,modules/name)
            (root/'event.json').write_text('{}')
            output=root/'output'
            env=dict(os.environ,GITHUB_EVENT_NAME='workflow_dispatch',GITHUB_REPOSITORY_ID='1360432948',
                     GITHUB_REPOSITORY='Harborline-Software/harborline-api',GITHUB_SHA='a'*40,
                     GITHUB_RUN_ID='42',GITHUB_RUN_ATTEMPT='1',GITHUB_EVENT_PATH=str(root/'event.json'),GITHUB_OUTPUT=str(output))
            def execute():
                output.unlink(missing_ok=True)
                return subprocess.run(['bash','-c',body],cwd=root,env=env,capture_output=True,text=True,timeout=10)
            for directory in (root,modules):
                for name in ('json.py','argparse.py','hashlib.py'):
                    (directory/name).write_text('raise RuntimeError("candidate sibling imported")\n')
            result=execute();self.assertEqual(result.returncode,0,result.stderr)
            self.assertIn('route=hosted',output.read_text())
            for name in names:
                original=(modules/name).read_bytes()
                (modules/name).write_text('raise SystemExit(0)\n')
                result=execute()
                with self.subTest(module=name):
                    self.assertNotEqual(result.returncode,0)
                    self.assertIn('Unreviewed selector module',result.stderr)
                    self.assertFalse(output.exists())
                (modules/name).write_bytes(original)
