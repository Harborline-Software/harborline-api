"""Required-route oracles: explicit event scope and mandatory selected-lane truth table."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import sys
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import admission
import controller as c
from test_candidate import fixture, BASE, MERGE, GROUP, PR_HEAD, TREE
import test_candidate as candidate_tests

spec = importlib.util.spec_from_file_location('required_route', Path(c.__file__).with_name('required-route.py'))
route = importlib.util.module_from_spec(spec)
spec.loader.exec_module(route)


def required_fixture(event='pull_request'):
    candidate, policy, env, payload, run = fixture(event)
    candidate['required'] = True
    candidate['workflowRef'] = candidate['workflowRef'].replace('mini-candidate-gate.yml', 'verify.yml')
    policy.update(version=3, jobKey='verify-mini', jobId='73')
    env.update(GITHUB_JOB='verify-mini', GITHUB_WORKFLOW_REF=candidate['workflowRef'])
    run['path'] = '.github/workflows/verify.yml'
    if event == 'pull_request':
        payload['pull_request']['labels'] = []
    jobs = [
        {'id': 72, 'name': 'verify-route', 'status': 'completed', 'conclusion': 'success', 'labels': ['ubuntu-latest']},
        {'id': 73, 'name': 'verify-mini', 'status': 'queued', 'runner_id': 0,
         'labels': ['self-hosted', 'harborline-api-mini-required-v3', 'linux-arm64-orbstack', 'lane-a']},
        {'id': 74, 'name': 'verify-windows-hosted', 'status': 'in_progress', 'labels': ['windows-2025']},
    ]
    return candidate, policy, env, payload, run, jobs


class RequiredAdmission(unittest.TestCase):
    def test_both_events_bind_required_job_and_keep_other_jobs_independent(self):
        for event in ('pull_request', 'merge_group'):
            candidate, policy, env, payload, run, jobs = required_fixture(event)
            self.assertTrue(admission.admitted(policy, env, payload))
            self.assertEqual(c.validate_candidate_run(run, jobs, candidate, b'reviewed', b'reviewed'), policy)
            for field, value in [('jobKey', 'portable'), ('jobId', '0'), ('jobId', None), ('version', 2)]:
                self.assertFalse(admission.admitted({**policy, field: value}, env, payload))
            self.assertFalse(admission.admitted(policy, {**env, 'GITHUB_JOB': 'verify-linux'}, payload))
            for altered in (jobs+jobs[1:2], jobs[:1]+jobs[2:], jobs+[{'name': 'unreviewed', 'labels': ['ubuntu-latest']}],
                            jobs[:2]+[{**jobs[2], 'labels': ['harborline-api-mini-required-v3']}]):
                with self.assertRaises(RuntimeError):
                    c.validate_candidate_run(run, altered, candidate, b'reviewed', b'reviewed')

    def test_controller_finishes_mini_while_verify_workflow_is_still_running(self):
        self.controller_completion(True)

    def test_controller_refuses_success_without_live_runtime_measurement(self):
        self.controller_completion(False)

    def controller_completion(self, capture_runtime):
        _, policy, _, _, run, jobs = required_fixture()
        policy.update(tree=TREE, sdk='11.0.100-rc.1.26425.128')
        jobs[1].update(status='completed', conclusion='success', runner_id=8, runner_name='hl-mini-'+'a'*32+'-a')
        run.update(status='in_progress', conclusion=None)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); (root/'policy.json').write_text(json.dumps(policy)); (root/'heavy.lock').touch()
            args = argparse.Namespace(policy=str(root/'policy.json'), output=str(root/'evidence'), lock=str(root/'heavy.lock'), image='sha256:'+'a'*64)
            job_polls = iter(('in_progress', 'completed'))
            def api(path, method='GET'):
                if method == 'POST':
                    return {'token': 'fixture-only'}
                if '/jobs?' in path:
                    jobs[1]['status'] = next(job_polls)
                    return {'total_count': len(jobs), 'jobs': jobs}
                return run
            def docker(*args, **kwargs):
                if '--measure-focused' in args[-1]:
                    self.assertEqual(jobs[1]['status'], 'in_progress', 'must measure before ephemeral exit')
                    if not capture_runtime:
                        return ''
                    return json.dumps({'policySha256': c.digest(root/'policy.json'), 'runId': policy['runId'],
                                       'jobId': policy['jobId'], 'head': policy['head'],
                                       'host': 'fixture/linux/arm64', 'dependencies': 'sha256:'+'b'*64})
                return '2.338.0' if args[-1] == '--version' else ''
            with patch.object(c, 'pending', return_value=policy), patch.object(c, 'preflight_image', return_value={}), \
                    patch.object(c, 'api', side_effect=api), patch.object(c, 'docker', side_effect=docker), \
                    patch.object(c.uuid, 'uuid4', return_value=SimpleNamespace(hex='a'*32)), \
                    patch.object(c, 'validate_receipt') as receipt, patch.object(c, 'validate_candidate_evidence') as candidate_evidence, \
                    patch.object(c, 'cleanup', return_value={'clean': True}) as cleanup, \
                    patch.object(c.time, 'sleep') as sleep, patch.object(c, 'candidate_state', return_value=TREE):
                if capture_runtime:
                    c.execute(args)
                    candidate_evidence.assert_called_once()
                    self.assertEqual(candidate_evidence.call_args.kwargs['runtime']['host'], 'fixture/linux/arm64')
                else:
                    with self.assertRaisesRegex(RuntimeError, 'Run or verified cleanup failed'):
                        c.execute(args)
                    candidate_evidence.assert_not_called()
                receipt.assert_called_once(); cleanup.assert_called_once()
                sleep.assert_called_once_with(5)
            result = json.loads((root/'evidence/result.json').read_text())
            self.assertEqual(result['success'], capture_runtime); self.assertTrue(result['selectedJobOnly'])
            self.assertEqual([job['id'] for job in result['jobs']], [73])


class RequiredSelection(unittest.TestCase):
    def setUp(self):
        # Selection tests must never consult the network, including when a
        # refusal guard is deliberately removed by an executed causal control.
        guard = patch.object(c, 'candidate_state', return_value=TREE)
        guard.start(); self.addCleanup(guard.stop)
    def test_owner_pr_selects_mini_with_bound_descriptor(self):
        candidate, _, env, payload, _, _ = required_fixture()
        with patch.object(c, 'candidate_state', return_value=TREE) as state:
            selected = route.choose(env, payload, api=lambda _: {'object': {'sha': MERGE}})
            self.assertEqual(selected['route'], 'mini')
            self.assertEqual(selected['candidate'], candidate)
            state.assert_called_once_with(candidate)

    def test_regular_owner_branches_keep_exact_ref_binding_without_pilot_prefix(self):
        for branch in ('fix/host-startup', 'feature/ordinary-pr'):
            candidate, policy, env, payload, run, _ = required_fixture()
            candidate['headBranch'] = branch
            payload['pull_request']['head']['ref'] = branch
            env['GITHUB_HEAD_REF'] = branch
            run['head_branch'] = branch
            self.assertTrue(admission.admitted(policy, env, payload))
            selected = route.choose(env, payload, api=lambda _: {'object': {'sha': MERGE}})
            self.assertEqual(selected['route'], 'mini')
            self.assertEqual(selected['candidate']['headBranch'], branch)
            self.assertFalse(admission.admitted(policy, {**env, 'GITHUB_HEAD_REF': 'unrelated'}, payload))
            # Historical v2 qualification retains its narrower contract.
            old, *_ = fixture('pull_request')
            old['headBranch'] = branch
            self.assertFalse(admission.candidate_shape(old))

    def test_known_other_routes_are_hosted_but_stale_attempt_or_ref_is_red(self):
        _, _, env, payload, _, _ = required_fixture()
        for kind in ('schedule', 'workflow_dispatch'):
            self.assertEqual(route.choose({**env, 'GITHUB_EVENT_NAME': kind}, {})['route'], 'hosted')
        self.assertEqual(route.choose({**env, 'GITHUB_ACTOR_ID': '49699333', 'GITHUB_ACTOR': 'dependabot[bot]'}, payload)['route'], 'hosted')
        for path, value in [(('head', 'repo', 'fork'), True), (('draft',), True),
                            (('user', 'id'), 1), (('labels',), [{'name': 'stacked'}])]:
            altered = copy.deepcopy(payload); target = altered['pull_request']
            for part in path[:-1]:
                target = target[part]
            target[path[-1]] = value
            self.assertEqual(route.choose(env, altered)['route'], 'hosted')
        for changes in ({'GITHUB_RUN_ATTEMPT': '2'}, {'GITHUB_REF': 'refs/pull/381/merge'}):
            with self.assertRaises(RuntimeError):
                route.choose({**env, **changes}, payload, api=lambda _: {'object': {'sha': MERGE}})
        with self.assertRaises(RuntimeError):
            route.choose(env, payload, api=lambda _: {'object': {'sha': GROUP}})

    def test_owner_rerun_never_switches_to_hosted_for_a_different_initiator(self):
        for event in ('pull_request', 'merge_group'):
            _, _, env, payload, _, _ = required_fixture(event)
            for actor in ('ctwoodwa', 'another-maintainer'):
                with self.subTest(event=event, actor=actor):
                    with self.assertRaisesRegex(RuntimeError, 'first attempt'):
                        route.choose({**env, 'GITHUB_RUN_ATTEMPT': '2', 'GITHUB_TRIGGERING_ACTOR': actor}, payload)
            with self.assertRaisesRegex(RuntimeError, 'triggering actor changed'):
                route.choose({**env, 'GITHUB_TRIGGERING_ACTOR': 'another-maintainer'}, payload)

    def test_unrecognized_group_refs_are_red_without_a_hosted_fallback(self):
        _, _, env, payload, _, _ = required_fixture('merge_group')
        for ref in ('', 'refs/heads/main', 'refs/heads/gh-readonly-queue/main/not-a-candidate'):
            altered = copy.deepcopy(payload)
            altered['merge_group']['head_ref'] = ref
            with self.subTest(ref=ref):
                with self.assertRaisesRegex(RuntimeError, 'Unrecognized merge-group ref'):
                    route.choose({**env, 'GITHUB_REF': ref}, altered)

    def test_single_group_selects_mini_batches_remain_hosted_unknown_is_red(self):
        candidate, _, env, payload, _, _ = required_fixture('merge_group')
        _, records, entry = candidate_tests.CandidateHost().state_fixture('merge_group')
        entry['mergeQueue']['configuration'] = {'mergeMethod': 'SQUASH'}
        with patch.object(c, 'candidate_state', return_value=TREE):
            result = route.choose(env, payload, api=lambda key: records[key], queue=lambda _: entry)
            self.assertEqual(result['route'], 'mini'); self.assertEqual(result['candidate'], candidate)
        batch = copy.deepcopy(entry)
        batch['mergeQueue']['entries']['nodes'].append({'pullRequest': {'number': 381, 'headRefOid': BASE}, 'headCommit': {'oid': GROUP}})
        for known in (batch, {**entry, 'position': 2}):
            self.assertEqual(route.choose(env, payload, api=lambda key: records[key], queue=lambda _: known)['route'], 'hosted')
        partial = copy.deepcopy(entry); partial['mergeQueue']['entries']['pageInfo']['hasNextPage'] = True
        for unknown in (None, partial, {**entry, 'headCommit': {'oid': MERGE}}):
            with self.assertRaises(RuntimeError):
                route.choose(env, payload, api=lambda key: records[key], queue=lambda _: unknown)
        with patch.object(c, 'candidate_state', side_effect=RuntimeError('API unavailable')):
            with self.assertRaisesRegex(RuntimeError, 'API unavailable'):
                route.choose(env, payload, api=lambda key: records[key], queue=lambda _: entry)


class RequiredOwnerBranchState(unittest.TestCase):
    def test_pr_and_single_group_resolve_owner_regular_branch(self):
        for event in ('pull_request', 'merge_group'):
            candidate, records, entry = candidate_tests.CandidateHost().state_fixture(event)
            candidate['required'] = True
            candidate['workflowRef'] = candidate['workflowRef'].replace('mini-candidate-gate.yml', 'verify.yml')
            records['pulls/380']['head']['ref'] = 'fix/ordinary-owner-branch'
            if event == 'pull_request':
                candidate['headBranch'] = 'fix/ordinary-owner-branch'
            with patch.object(c, 'api', side_effect=lambda key: records[key]), \
                    patch.object(c, 'command', return_value=json.dumps({'data': {'repository': {'pullRequest': {'mergeQueueEntry': entry}}}})):
                self.assertEqual(c.candidate_state(candidate), TREE)


class RequiredAggregation(unittest.TestCase):
    def results(self, selected):
        return {'verify-route': 'success', 'verify-shared': 'success', 'verify-macos': 'success',
                'verify-perf-hosted': 'success', 'verify-windows-hosted': 'success', 'verify-windows': 'skipped',
                'verify-mini': 'success' if selected == 'mini' else 'skipped',
                'verify-linux': 'success' if selected == 'hosted' else 'skipped'}

    def test_every_required_proof_and_selected_route_must_succeed(self):
        for selected in ('mini', 'hosted'):
            rows = self.results(selected)
            self.assertTrue(route.accepts(selected, rows))
            for job, state in rows.items():
                if state != 'success':
                    continue
                for bad in ('skipped', 'failure', 'cancelled', None):
                    with self.subTest(route=selected, job=job, result=bad):
                        self.assertFalse(route.accepts(selected, {**rows, job: bad}))
            other = 'verify-linux' if selected == 'mini' else 'verify-mini'
            self.assertFalse(route.accepts(selected, {**rows, other: 'success'}))
        self.assertFalse(route.accepts('unknown', self.results('mini')))
        self.assertFalse(route.accepts('mini', {**self.results('mini'), 'verify-mini': 'failure', 'verify-linux': 'success'}))

    def test_development_suspension_is_explicit_and_does_not_claim_windows_success(self):
        for selected in ('mini', 'hosted'):
            rows = {**self.results(selected), 'verify-windows-hosted': 'skipped'}
            self.assertFalse(route.accepts(selected, rows))
            self.assertTrue(route.accepts(selected, rows, windows_policy='development-suspended'))
            for name in ('verify-windows-hosted', 'verify-windows'):
                for bad in ('success', 'failure', 'cancelled', None):
                    self.assertFalse(route.accepts(selected, {**rows, name: bad}, windows_policy='development-suspended'))
            for bad in ('', 'optional', None):
                self.assertFalse(route.accepts(selected, rows, windows_policy=bad))


class RequiredFocusedSelection(unittest.TestCase):
    def test_documentation_skip_is_bound_to_actual_admitted_diff(self):
        _, policy, *_ = required_fixture()
        policy.update(tree=TREE, sdk='11.0.100-rc.1.26425.128', sourcesSha256='a'*64,
                      comparisonDiff='M\0docs/guide.md\0')
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            provenance = {k: policy[k] for k in ('runId', 'head', 'tree', 'candidate', 'coverage',
                                                 'sourcesSha256', 'jobId', 'jobKey', 'comparisonDiff')}
            provenance.update(comparisonBase=BASE, changedPaths=['M\tdocs/guide.md'])
            (root/'candidate-provenance.json').write_text(json.dumps(provenance))
            c.validate_candidate_evidence(root, policy)
            for changed in ('M\0packages/Fixture.cs\0', 'M\0unknown-input.xyz\0'):
                policy['comparisonDiff'] = changed
                provenance['comparisonDiff'] = changed
                (root/'candidate-provenance.json').write_text(json.dumps(provenance))
                with self.assertRaises(RuntimeError):
                    c.validate_candidate_evidence(root, policy)
            policy['comparisonDiff'] = 'M\0docs/guide.md\0'
            with self.assertRaisesRegex(RuntimeError, 'source diff'):
                c.validate_candidate_evidence(root, policy)


class ImmutableCompletion(unittest.TestCase):
    def test_real_pre_exit_validator_refuses_missing_or_tampered_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); inputs = root/'inputs'; inputs.mkdir()
            gate = root/'gate'; (gate/'out').mkdir(parents=True)
            sources = {}
            for name in ('api', 'platform', 'quality', 'control'):
                repo = gate/name; repo.mkdir()
                c.command(['git', 'init', '-q', str(repo)])
                (repo/'fixture.txt').write_text('committed fixture\n')
                c.git(repo, 'add', '.')
                c.git(repo, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'fixture')
                sources[name] = {'head': c.git(repo, 'rev-parse', 'HEAD')}
            head = sources['api']['head']; tree = c.git(gate/'api', 'rev-parse', 'HEAD^{tree}')
            (inputs/'sources.json').write_text(json.dumps(sources))
            policy = {'version': 1, 'runId': '42', 'head': head, 'tree': tree, 'sourcesSha256': c.digest(inputs/'sources.json')}
            (inputs/'policy.json').write_text(json.dumps(policy))
            receipt = {'schemaVersion': 1, 'repository': 'harborline-api', 'lane': 'all',
                       'baseHead': head, 'testedTree': tree, 'hostBaseline': 'eng/baselines/host-test-baseline.ubuntu.json',
                       'coverage': 'none', 'steps': ['boundaries', 'dependency-ledger', 'identity-r3', 'codegen-check',
                       'codegen-guard-suite', 'contracts-typescript', 'contracts-csharp', 'localfirst-csharp',
                       'rule-engine-conformance', 'contracts-rust', 'operator-cli-headless', 'install-artefact',
                       'removal-exercise', 'exact-clone', {'id': 'quality', 'decisionDigest': 'sha256:'+'1'*64,
                       'policyDigest': 'sha256:'+'2'*64}, 'quality-baseline', 'packages']}
            receipt_path = gate/'out/harborline-api-verify-receipt.json'
            receipt_path.write_text(json.dumps(receipt))
            (gate/'out/gate-exit.txt').write_text('0\n')
            (gate/'out/harborline-api-quality-decision.json').write_text(json.dumps({'decisionId': 'sha256:'+'1'*64, 'policyDigest': 'sha256:'+'2'*64}))
            script = Path(c.__file__).with_name('finish-gate.py')
            program = ('import importlib.util,sys; from pathlib import Path; '
                       'spec=importlib.util.spec_from_file_location("immutable_finish",sys.argv[1]); '
                       'm=importlib.util.module_from_spec(spec); spec.loader.exec_module(m); '
                       'm.finish(Path(sys.argv[2]),Path(sys.argv[3]),{})')
            def execute():
                return subprocess.run([sys.executable, '-I', '-c', program, str(script), str(inputs), str(gate)],
                                      capture_output=True, text=True, timeout=30)
            result = execute()
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('MINI_IMMUTABLE_EVIDENCE_VALIDATED', result.stdout)
            marker = gate/'out/immutable-validation.json'
            self.assertEqual(json.loads(marker.read_text())['head'], head)
            for kind in ('missing-receipt', 'missing-step', 'stale-tree', 'quality-mismatch', 'dirty-api', 'dirty-platform', 'dirty-quality', 'dirty-control'):
                marker.unlink(missing_ok=True)
                for name in ('api', 'platform', 'quality', 'control'):
                    (gate/name/'fixture.txt').write_text('committed fixture\n')
                altered = copy.deepcopy(receipt)
                if kind == 'missing-step':
                    altered['steps'].remove('exact-clone')
                elif kind == 'stale-tree':
                    altered['testedTree'] = '0'*40
                elif kind == 'quality-mismatch':
                    altered['steps'][14]['decisionDigest'] = 'sha256:'+'3'*64
                receipt_path.write_text(json.dumps(altered))
                if kind == 'missing-receipt':
                    receipt_path.unlink()
                if kind.startswith('dirty-'):
                    (gate/kind.removeprefix('dirty-')/'fixture.txt').write_text('uncommitted change\n')
                result = execute()
                with self.subTest(kind=kind):
                    self.assertNotEqual(result.returncode, 0)
                    self.assertNotIn('MINI_IMMUTABLE_EVIDENCE_VALIDATED', result.stdout)
                    self.assertFalse(marker.exists())


class FocusedRuntimeMeasurement(unittest.TestCase):
    def test_actual_runtime_and_feed_bytes_are_measured_independently(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); inputs = root/'inputs'; inputs.mkdir()
            gate = root/'gate'; feed = gate/'api/.feed'; feed.mkdir(parents=True)
            policy = {'runId': '42', 'jobId': '73', 'head': MERGE}
            (inputs/'policy.json').write_text(json.dumps(policy))
            (feed/'fixture.nupkg').write_bytes(b'package bytes\n')
            (feed/'packed-version.props').write_bytes(b'<Project/>\n')
            actual = c.measure_focused_runtime(inputs, gate)
            # Literal fixture digest for ordered [filename, SHA256(bytes)] JSON.
            self.assertEqual(actual['dependencies'], 'sha256:fccc77e428d4d08dcc7ae5c453925c47575a57b926bca92aa0dc3e76a262a4b6')
            import socket, platform
            arch = {'aarch64': 'arm64', 'arm64': 'arm64', 'x86_64': 'x64', 'AMD64': 'x64'}[platform.machine()]
            self.assertEqual(actual['host'], socket.gethostname()+'/'+sys.platform+'/'+arch)
            c.validate_runtime_anchor(actual, policy, c.digest(inputs/'policy.json'))
            for key in ('runId', 'jobId', 'head', 'policySha256'):
                with self.subTest(binding=key), self.assertRaisesRegex(RuntimeError, 'policy mismatch'):
                    c.validate_runtime_anchor({**actual, key: 'wrong'}, policy, c.digest(inputs/'policy.json'))
            for bad in (None, {}):
                with self.assertRaises(RuntimeError):
                    c.validate_runtime_anchor(bad, policy, c.digest(inputs/'policy.json'))
            (feed/'fixture.nupkg').write_bytes(b'changed package bytes\n')
            self.assertNotEqual(c.measure_focused_runtime(inputs, gate)['dependencies'], actual['dependencies'])
            (feed/'fixture.nupkg').unlink()
            with self.assertRaisesRegex(RuntimeError, 'feed absent'):
                c.measure_focused_runtime(inputs, gate)
