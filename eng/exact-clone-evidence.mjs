// Where run-exact-clone.mjs persists its report. Pure so eng/tests/exact-clone-evidence.test.mjs can
// enumerate the four (record, status) cases without cloning anything:
//   --record            -> the committed evidence file (docs/evidence/exact-clone.json), PASS or FAIL
//   no --record, FAIL   -> <apiRoot>/.claude/gate-evidence/exact-clone-fail.json (ignored by git)
//   no --record, PASS   -> nothing is written; the tracked tree stays clean
import path from 'node:path'

export const FAIL_EVIDENCE_RELATIVE = '.claude/gate-evidence/exact-clone-fail.json'

export function evidenceTarget({record, status, apiRoot, evidencePath}) {
  if (record) return evidencePath
  if (status === 'FAIL') return path.join(apiRoot, FAIL_EVIDENCE_RELATIVE)
  return null
}
