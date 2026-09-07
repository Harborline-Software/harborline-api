// Where run-exact-clone.mjs persists its report. Pure so eng/tests/exact-clone-evidence.test.mjs can
// enumerate the four (record, status) cases without cloning anything:
//   --record            -> the committed evidence file (docs/evidence/exact-clone.json), PASS or FAIL
//   no --record, FAIL   -> <apiRoot>/.claude/land-evidence/exact-clone-fail.json (ignored by git; read by
//                          eng/land-evidence.sh before the land worktree is removed)
//   no --record, PASS   -> nothing is written; the tracked tree stays clean
import path from 'node:path'

export const FAIL_EVIDENCE_RELATIVE = '.claude/land-evidence/exact-clone-fail.json'

export function evidenceTarget({record, status, apiRoot, evidencePath}) {
  if (record) return evidencePath
  if (status === 'FAIL') return path.join(apiRoot, FAIL_EVIDENCE_RELATIVE)
  return null
}
