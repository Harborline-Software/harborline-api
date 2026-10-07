"""Immutable, pre-exit gate validation. No network, credentials or candidate imports."""
import json
import os
from pathlib import Path
import sys

# -I excludes cwd/PYTHONPATH. Only the immutable image directory is imported.
ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
import controller


def finish(inputs, gate, env):
    policy = json.loads((inputs/'policy.json').read_text())
    sources_file = inputs/'sources.json'
    controller.require(controller.digest(sources_file) == policy['sourcesSha256'], 'Source manifest changed')
    sources = json.loads(sources_file.read_text())
    for name, record in sources.items():
        controller.require(controller.git(gate/name, 'rev-parse', 'HEAD') == record['head'], 'Source head changed')
        # These are fresh private clones. Expected build/cache outputs must be
        # ignored by each reviewed repository; never exempt unexpected files.
        controller.require(not controller.git(gate/name, 'status', '--porcelain'), 'Source is dirty: '+name)
    controller.require(controller.git(gate/'api', 'rev-parse', 'HEAD^{tree}') == policy['tree'], 'Tested tree changed')
    if policy.get('candidate'):
        candidate = policy['candidate']
        controller.require(controller.git(gate/'api', 'rev-parse', 'origin/main') == candidate['base'], 'Comparison base changed')
        if policy['version'] == 3:
            controller.require(controller.git(gate/'api', 'diff', '--name-status', '-z', '--find-renames', candidate['base'], 'HEAD')
                               == policy['comparisonDiff'], 'Source diff changed')
        for key, value in {'GITHUB_RUN_ID': policy['runId'], 'GITHUB_RUN_ATTEMPT': '1',
                           'GITHUB_SHA': policy['head'], 'GITHUB_WORKFLOW_SHA': candidate['workflowHead'],
                           'GITHUB_WORKFLOW_REF': candidate['workflowRef'],
                           'GITHUB_JOB': policy.get('jobKey', 'portable')}.items():
            controller.require(env.get(key) == value, 'Completion runtime identity changed: '+key)
    controller.validate_receipt(gate/'out', policy['head'], policy['tree'], policy.get('coverage', False))
    if policy.get('candidate'):
        runtime = None
        if policy['version'] == 3:
            runtime = controller.measure_focused_runtime(inputs, gate)
            controller.validate_runtime_anchor(runtime, policy, controller.digest(inputs/'policy.json'))
        controller.validate_candidate_evidence(gate/'out', policy, runtime=runtime)
    value = {'runId': policy['runId'], 'head': policy['head'], 'tree': policy['tree'],
             'policySha256': controller.digest(inputs/'policy.json'),
             'receiptSha256': controller.digest(gate/'out'/'harborline-api-verify-receipt.json'),
             'verdict': 'passed'}
    (gate/'out'/'immutable-validation.json').write_text(json.dumps(value, indent=2)+'\n')
    print('MINI_IMMUTABLE_EVIDENCE_VALIDATED', flush=True)


if __name__ == '__main__':
    if sys.argv[1:] == ['--measure-focused']:
        print(json.dumps(controller.measure_focused_runtime(ROOT, Path('/runner/gate')), sort_keys=True))
    else:
        finish(ROOT, Path('/runner/gate'), os.environ)
