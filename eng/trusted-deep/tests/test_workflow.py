import copy
import json
import os
from pathlib import Path
import subprocess
import textwrap
import unittest
ROOT=Path(__file__).resolve().parents[3]

class HostedWorkflow(unittest.TestCase):
    def test_required_dag_is_hosted_and_has_no_deep_dependency(self):
        jobs=json.loads(subprocess.check_output(['ruby','-r','yaml','-r','json','-e','puts JSON.generate(YAML.safe_load(File.read(ARGV[0]), aliases: false))',str(ROOT/'.github/workflows/verify.yml')],text=True))['jobs']
        self.assertEqual(set(jobs['verify']['needs']),{'verify-shared','verify-linux','verify-perf-hosted','verify-macos','verify-windows-hosted'})
        self.assertEqual(jobs['verify']['if'],'always()')
        for name in ['verify',*jobs['verify']['needs']]:
            self.assertIsInstance(jobs[name]['runs-on'],str)
            self.assertNotIn('self-hosted',jobs[name]['runs-on'])
        self.assertNotIn('verify-mini',jobs)
        self.assertFalse((ROOT/'.github/workflows/mini-candidate-gate.yml').exists())
        linux=jobs['verify-linux'];self.assertEqual(linux['env']['HARBORLINE_GATE_QUALITY'],'1')
        self.assertIn('eng/verify.sh',json.dumps(linux));self.assertIn('eng/verify.sh',json.dumps(jobs['verify-shared']))
    def test_hosted_capacity_is_not_capped_by_local_slots(self):
        document=json.loads(subprocess.check_output(['ruby','-r','yaml','-r','json','-e','puts JSON.generate(YAML.safe_load(File.read(ARGV[0]), aliases: false))',str(ROOT/'.github/workflows/verify.yml')],text=True))
        self.assertEqual(document['concurrency']['group'],"verify-${{ github.event.pull_request.number || (github.event_name == 'merge_group' && github.ref) || github.run_id }}")
        self.assertEqual(document['concurrency']['cancel-in-progress'],"${{ github.event_name == 'pull_request' }}")
        for name in ('verify-shared','verify-linux','verify-perf-hosted'):
            self.assertNotIn('strategy',document['jobs'][name])
            self.assertNotIn('concurrency',document['jobs'][name])

    def test_actual_aggregate_rejects_every_missing_failed_skipped_lane(self):
        text=(ROOT/'.github/workflows/verify.yml').read_text();start=text.index('          # BEGIN hosted critical aggregate');end=text.index('          # END hosted critical aggregate',start);body=textwrap.dedent(text[start:end])
        needs={name:{'result':'success'} for name in ('verify-shared','verify-linux','verify-perf-hosted')}
        needs.update({name:{'result':'skipped'} for name in ('verify-macos','verify-windows-hosted')})
        def run(value,native='development-suspended',draft='false'):
            return subprocess.run(['bash','-c',body],env=dict(os.environ,NEEDS=json.dumps(value),NATIVE_POLICY=native,DRAFT=draft,GITHUB_STEP_SUMMARY=os.devnull),capture_output=True,timeout=5).returncode
        self.assertEqual(run(needs),0)
        for name in needs:
            changed=copy.deepcopy(needs);del changed[name]
            with self.subTest(missing=name):self.assertNotEqual(run(changed),0)
            for outcome in ('failure','cancelled',None):
                changed=copy.deepcopy(needs);changed[name]['result']=outcome
                with self.subTest(name=name,outcome=outcome):self.assertNotEqual(run(changed),0)
        for name in ('verify-shared','verify-linux','verify-perf-hosted'):
            changed=copy.deepcopy(needs);changed[name]['result']='skipped';self.assertNotEqual(run(changed),0)
        self.assertNotEqual(run(needs,draft='true'),0);self.assertNotEqual(run(needs,native='optional'),0)
        self.assertNotEqual(run(needs,native='required'),0)
        for name in ('verify-macos','verify-windows-hosted'):needs[name]['result']='success'
        self.assertEqual(run(needs,native='required'),0)
        self.assertNotEqual(run(needs),0)

if __name__=='__main__':unittest.main()
