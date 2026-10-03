#!/usr/bin/env node
// Compare downloaded observations, never execute them or accept them as trusted receipts.
import {readFileSync, readdirSync, writeFileSync, statSync} from 'node:fs'
import path from 'node:path'
import {compareInputs, fingerprint} from './validation-reuse.mjs'
import {hostLanes, artifactLane} from './validation-lanes.mjs'

export function compareObservations(current, prior) {
  const problems = []
  for (const [name, item] of [['current', current], ['prior', prior]]) {
    try {
      if (!item?.inputs || item.fingerprint !== fingerprint(item.inputs)) problems.push(`${name} observation missing or corrupt`)
    } catch {problems.push(`${name} observation missing or corrupt`)}
  }
  if (problems.length) return {sameInputs: false, completeInputs: false, problems,
    mode: 'shadow', trusted: false, wouldReuse: false, reuseAuthorized: false, requiredWorkSkipped: false}
  return {...compareInputs(current.inputs, prior.inputs), candidateSha: current.candidateSha,
    priorCandidateSha: prior.candidateSha, mode: 'shadow', trusted: false,
    wouldReuse: false, reuseAuthorized: false, requiredWorkSkipped: false,
    trustBlocker: 'unsigned branch-produced observation; authenticated isolated producer required'}
}

export function compareDirectories(currentDirectory, priorDirectory) {
  const observations = directory => {
    const found = new Map()
    const problems = []
    const visit = root => {
      for (const entry of readdirSync(root, {withFileTypes: true})) {
        if (entry.isSymbolicLink()) continue
        const file = path.join(root, entry.name)
        if (entry.isDirectory()) visit(file)
        else if (entry.name === 'validation-inputs-shadow.json') {
          const lane = artifactLane(path.relative(directory, file).split(path.sep)[0])
          if (!lane) {problems.push('unrecognized host artifact directory'); continue}
          if (found.has(lane)) throw new Error('duplicate lane observations')
          try {
            if (statSync(file).size > 32 * 1024 * 1024) throw new Error('observation exceeds size bound')
            found.set(lane, JSON.parse(readFileSync(file, 'utf8')))
          } catch {found.set(lane, undefined); problems.push(`${lane} observation unreadable or malformed`)}
        }
      }
    }
    visit(directory)
    return {found, problems}
  }
  const current = observations(currentDirectory), prior = observations(priorDirectory)
  const lanes = hostLanes.map(lane => ({lane,
    currentEvidencePresent: Boolean(current.found.get(lane)?.inputs), priorEvidencePresent: Boolean(prior.found.get(lane)?.inputs),
    ...compareObservations(current.found.get(lane), prior.found.get(lane))}))
  const evidenceComplete = lanes.every(lane => lane.currentEvidencePresent && lane.priorEvidencePresent
    && lane.problems.every(problem => !problem.includes('observation missing or corrupt')))
    && current.problems.length === 0 && prior.problems.length === 0
  return {schemaVersion: 1, mode: 'shadow', reuseAuthorized: false, requiredWorkSkipped: false,
    evidenceState: evidenceComplete ? 'present' : 'unknown', currentProblems: current.problems, priorProblems: prior.problems, lanes}
}

if (import.meta.main) {
  const [currentDirectory, priorDirectory, output] = process.argv.slice(2)
  if (!currentDirectory || !priorDirectory || !output) throw new Error('usage: validation-shadow-report.mjs <current> <prior> <output>')
  const result = compareDirectories(currentDirectory, priorDirectory)
  writeFileSync(output, JSON.stringify(result, null, 2) + '\n')
  console.log(`validation shadow: ${result.lanes.length} expected lanes; evidence ${result.evidenceState}; no work skipped`)
}
