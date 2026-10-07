"""Literal identity oracles; real Git history; independent refusal properties."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import admission
import controller as c

BASE = 'd33d2f291e0abedea74153f7297d198a77874a95'
PR_HEAD = '81217b25705fd42a5a956d5e01eae3ee47206f9f'
MERGE = 'f9d43ee0fc5b1d7e6d831800b29ce4833b704c3d'
GROUP = 'e04c7c3493a10517d6d74605ba26f929fe7b570b'
TREE = '8908ac7c0f9d45b7b3133a3057ed4cbd8e869fbf'
REPOSITORY = {'id': 1360432948, 'full_name': 'Harborline-Software/harborline-api', 'fork': False}
OWNER = {'id': 1328090, 'login': 'ctwoodwa'}
PR = {'number': 380, 'state': 'open', 'draft': False, 'user': OWNER,
      'head': {'sha': PR_HEAD, 'ref': 'pipeline/mini-fixture', 'repo': REPOSITORY},
      'base': {'sha': BASE, 'ref': 'main', 'repo': REPOSITORY}}
JOBS = [{'name': 'portable', 'status': 'queued', 'runner_id': 0,
         'labels': ['self-hosted', 'harborline-api-mini-candidate-v2', 'linux-arm64-orbstack', 'lane-a']}]


def fixture(event):
    head = MERGE if event == 'pull_request' else GROUP
    branch = 'pipeline/mini-fixture' if event == 'pull_request' else 'gh-readonly-queue/main/pr-380-'+BASE
    ref = 'refs/pull/380/merge' if event == 'pull_request' else 'refs/heads/'+branch
    candidate = {'event': event, 'prNumber': 380, 'prHead': PR_HEAD, 'prMerge': MERGE, 'head': head,
                 'base': BASE, 'headBranch': branch, 'ref': ref, 'workflowHead': head,
                 'workflowRef': 'Harborline-Software/harborline-api/.github/workflows/mini-candidate-gate.yml@'+ref}
    policy = {'version': 2, 'runId': '42', 'head': head, 'candidate': candidate, 'coverage': event == 'merge_group'}
    env = {'GITHUB_EVENT_NAME': event, 'GITHUB_REPOSITORY': 'Harborline-Software/harborline-api',
           'GITHUB_REPOSITORY_ID': '1360432948', 'GITHUB_REF': ref,
           'GITHUB_ACTOR': 'ctwoodwa', 'GITHUB_ACTOR_ID': '1328090', 'GITHUB_TRIGGERING_ACTOR': 'ctwoodwa',
           'GITHUB_JOB': 'portable', 'GITHUB_WORKFLOW_REF': candidate['workflowRef'],
           'GITHUB_SHA': head, 'GITHUB_WORKFLOW_SHA': head, 'GITHUB_RUN_ID': '42', 'GITHUB_RUN_ATTEMPT': '1'}
    payload = {'repository': copy.deepcopy(REPOSITORY), 'sender': copy.deepcopy(OWNER)}
    if event == 'pull_request':
        env.update(GITHUB_BASE_REF='main', GITHUB_HEAD_REF='pipeline/mini-fixture')
        payload.update(action='synchronize', number=380, pull_request=copy.deepcopy(PR))
    else:
        payload.update(action='checks_requested', merge_group={'head_sha': head, 'head_ref': ref,
                                                              'base_sha': BASE, 'base_ref': 'refs/heads/main'})
    run = {'id': 42, 'repository': REPOSITORY, 'head_repository': REPOSITORY,
           'event': event, 'run_attempt': 1, 'head_branch': branch,
           'head_sha': PR_HEAD if event == 'pull_request' else GROUP,
           'path': '.github/workflows/mini-candidate-gate.yml', 'status': 'queued', 'conclusion': None,
           'actor': OWNER, 'triggering_actor': OWNER}
    return candidate, policy, env, payload, run


def leaves(value, prefix=()):
    for key, item in value.items():
        if isinstance(item, dict):
            yield from leaves(item, (*prefix, key))
        else:
            yield (*prefix, key)


class CandidateHook(unittest.TestCase):
    def test_both_literal_events_and_every_runtime_binding(self):
        for event in ('pull_request', 'merge_group'):
            candidate, policy, env, payload, _ = fixture(event)
            self.assertTrue(admission.admitted(policy, env, payload))
            for key in env:
                with self.subTest(event=event, env=key):
                    self.assertFalse(admission.admitted(policy, {**env, key: 'wrong'}, payload))
            for path in leaves(payload):
                altered = copy.deepcopy(payload)
                target = altered
                for part in path[:-1]:
                    target = target[part]
                target.pop(path[-1])
                # Full repo names in nested PR objects are informational; IDs
                # and fork bits provide the separately checked identity there.
                if path[-1] == 'full_name' and len(path) > 2:
                    continue
                if path == ('pull_request', 'base', 'repo', 'fork'):
                    continue
                with self.subTest(event=event, missing=path):
                    self.assertFalse(admission.admitted(policy, env, altered))
            for key in candidate:
                changed = copy.deepcopy(policy)
                changed['candidate'].pop(key)
                self.assertFalse(admission.admitted(changed, env, payload), key)

    def test_fork_bot_wrong_event_inputs_and_wrong_author_refuse(self):
        _, policy, env, event, _ = fixture('pull_request')
        for path, value in [(('repository', 'fork'), True), (('pull_request', 'draft'), True),
                            (('pull_request', 'head', 'repo', 'fork'), True),
                            (('pull_request', 'user', 'id'), 1), (('sender', 'login'), 'github-actions[bot]')]:
            altered = copy.deepcopy(event)
            target = altered
            for part in path[:-1]:
                target = target[part]
            target[path[-1]] = value
            self.assertFalse(admission.admitted(policy, env, altered))
        for key in ('workflow_run', 'inputs', 'merge_group'):
            self.assertFalse(admission.admitted(policy, env, {**event, key: {}}))


class CandidateHost(unittest.TestCase):
    def test_run_binds_pr_source_head_distinct_from_tested_head(self):
        for event in ('pull_request', 'merge_group'):
            candidate, policy, _, _, run = fixture(event)
            self.assertEqual(c.validate_candidate_run(run, JOBS, candidate, b'reviewed', b'reviewed'), policy)
            for key, value in [('event', 'push'), ('head_sha', BASE), ('head_branch', 'other'),
                               ('path', '.github/workflows/verify.yml'), ('run_attempt', 2),
                               ('actor', {'id': 1, 'login': 'ctwoodwa'}), ('triggering_actor', {'id': 1328090, 'login': 'other'}),
                               ('repository', {'id': 1}), ('head_repository', {'id': 1}), ('conclusion', 'success')]:
                with self.subTest(event=event, binding=key), self.assertRaises(RuntimeError):
                    c.validate_candidate_run({**run, key: value}, JOBS, candidate, b'reviewed', b'reviewed')
            for jobs in ([], JOBS+JOBS, [{**JOBS[0], 'runner_id': 7}], [{**JOBS[0], 'labels': ['self-hosted']}],
                         [{**JOBS[0], 'name': 'other'}], [{**JOBS[0], 'status': 'in_progress'}]):
                with self.assertRaises(RuntimeError):
                    c.validate_candidate_run(run, jobs, candidate, b'reviewed', b'reviewed')
            with self.assertRaises(RuntimeError):
                c.validate_candidate_run(run, JOBS, candidate, b'candidate-changed-workflow', b'reviewed')

    def state_fixture(self, event):
        candidate, *_ = fixture(event)
        records = {'pulls/380': copy.deepcopy(PR), 'git/ref/heads/main': {'object': {'sha': BASE}},
                   'git/ref/pull/380/merge': {'object': {'sha': MERGE}},
                   'git/commits/'+MERGE: {'sha': MERGE, 'parents': [{'sha': BASE}, {'sha': PR_HEAD}], 'tree': {'sha': TREE}},
                   'git/commits/'+GROUP: {'sha': GROUP, 'parents': [{'sha': BASE}], 'tree': {'sha': TREE}},
                   'git/ref/heads/gh-readonly-queue/main/pr-380-'+BASE: {'object': {'sha': GROUP}}}
        entry = {'position': 1, 'baseCommit': {'oid': BASE}, 'headCommit': {'oid': GROUP},
                 'mergeQueue': {'entries': {'pageInfo': {'hasNextPage': False}, 'nodes': [
                     {'pullRequest': {'number': 380, 'headRefOid': PR_HEAD}, 'headCommit': {'oid': GROUP}}]}}}
        return candidate, records, entry

    def evaluate(self, candidate, records, entry, *, allow_landed=False):
        data = {'data': {'repository': {'pullRequest': {'mergeQueueEntry': entry}}}}
        with patch.object(c, 'api', side_effect=lambda path: records[path]), patch.object(c, 'command', return_value=json.dumps(data)):
            return c.candidate_state(candidate, allow_landed=allow_landed)

    def test_exact_landed_group_only_allowed_at_terminal_check(self):
        candidate, records, entry = self.state_fixture('merge_group')
        records['pulls/380'].update(state='closed', merged=True, merge_commit_sha=GROUP)
        records['git/ref/heads/main']['object']['sha'] = GROUP
        del records['git/ref/heads/gh-readonly-queue/main/pr-380-'+BASE]
        del records['git/ref/pull/380/merge']
        self.assertEqual(self.evaluate(candidate, records, None, allow_landed=True), TREE)
        with self.assertRaises(RuntimeError):
            self.evaluate(candidate, records, None)
        for mutation in ('other-main', 'unmerged', 'different-merge', 'different-pr-head', 'different-tree', 'different-parent'):
            altered = copy.deepcopy(records)
            if mutation == 'other-main':
                altered['git/ref/heads/main']['object']['sha'] = MERGE
            elif mutation == 'unmerged':
                altered['pulls/380']['merged'] = False
            elif mutation == 'different-merge':
                altered['pulls/380']['merge_commit_sha'] = MERGE
            elif mutation == 'different-pr-head':
                altered['pulls/380']['head']['sha'] = MERGE
            elif mutation == 'different-tree':
                altered['git/commits/'+GROUP]['tree']['sha'] = BASE
            else:
                altered['git/commits/'+GROUP]['parents'] = [{'sha': PR_HEAD}]
            with self.subTest(mutation=mutation), self.assertRaises(RuntimeError):
                self.evaluate(candidate, altered, None, allow_landed=True)

    def test_live_state_stale_head_base_merge_and_group_composition(self):
        for event in ('pull_request', 'merge_group'):
            candidate, records, entry = self.state_fixture(event)
            self.assertEqual(self.evaluate(candidate, records, entry), TREE)
            for path in ('git/ref/heads/main', 'git/ref/pull/380/merge'):
                altered = copy.deepcopy(records)
                altered[path]['object']['sha'] = GROUP
                with self.subTest(event=event, stale=path), self.assertRaises((RuntimeError, KeyError)):
                    self.evaluate(candidate, altered, entry)
            for side in ('head', 'base'):
                altered = copy.deepcopy(records)
                altered['pulls/380'][side]['sha'] = '0'*40
                with self.assertRaises(RuntimeError):
                    self.evaluate(candidate, altered, entry)
        candidate, records, entry = self.state_fixture('merge_group')
        variants = [None, {**entry, 'position': 2}, {**entry, 'baseCommit': {'oid': PR_HEAD}},
                    {**entry, 'headCommit': {'oid': MERGE}}]
        extra = copy.deepcopy(entry)
        extra['mergeQueue']['entries']['nodes'].append({'pullRequest': {'number': 381, 'headRefOid': BASE}, 'headCommit': {'oid': GROUP}})
        variants.append(extra)
        partial = copy.deepcopy(entry)
        partial['mergeQueue']['entries']['pageInfo']['hasNextPage'] = True
        variants.append(partial)
        for changed in variants:
            with self.assertRaises(RuntimeError):
                self.evaluate(candidate, records, changed)
        altered = copy.deepcopy(records)
        altered['git/commits/'+GROUP]['tree']['sha'] = '0'*40
        with self.assertRaises(RuntimeError):
            self.evaluate(candidate, altered, entry)


class ComparisonHistory(unittest.TestCase):
    def test_real_bundles_preserve_changed_code_and_select_both_modes(self):
        spec = importlib.util.spec_from_file_location('checkout_sources', Path(c.__file__).with_name('checkout-sources.py'))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = root/'source'; repo.mkdir()
            c.command(['git', 'init', '-q', str(repo)])
            def git(*args):
                return c.git(repo, *args)
            file = repo/'packages/Fixture.cs'; file.parent.mkdir()
            file.write_text('public class Fixture { public int Value => 42; }\n')
            def commit():
                git('add', '.')
                git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'literal fixture')
                return git('rev-parse', 'HEAD')
            base = commit()
            file.write_text('public class Fixture { public int Value => 43; }\n')
            head = commit()
            inputs = root/'inputs'; (inputs/'bundles').mkdir(parents=True)
            sources = {}
            for name in ('api', 'platform', 'quality', 'control'):
                bundle = inputs/'bundles'/(name+'.bundle')
                git('bundle', 'create', str(bundle), 'HEAD')
                sources[name] = {'head': head, 'bundleSha256': c.digest(bundle)}
            sources['api']['comparisonBase'] = base
            (inputs/'sources.json').write_text(json.dumps(sources))
            policy = {'head': head, 'tree': git('rev-parse', 'HEAD^{tree}'), 'runId': '42', 'coverage': False,
                      'sourcesSha256': c.digest(inputs/'sources.json'), 'candidate': {'head': head, 'base': base}}
            (inputs/'policy.json').write_text(json.dumps(policy))
            output = root/'restored'; (output/'out').mkdir(parents=True)
            module.restore(inputs, output, {'HARBORLINE_GATE_COVERAGE': '0'})
            self.assertEqual(c.git(output/'api', 'rev-parse', 'origin/main'), base)
            self.assertEqual(c.git(output/'api', 'diff', '--name-status', 'origin/main', 'HEAD'), 'M\tpackages/Fixture.cs')
            self.assertEqual(json.loads((output/'out/candidate-provenance.json').read_text())['changedPaths'], ['M\tpackages/Fixture.cs'])
            selector = Path(c.__file__).parents[1]/'focused-mode-policy.mjs'
            program = ("import {execFileSync} from 'node:child_process'; import {classifyFocusedModes,parseChangedFiles} from "
                       +json.dumps(selector.as_uri())+"; const raw=execFileSync('git',['-C',process.argv[1],'diff','--name-status','-z','origin/main','HEAD'],{encoding:'utf8'}); console.log(JSON.stringify(classifyFocusedModes(parseChangedFiles(raw)).requiredModes));")
            modes = json.loads(c.command(['node', '--input-type=module', '-e', program, str(output/'api')]))
            self.assertEqual(modes, ['coverage-off', 'coverage-on'])
            # The old HEAD-as-main defect actually changes the selector to no work.
            c.git(output/'api', 'update-ref', 'refs/remotes/origin/main', head)
            self.assertEqual(json.loads(c.command(['node', '--input-type=module', '-e', program, str(output/'api')])), [])
            for key, value in [('comparisonBase', '0'*40), ('comparisonBase', head)]:
                sources['api'][key] = value
                (inputs/'sources.json').write_text(json.dumps(sources))
                policy['sourcesSha256'] = c.digest(inputs/'sources.json')
                (inputs/'policy.json').write_text(json.dumps(policy))
                bad = root/('bad-'+value[:8]); (bad/'out').mkdir(parents=True)
                with self.assertRaises(RuntimeError):
                    module.restore(inputs, bad, {'HARBORLINE_GATE_COVERAGE': '0'})


class CandidateEvidence(unittest.TestCase):
    def test_descriptor_and_completed_modes_are_required(self):
        _, policy, *_ = fixture('pull_request')
        policy.update(tree=TREE, sdk='11.0.100-rc.1.26425.128', sourcesSha256='a'*64)
        names = [
            'ck-10 fence: no production code runs its own loop over the ADR 0038 stage order',
            'ck-10 fence: only the executor calls a KernelWrite stage',
            'ck-10 fence: every admitted-write commit is a KernelWrite commit stage or a reviewed not-yet-moved path',
            'ck-10 fence: the record writer commits only from its KernelWrite commit stages, EF saves included',
            'ck-10 fence: a planted bypass outside the writer is caught by every check',
        ]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            provenance = {k: policy[k] for k in ('runId', 'head', 'tree', 'candidate', 'coverage', 'sourcesSha256')}
            provenance.update(comparisonBase=BASE, changedPaths=['M\teng/mini-runner/controller.py'])
            def save_provenance(value):
                (root/'candidate-provenance.json').write_text(json.dumps(value))
            save_provenance(provenance)
            with self.assertRaisesRegex(RuntimeError, 'receipts absent'):
                c.validate_candidate_evidence(root, policy)
            directory = root/'gate-evidence/focused-modes/run-fixture'
            (directory/'coverage-on').mkdir(parents=True)
            xml = directory/'coverage-on/focused.cobertura.xml'
            xml.write_text('<coverage><packages><package><classes><class filename="fixture.cs"><lines><line number="1" hits="1"/></lines></class></classes></package></packages></coverage>')
            def hash_json(value):
                return 'sha256:'+hashlib.sha256(json.dumps(value, separators=(',', ':')).encode()).hexdigest()
            plan = {'schemaVersion': 1, 'diffAvailable': True,
                    'changes': [{'path': 'eng/mini-runner/controller.py', 'status': 'M', 'kind': 'build-artifact'}],
                    'requiredModes': ['coverage-off', 'coverage-on'], 'selection': 'focused'}
            plan['planDigest'] = hash_json(plan)
            scope = {'id': 'compiled-write-fences-v1', 'project': 'apps/local-node-host/tests/tests.csproj',
                     'filter': 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.ArchTests.WritePipelineExecutorFenceTests',
                     'requiredTests': names}
            expected = {'commit': MERGE, 'tree': TREE, 'sdk': '11.0.100-rc.1.26425.128',
                        'host': 'fixture/linux/arm64', 'run': 'run-fixture', 'dependencies': 'sha256:'+'b'*64}
            rows = []
            for mode in ('coverage-off', 'coverage-on'):
                rows.append({'mode': mode, **expected, 'schemaVersion': 1, 'scopeDigest': hash_json(scope), 'planDigest': plan['planDigest'],
                             'executed': True, 'exitCode': 0, 'counts': {'total': 5, 'passed': 5, 'failed': 0, 'notExecuted': 0},
                             'results': [{'testName': name, 'rosterId': name, 'outcome': 'Passed'} for i, name in enumerate(names)],
                             'coverage': {'validLines': 1, 'coveredLines': 1, 'paths': ['fixture.cs']} if mode == 'coverage-on' else None,
                             'coverageDigest': 'sha256:'+c.digest(xml) if mode == 'coverage-on' else None})
                import xml.etree.ElementTree as ET
                trx = ET.Element('TestRun'); results = ET.SubElement(trx, 'Results')
                for name in names:
                    ET.SubElement(results, 'UnitTestResult', testName=name, outcome='Passed')
                ET.SubElement(ET.SubElement(trx, 'ResultSummary'), 'Counters', total='5', passed='5', failed='0', notExecuted='0')
                (directory/mode).mkdir(exist_ok=True)
                ET.ElementTree(trx).write(directory/mode/'focused.trx', encoding='unicode')
            record = {'plan': plan, 'expected': expected, 'receipts': rows}
            def save_receipts(value):
                (directory/'receipts.json').write_text(json.dumps(value))
            save_receipts(record)
            c.validate_candidate_evidence(root, policy)
            for key in provenance:
                altered = copy.deepcopy(provenance)
                altered.pop(key)
                save_provenance(altered)
                with self.subTest(omitted=key), self.assertRaises(RuntimeError):
                    c.validate_candidate_evidence(root, policy)
            save_provenance(provenance)
            for key, value in [('commit', BASE), ('tree', '0'*40), ('sdk', '10.0.401'), ('executed', False),
                               ('exitCode', 1), ('results', []), ('coverageDigest', None), ('host', 'other/linux/arm64'),
                               ('dependencies', 'sha256:'+'c'*64), ('run', 'stale-run'), ('scopeDigest', 'sha256:'+'c'*64),
                               ('planDigest', 'sha256:'+'c'*64)]:
                altered = copy.deepcopy(record)
                altered['receipts'][1][key] = value
                save_receipts(altered)
                with self.subTest(binding=key), self.assertRaises(RuntimeError):
                    c.validate_candidate_evidence(root, policy)
            for omitted in (0, 1):
                altered = copy.deepcopy(record)
                altered['receipts'].pop(omitted)
                save_receipts(altered)
                with self.assertRaises(RuntimeError):
                    c.validate_candidate_evidence(root, policy)
            different_roster = copy.deepcopy(record)
            different_roster['receipts'][1]['results'][0]['rosterId'] = 'another identity'
            save_receipts(different_roster)
            with self.assertRaises(RuntimeError):
                c.validate_candidate_evidence(root, policy)
            save_receipts(record)
            trx = directory/'coverage-off/focused.trx'
            saved_trx = trx.read_text()
            trx.unlink()
            with self.assertRaises(RuntimeError):
                c.validate_candidate_evidence(root, policy)
            trx.write_text(saved_trx.replace('outcome="Passed"', 'outcome="Failed"', 1))
            with self.assertRaises(RuntimeError):
                c.validate_candidate_evidence(root, policy)
            trx.write_text(saved_trx)
            save_receipts(record)
            xml.write_text('changed')
            with self.assertRaisesRegex(RuntimeError, 'coverage missing or changed'):
                c.validate_candidate_evidence(root, policy)
            record['receipts'][1]['coverageDigest'] = 'sha256:'+c.digest(xml)
            save_receipts(record)
            with self.assertRaises(ET.ParseError):
                c.validate_candidate_evidence(root, policy)


class CandidateLifecycle(unittest.TestCase):
    def test_supersession_at_each_activation_boundary_cleans_up(self):
        import argparse
        from types import SimpleNamespace
        for point in ('after-preflight', 'after-registration', 'during-execution', 'completion'):
            with self.subTest(point=point), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                _, policy, *_ = fixture('pull_request')
                policy.update(tree=TREE, sdk='11.0.100-rc.1.26425.128')
                (root/'policy.json').write_text(json.dumps(policy)); (root/'heavy.lock').touch()
                args = argparse.Namespace(policy=str(root/'policy.json'), lock=str(root/'heavy.lock'),
                                          output=str(root/'evidence'), image='sha256:'+'a'*64)
                sequence = [policy, policy, policy]
                if point == 'after-preflight':
                    sequence[1] = RuntimeError('candidate superseded')
                elif point == 'after-registration':
                    sequence[2] = RuntimeError('candidate superseded')
                def api(path, method='GET'):
                    if method == 'POST':
                        return {'token': 'fixture-only'}
                    return {'run_attempt': 1, 'head_sha': PR_HEAD,
                            'status': 'completed' if point == 'completion' else 'in_progress', 'conclusion': 'success'}
                def docker(*args, **kwargs):
                    return '2.338.0' if args[-1] == '--version' else ''
                with patch.object(c, 'pending', side_effect=sequence), patch.object(c, 'preflight_image', return_value={}), \
                        patch.object(c, 'docker', side_effect=docker), patch.object(c, 'api', side_effect=api) as api_call, \
                        patch.object(c, 'candidate_state', side_effect=RuntimeError('candidate superseded')), \
                        patch.object(c, 'candidate_pending', side_effect=RuntimeError('candidate superseded')), \
                        patch.object(c.uuid, 'uuid4', return_value=SimpleNamespace(hex='a'*32)), \
                        patch.object(c, 'cleanup', return_value={'clean': True}) as cleanup:
                    with self.assertRaisesRegex(RuntimeError, 'candidate superseded'):
                        c.execute(args)
                    cleanup.assert_called_once_with('a'*32)
                    if point == 'after-preflight':
                        api_call.assert_not_called()
                self.assertFalse(json.loads((root/'evidence/result.json').read_text())['success'])
                fd = c.reserve(root/'heavy.lock'); c.os.close(fd)
