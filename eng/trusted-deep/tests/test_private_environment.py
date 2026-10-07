import json
import os
from pathlib import Path
import shlex
import subprocess
import textwrap
import unittest

ROOT=Path(__file__).resolve().parents[3]

class PrivateCheckoutEnvironment(unittest.TestCase):
    def test_private_identity_is_retained_outside_checkout_proofs(self):
        # Oracle: the checked-out git source, the literal API repository contract,
        # and honest local child provenance; private workflow metadata stays outer.
        source=subprocess.check_output(['git','-C',str(ROOT),'rev-parse','HEAD'],text=True).strip()
        code="""import importlib.util,json,os,sys
spec=importlib.util.spec_from_file_location('package_proof',sys.argv[1])
p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p)
print(json.dumps({'proof':p.identity('fixture-version'),'githubNames':[k for k in os.environ if k.startswith('GITHUB_')],
                  'coverage':os.environ['HARBORLINE_GATE_COVERAGE'],'heap':os.environ['DOTNET_GCHeapHardLimitPercent']}))
"""
        env=dict(os.environ,GITHUB_SHA='f'*40,GITHUB_REPOSITORY='Harborline-Software/harborline-control',
                 GITHUB_RUN_ID='42',GITHUB_RUN_ATTEMPT='1',GITHUB_WORKFLOW_REF='private-workflow',
                 GITHUB_FUTURE_IDENTITY='must-be-removed',DOTNET_GCHeapHardLimitPercent='0x32',HARBORLINE_GATE_COVERAGE='0')
        command=['python3','-I','-c',code,str(ROOT/'eng/package-proof.py')]
        direct=subprocess.run(command,env=env,capture_output=True,text=True,timeout=5)
        self.assertNotEqual(direct.returncode,0)
        self.assertIn('checkout does not match workflow source',direct.stderr)
        text=(ROOT/'eng/trusted-deep/run.sh').read_text()
        start=text.index('  # BEGIN isolated checkout environment')
        end=text.index('  # END isolated checkout environment',start)
        body=textwrap.dedent(text[start:end])
        # Replace only the expensive gate command with the real package identity
        # consumer; execute the saved production environment boundary unchanged.
        body=body.replace('bash /opt/mini/portable-gate.sh',shlex.join(command))
        for mode in ('0','1'):
            actual=subprocess.run(['bash','-c',body+'\nprintf "%s\\n" "$GITHUB_SHA" "$GITHUB_RUN_ID"'],
                                  env=dict(env,HARBORLINE_GATE_COVERAGE=mode),capture_output=True,text=True,timeout=5)
            self.assertEqual(actual.returncode,0,actual.stderr)
            lines=actual.stdout.splitlines();measured=json.loads(lines[0])
            self.assertEqual(measured,{'proof':{'version':'fixture-version','source':source,
                 'repository':'Harborline-Software/harborline-api','run':'local','attempt':'local','workflow':'local'},
                 'githubNames':[],'coverage':mode,'heap':'0x32'})
            self.assertEqual(lines[1:],['f'*40,'42'])

if __name__=='__main__':unittest.main()
