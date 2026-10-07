"""Choose a required Linux route before execution; never fall back after failure."""
import hashlib
import json
import os
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import controller as c


def choose(env, event, api=c.api):
    c.require(env.get('GITHUB_REPOSITORY_ID') == '1360432948'
              and env.get('GITHUB_REPOSITORY') == 'Harborline-Software/harborline-api', 'Wrong repository')
    kind = env['GITHUB_EVENT_NAME']
    c.require(kind in ('pull_request', 'merge_group', 'schedule', 'workflow_dispatch'), 'Unsupported event')
    c.require(re.fullmatch('[0-9a-f]{40}', env['GITHUB_SHA']), 'Invalid tested head')
    decision = {'runId': env['GITHUB_RUN_ID'], 'attempt': env['GITHUB_RUN_ATTEMPT'],
                'event': kind, 'testedHead': env['GITHUB_SHA'], 'route': 'hosted', 'candidate': None}
    def hosted(reason):
        return {**decision, 'reason': reason}
    if kind in ('schedule', 'workflow_dispatch'):
        return hosted('scheduled-or-manual-hosted-route')
    c.require(event['repository']['id'] == 1360432948 and event['repository']['fork'] is False, 'Wrong event repository')
    if env['GITHUB_ACTOR_ID'] != '1328090' or env['GITHUB_ACTOR'] != 'ctwoodwa':
        return hosted('actor-outside-qualified-mini-scope')
    # Original actor is stable across reruns; a different rerun initiator must
    # never turn an originally mini-eligible event into hosted fallback.
    c.require(env['GITHUB_RUN_ATTEMPT'] == '1', 'Owner candidate events require a newly reviewed first attempt')
    c.require(env['GITHUB_TRIGGERING_ACTOR'] == 'ctwoodwa', 'Candidate triggering actor changed')
    c.require(event['sender']['id'] == 1328090 and event['sender']['login'] == 'ctwoodwa', 'Event sender mismatch')
    if kind == 'pull_request':
        pr = event['pull_request']
        if (pr['draft'] or any(label['name'] == 'stacked' for label in pr['labels'])
                or pr['head']['repo']['id'] != 1360432948 or pr['head']['repo']['fork'] is True
                or pr['base']['ref'] != 'main' or pr['user']['id'] != 1328090
                or pr['user']['login'] != 'ctwoodwa'):
            return hosted('pr-outside-qualified-mini-scope')
        number, head, base = pr['number'], env['GITHUB_SHA'], pr['base']['sha']
        c.require(event['number'] == number and env['GITHUB_REF'] == f'refs/pull/{number}/merge', 'PR event binding mismatch')
        pr_merge = api('git/ref/pull/'+str(number)+'/merge')['object']['sha']
        c.require(pr_merge == head, 'PR merge replaced during selection')
        branch = pr['head']['ref']
    else:
        group = event['merge_group']
        c.require(event['action'] == 'checks_requested' and group['head_sha'] == env['GITHUB_SHA']
                  and group['head_ref'] == env['GITHUB_REF'] and group['base_ref'] == 'refs/heads/main', 'Group event binding mismatch')
        match = re.fullmatch(r'refs/heads/gh-readonly-queue/main/pr-([1-9][0-9]*)-([0-9a-f]{40})', group['head_ref'])
        c.require(match is not None, 'Unrecognized merge-group ref; queue identity is unverified')
        number, head, base = int(match[1]), group['head_sha'], group['base_sha']
        c.require(match[2] == base, 'Group ref/base mismatch')
        pr = api('pulls/'+str(number))
        c.require(pr['number'] == number, 'Queue PR number mismatch')
        if (pr['head']['repo']['id'] != 1360432948 or pr['head']['repo']['fork'] is True
                or pr['user']['id'] != 1328090 or pr['user']['login'] != 'ctwoodwa'):
            return hosted('group-pr-outside-qualified-mini-scope')
        pr_merge = api('git/ref/pull/'+str(number)+'/merge')['object']['sha']
        branch = group['head_ref'].removeprefix('refs/heads/')
    c.require(pr['state'] == 'open' and pr['draft'] is False and pr['base']['sha'] == base, 'Candidate no longer ready')
    candidate = {'required': True, 'event': kind, 'prNumber': number, 'prHead': pr['head']['sha'],
                 'prMerge': pr_merge, 'head': head, 'base': base, 'headBranch': branch,
                 'ref': env['GITHUB_REF'], 'workflowHead': head,
                 'workflowRef': 'Harborline-Software/harborline-api/.github/workflows/verify.yml@'+env['GITHUB_REF']}
    # GITHUB_TOKEN cannot read the merge-queue GraphQL resource. REST proves
    # the exact one-PR synthetic tree here; this only selects a provisional lane.
    # Host admission still requires complete queue membership before credentials.
    # Unknown batch/parent/tree shapes are red, never inferred as hosted fallback.
    c.candidate_rest_state(candidate)
    return {**decision, 'route': 'mini', 'reason': 'exact-qualified-candidate', 'candidate': candidate}


def accepts(route, results, *, native_policy="required"):
    """Required aggregate truth table, including no skipped-mini acceptance."""
    common = ('verify-route', 'verify-perf-hosted')
    if route not in ('mini', 'hosted') or any(results.get(name) != 'success' for name in common):
        return False
    selected, other = ('verify-mini', 'verify-linux') if route == 'mini' else ('verify-linux', 'verify-mini')
    if results.get(selected) != 'success' or results.get(other) != 'skipped':
        return False
    if results.get('verify-shared') != ('skipped' if route == 'mini' else 'success'):
        return False
    if results.get('verify-windows') != 'skipped':
        return False
    if native_policy == 'development-suspended':
        return (results.get('verify-windows-hosted') == 'skipped'
                and results.get('verify-macos') == 'skipped')
    return (native_policy == 'required' and results.get('verify-windows-hosted') == 'success'
            and results.get('verify-macos') == 'success')


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'aggregate':
        needs = json.loads(os.environ['NEEDS'])
        result = accepts(os.environ['LINUX_ROUTE'], {name: value['result'] for name, value in needs.items()},
                         native_policy=os.environ['NATIVE_POLICY'])
        c.require(os.environ.get('DRAFT') != 'true' and result, 'A selected required lane did not succeed')
    else:
        decision = choose(os.environ, json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text()))
        digest = hashlib.sha256(json.dumps(decision, sort_keys=True).encode()).hexdigest()
        with Path(os.environ['GITHUB_OUTPUT']).open('a') as output:
            output.write('route='+decision['route']+'\n')
            output.write('decision-digest='+digest+'\n')
        print('MINI_ROUTE_DECISION '+json.dumps(decision, sort_keys=True), flush=True)
