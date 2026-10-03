#!/usr/bin/env node
// Compare downloaded observations, never execute them or accept them as trusted receipts.
import {readFileSync, readdirSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {compareInputs, fingerprint} from './validation-reuse.mjs'

export function compareObservations(current, prior) {
  for (const [name, item] of [['current', current], ['prior', prior]]) {
    if (!item?.inputs || item.fingerprint !== fingerprint(item.inputs))
      return {sameInputs: false, completeInputs: false, problems: [`${name} observation missing or corrupt`],
        mode: 'shadow', trusted: false, wouldReuse: false, reuseAuthorized: false, requiredWorkSkipped: false}
  }
  return {...compareInputs(current.inputs, prior.inputs), candidateSha: current.candidateSha,
    priorCandidateSha: prior.candidateSha, mode: 'shadow', trusted: false,
    wouldReuse: false, reuseAuthorized: false, requiredWorkSkipped: false,
    trustBlocker: 'unsigned branch-produced observation; authenticated isolated producer required'}
}

if (import.meta.main) {
  const [currentDirectory, priorDirectory, output] = process.argv.slice(2)
  if (!currentDirectory || !priorDirectory || !output) throw new Error('usage: validation-shadow-report.mjs <current> <prior> <output>')
  const observations = directory => {
    const found = new Map()
    const visit = root => {
      for (const entry of readdirSync(root, {withFileTypes: true})) {
        if (entry.isSymbolicLink()) continue
        const file = path.join(root, entry.name)
        if (entry.isDirectory()) visit(file)
        else if (entry.name === 'validation-inputs-shadow.json') {
          const item = JSON.parse(readFileSync(file, 'utf8'))
          // Directory artifact names identify lanes even when an observation failed collection.
          const lane = path.relative(directory, file).split(path.sep)[0]
          if (found.has(lane)) throw new Error('duplicate lane observations')
          found.set(lane, item)
        }
      }
    }
    visit(directory)
    return found
  }
  const current = observations(currentDirectory), prior = observations(priorDirectory)
  const result = {schemaVersion: 1, mode: 'shadow', reuseAuthorized: false,
    lanes: [...current].map(([lane, item]) => ({lane, ...compareObservations(item, prior.get(lane))}))}
  writeFileSync(output, JSON.stringify(result, null, 2) + '\n')
  console.log(`validation shadow: ${result.lanes.length} lanes observed; no work skipped`)
}
