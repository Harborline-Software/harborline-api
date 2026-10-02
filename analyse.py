"""Summarise a Stryker.NET mutation-report.json: per file, each mutant's line, mutator, status, replacement and
the names of the tests that killed it. Usage: python analyse.py <report.json> [<file substring>...]"""
import json
import sys

report = json.load(open(sys.argv[1], encoding='utf-8'))
wanted = sys.argv[2:]

tests = {}
for test_file in (report.get('testFiles') or {}).values():
    for test in test_file.get('tests', []):
        tests[test['id']] = test.get('name', test['id'])

for path, info in report['files'].items():
    if wanted and not any(w in path for w in wanted):
        continue
    mutants = info['mutants']
    counts = {}
    for m in mutants:
        counts[m['status']] = counts.get(m['status'], 0) + 1
    tested = counts.get('Killed', 0) + counts.get('Survived', 0) + counts.get('Timeout', 0)
    score = 100 * (counts.get('Killed', 0) + counts.get('Timeout', 0)) / tested if tested else 0
    print(f'== {path}\n   {counts}  score over tested {score:.2f}')
    source = info.get('source', '').split('\n')
    for m in sorted(mutants, key=lambda m: (m['location']['start']['line'], m['mutatorName'])):
        line = m['location']['start']['line']
        text = source[line - 1].strip()[:90] if line - 1 < len(source) else ''
        killers = [tests.get(t, t) for t in (m.get('killedBy') or [])][:2]
        tail = ('  killed by: ' + '; '.join(killers)) if killers else ''
        print(f'   {line:4} {m["status"]:<10} {m["mutatorName"]:<22} -> {str(m.get("replacement", ""))[:50]!r}  | {text}{tail}')
