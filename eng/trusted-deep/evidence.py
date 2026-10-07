"""Exact-input reuse and release promotion; failure or absent proof always holds promotion."""
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
from manifest import FINGERPRINT_KEYS, require
ROOT=Path(__file__).resolve().parents[2]
MODULE=Path(__file__).resolve().parent

def native_context(root,expected):
    context=json.loads((root/'out/gate-evidence/native-context.json').read_bytes())
    measured={**context['sources'],'tree':context['tree'],'base':context['base'],'sdk':context['sdk'],
              'image':context['image'],**context['inputDigests'],'kind':'native-full','os':context['platform'],'architecture':context['architecture']}
    require(measured==expected,'Native measured inputs differ from approved fingerprint')
    job_key={'windows':'verify-windows-hosted','macos':'verify-macos'}[expected['os']]
    require(context['sha']==expected['api'] and context['workflowSha']==expected['api'] and
            context['jobKey']==job_key and context['repositoryId']=='1360432948' and
            context['event']=='workflow_dispatch' and context['attempt']=='1','Native workflow/job/source binding differs')
    indexed=json.loads((root/'hosted-job.json').read_bytes())
    require(type(indexed['runId']) is int and type(indexed['jobId']) is int and indexed['jobId']>0 and
            str(indexed['runId'])==context['runId'],'Native indexed run/job identity differs')
    receipt=json.loads((root/'out/harborline-api-verify-receipt.json').read_bytes())
    require(receipt['schemaVersion']==1 and receipt['repository']=='harborline-api' and receipt['lane']=='host' and
            receipt['baseHead']==expected['api'] and receipt['testedTree']==expected['tree'] and
            receipt['hostBaseline']==('eng/baselines/host-test-baseline.json' if expected['os']=='windows' else 'eng/baselines/host-test-baseline.macos.json'),'Native host receipt source/profile differs')
    require('exact-clone' in [s if isinstance(s,str) else s['id'] for s in receipt['steps']],'Native full host suite missing')
    return context

def validate_native_raw(root,expected):
    native_context(root,expected)
    require(subprocess.check_output(['git','-C',str(ROOT),'rev-parse','HEAD'],text=True).strip()==expected['api'], 'Raw reader must use exact candidate source')
    result=subprocess.run(['node',str(MODULE/'raw-evidence.mjs'),'native-full',str(root/'out'),str(ROOT),'check'],capture_output=True,text=True,timeout=60)
    require(result.returncode==0,'Native raw tests/baselines disagree; inspect retained raw artifacts')


def required_artifacts(expected):
    if expected['kind']=='native-full':
        return {'out/gate-evidence/native-context.json','hosted-job.json','out/harborline-api-verify-receipt.json',
                'out/gate-evidence/host-tests.trx','out/gate-evidence/named-test-outcomes.json',
                'out/gate-evidence/capability-tests.json','out/gate-evidence/exact-clone-report.json','out/raw-validation.json'}
    common={'session.json','cleanup.json','resources/summary.json','out/immutable-completion.json','out/raw-validation.json','out/gc-preflight.json'}
    if expected['kind'] in ('portable','portable-coverage'):
        common |= {'out/gate-exit.txt','out/harborline-api-verify-receipt.json','out/harborline-api-quality-decision.json',
                   'out/gate-evidence/host-tests.trx','out/gate-evidence/named-test-outcomes.json'}
        if expected['kind']=='portable-coverage':
            common |= {'out/quality/host.cobertura.xml','out/quality/contracts.cobertura.xml'}
    elif expected['kind']=='mutation-benchmark':
        common |= {'out/mutation-exit.txt','out/mutation-reports/report.json'}
    else:
        raise ValueError('Unknown evidence profile')
    return common


def equivalent(receipt_bytes, expected, artifact_root, trusted_receipt_digest, now):
    require(hashlib.sha256(receipt_bytes).hexdigest() == trusted_receipt_digest, "Receipt is not in the trusted evidence index")
    receipt = json.loads(receipt_bytes)
    require(set(expected) == FINGERPRINT_KEYS and receipt.get("fingerprint") == expected, "Evidence inputs are not equivalent; rerun")
    require(receipt.get("status") == "passed" and receipt.get("cleanup") is True, "Failed/incomplete evidence")
    require(receipt.get("oom") == 0 and receipt.get("swapMiB") == 0, "Resource alarm")
    completed = datetime.datetime.fromisoformat(receipt["completedAt"])
    require(completed.tzinfo is not None and 0 <= (now - completed).total_seconds() <= 72 * 3600, "Stale or future evidence")
    artifacts = receipt.get("artifacts")
    require(isinstance(artifacts, dict) and set(artifacts) >= required_artifacts(expected), "Complete raw evidence contract missing")
    root = Path(artifact_root).resolve()
    for name, expected_digest in artifacts.items():
        path = root / name
        require(not Path(name).is_absolute() and ".." not in Path(name).parts and not path.is_symlink(), "Unsafe evidence path")
        require(path.resolve().is_relative_to(root) and path.is_file(), "Artifact absent or outside evidence root")
        require(hashlib.sha256(path.read_bytes()).hexdigest() == expected_digest, "Raw artifact changed")
    if expected['kind']!='native-full':
        cleanup=json.loads((root/'cleanup.json').read_bytes())
        resources=json.loads((root/'resources/summary.json').read_bytes())
        completion=json.loads((root/'out/immutable-completion.json').read_bytes())
        gc=json.loads((root/'out/gc-preflight.json').read_bytes())
        require(gc['requestedEnv']=='0x32' and gc['availableBytes']==5368709120 and
                gc['config']['GCHeapHardLimit']==5368709120 and gc['config']['GCHeapHardLimitPercent']==50,'Runtime heap budget evidence differs')
        require(cleanup['clean'] is True and not cleanup['failures'],'Cleanup evidence failed')
        require(resources['alarm'] is None and resources['samples']>0 and resources['maxHostSwapMiB']==0,'Telemetry absent or failed')
        require(completion['verdict']=='passed' and completion['head']==expected['api'] and completion['tree']==expected['tree'], 'Immutable source completion mismatch')
        require(completion['task']['kind']==expected['kind'],'Completed profile differs')
    else:
        validate_native_raw(root,expected)
    return receipt


def release_ready(candidate, expected, raw_receipts, artifact_roots, trusted_digests, incidents, now):
    # Caller obtains expected fingerprints/digests and incidents from trusted operator state.
    # Receipt claims cannot assert their own approval or clear a promotion incident.
    require(not incidents, "Unresolved critical deep failure: promotion stopped")
    platforms = {"linux", "windows", "macos"}
    require(set(raw_receipts) == set(expected) == set(artifact_roots) == set(trusted_digests) == platforms,
            "Full native release proof required")
    common=FINGERPRINT_KEYS-{'image','environment','os','architecture','kind'}
    require(all(all(expected[p][key]==expected['linux'][key] for key in common) for p in platforms),
            'Release source pins, SDK, scripts and verification inputs must agree across platforms')
    for platform in platforms:
        fingerprint = expected[platform]
        require(fingerprint["api"] == candidate and fingerprint["os"] == platform,
                "Release candidate/platform does not match approved expectation")
        require(fingerprint['kind']==('portable-coverage' if platform=='linux' else 'native-full'),'Release requires full coverage/native profiles')
        proof = equivalent(raw_receipts[platform], fingerprint, artifact_roots[platform],
                           trusted_digests[platform], now)
        require(proof.get("suite") == "full", "Incomplete native/portable suite")
        # Linux all17 evidence is separate from the five required hosted check contexts.
        require(proof.get("criticalCheckSet") in ('api-portable-all17-v1','api-required-v1'), "Wrong release contract")
    return True
