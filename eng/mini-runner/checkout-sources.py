"""Restore only immutable bundled commits, including the actual comparison base."""
import hashlib
import json
import os
from pathlib import Path
import subprocess


def restore(inputs, root, env):
    source_bytes = (inputs/'sources.json').read_bytes()
    sources = json.loads(source_bytes)
    policy = json.loads((inputs/'policy.json').read_text())
    source_digest = hashlib.sha256(source_bytes).hexdigest()
    if source_digest != policy['sourcesSha256'] or set(sources) != {'api', 'platform', 'quality', 'control'}:
        raise RuntimeError('Source manifest identity mismatch')
    candidate = policy.get('candidate')
    if candidate and env.get('HARBORLINE_GATE_COVERAGE') != ('1' if policy['coverage'] else '0'):
        raise RuntimeError('Candidate coverage mode mismatch')
    for name, record in sources.items():
        bundle = inputs/'bundles'/(name+'.bundle')
        if hashlib.sha256(bundle.read_bytes()).hexdigest() != record['bundleSha256']:
            raise RuntimeError('Source bundle digest mismatch')
        target = root/name
        subprocess.run(['git', 'clone', str(bundle), str(target)], check=True, timeout=90)
        def git(*args):
            return subprocess.check_output(['git', '-C', str(target), *args], text=True, timeout=90).strip()
        git('checkout', '--detach', record['head'])
        if git('rev-parse', 'HEAD') != record['head']:
            raise RuntimeError('Source head mismatch')
        base = record['head']
        if name == 'api':
            if record['head'] != policy['head'] or git('rev-parse', 'HEAD^{tree}') != policy['tree']:
                raise RuntimeError('Tested source/tree mismatch')
            if candidate:
                base = record.get('comparisonBase')
                if base != candidate['base'] or base == record['head'] or record['head'] != candidate['head']:
                    raise RuntimeError('Candidate source/base mismatch')
                if git('merge-base', base, record['head']) != base:
                    raise RuntimeError('Missing or unrelated comparison base')
        git('update-ref', 'refs/remotes/origin/main', base)
        if git('rev-parse', 'refs/remotes/origin/main') != base:
            raise RuntimeError('Comparison ref mismatch')
        if name == 'api' and candidate:
            changed = git('diff', '--name-status', '--no-renames', base, 'HEAD')
            (root/'out'/'candidate-provenance.json').write_text(json.dumps({
                'runId': policy['runId'], 'head': policy['head'], 'tree': policy['tree'],
                'candidate': candidate, 'coverage': policy['coverage'],
                'sourcesSha256': source_digest, 'comparisonBase': base,
                'changedPaths': changed.splitlines(),
            }, indent=2)+'\n')


if __name__ == '__main__':
    restore(Path('/opt/mini'), Path('/runner/gate'), os.environ)
