#!/usr/bin/env node
// known-tests-drift.mjs <known-tests-candidate.json>
//
// Report-only (T-724 ruling 119e). run-exact-clone.mjs writes a knownTests CANDIDATE on every run,
// pass or fail, from what it actually observed running -- this script compares that candidate
// against each baseline's COMMITTED knownTests roster and reports drift, by name. It never writes
// anything: the fix is `node eng/run-exact-clone.mjs --write-known-tests`, reviewed and committed
// like any other baseline change. A scheduled/nightly job runs this after its own exact-clone step
// so a missed refresh (someone added or removed a test and forgot to repin) is caught the next
// night instead of silently leaving the roster stale, which would otherwise sit unnoticed until
// the next intentional edit gave it a reason to be touched.
import {readFileSync} from 'node:fs'
import path from 'node:path'

const [candidatePath] = process.argv.slice(2)
if (!candidatePath) {
  console.error('usage: known-tests-drift.mjs <known-tests-candidate.json>')
  process.exit(2)
}
// Resolved against the CWD, like every other eng/*.mjs invoked as `node eng/<script>.mjs` from the
// repo root -- not against this script's own location, so a fixture can point it at a scratch tree.
const apiRoot = process.cwd()
const candidate = JSON.parse(readFileSync(candidatePath, 'utf8'))

let drifted = false
for (const lane of ['host', 'capability']) {
  const {baseline: relative, identities: observed} = candidate[lane]
  const committed = JSON.parse(readFileSync(path.join(apiRoot, relative), 'utf8'))
  const known = committed.knownTests ?? []
  if (known.length === 0) {
    console.log(`known-tests-drift: ${relative} has no committed roster yet; run --write-known-tests to populate it (not drift)`)
    continue
  }
  const observedSet = new Set(observed)
  const knownSet = new Set(known)
  const added = observed.filter(name => !knownSet.has(name))
  const removed = known.filter(name => !observedSet.has(name))
  if (added.length || removed.length) {
    drifted = true
    console.log(`known-tests-drift: ${relative} is stale (${added.length} added, ${removed.length} removed since the last refresh):`)
    for (const name of added) console.log(`  + ${name}`)
    for (const name of removed) console.log(`  - ${name}`)
  } else {
    console.log(`known-tests-drift: ${relative} matches this run (${known.length} identities)`)
  }
}
process.exit(drifted ? 1 : 0)
