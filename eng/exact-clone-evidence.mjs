// Report destination selection stays pure so eng/tests/exact-clone-evidence.test.mjs can
// enumerate the four (record, status) cases without cloning anything:
//   --record            -> the committed evidence file (docs/evidence/exact-clone.json), PASS or FAIL
//   no --record, FAIL   -> <apiRoot>/.claude/gate-evidence/exact-clone-fail.json (ignored by git)
//   no --record, PASS   -> nothing is written; the tracked tree stays clean
import path from 'node:path'
import {mkdirSync, writeFileSync} from 'node:fs'

export const FAIL_EVIDENCE_RELATIVE = '.claude/gate-evidence/exact-clone-fail.json'

export function evidenceTarget({record, status, apiRoot, evidencePath}) {
  if (record) return evidencePath
  if (status === 'FAIL') return path.join(apiRoot, FAIL_EVIDENCE_RELATIVE)
  return null
}

// Retain complete failed-step diagnostics in the existing ignored artifact directory.
// Successful reports keep their previous compact representation and write no output logs.
export function persistStepEvidence({report, apiRoot, redactEvidence}) {
  // An aborted assembly has no authoritative baseline verdict for provisional commands.
  // Preserve the complete captured command chain rather than treating provisional true as success.
  const assemblyAborted = report.steps.some(step => step.id === 'report-assembly' && step.passed === false)
  return {
    ...report,
    steps: report.steps.map(({fullOutput, rawOutput, ...step}) => {
      if (report.status !== 'FAIL' || (!assemblyAborted && step.passed !== false) || typeof fullOutput !== 'string') return step
      const outputFile = path.join('.claude', 'gate-evidence',
        `exact-clone-${encodeURIComponent(report.apiCommit)}-${encodeURIComponent(step.id)}.log`)
      const target = path.join(apiRoot, outputFile)
      mkdirSync(path.dirname(target), {recursive: true})
      writeFileSync(target, redactEvidence(fullOutput))
      return {...step, outputFile}
    }),
  }
}
