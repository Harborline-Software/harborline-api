#!/usr/bin/env node
// Roslyn's compiler ErrorLog includes assembly-level diagnostics that have no
// physical location, while CQG intentionally refuses such unanchored findings.
// Keep ErrorLog's original path, retain every anchored result, and add a stable
// partial fingerprint when the compiler did not supply one.
import {createHash} from 'node:crypto'
import {readFileSync, writeFileSync} from 'node:fs'

const files = process.argv.slice(2)
if (!files.length) throw new Error('usage: normalize-roslyn-sarif.mjs <sarif-file> [sarif-file...]')

const fingerprint = result => {
  const physical = result.locations[0].physicalLocation
  const location = physical.artifactLocation.uri
  const region = physical.region
  return 'sha256:' + createHash('sha256').update(JSON.stringify([
    result.ruleId,
    location,
    region.startLine,
    region.startColumn ?? null,
    region.endLine ?? null,
    region.endColumn ?? null,
  ])).digest('hex')
}

for (const file of files) {
  const sarif = JSON.parse(readFileSync(file, 'utf8'))
  if (sarif.version !== '2.1.0' || !Array.isArray(sarif.runs)) throw new Error('expected SARIF 2.1.0')
  for (const run of sarif.runs) {
    if (!Array.isArray(run.results)) throw new Error('SARIF run has no results array')
    run.results = run.results.filter(result => {
      const physical = result.locations?.[0]?.physicalLocation
      return typeof result.ruleId === 'string'
        && typeof physical?.artifactLocation?.uri === 'string'
        && Number.isInteger(physical?.region?.startLine)
        && physical.region.startLine > 0
    })
    for (const result of run.results) {
      if (!result.partialFingerprints || typeof result.partialFingerprints !== 'object'
        || Object.keys(result.partialFingerprints).length === 0) {
        result.partialFingerprints = {'harborline/primary-location/v1': fingerprint(result)}
      }
    }
  }
  writeFileSync(file, JSON.stringify(sarif) + '\n')
}
