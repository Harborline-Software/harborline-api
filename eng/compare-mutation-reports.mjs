#!/usr/bin/env node
// node eng/compare-mutation-reports.mjs <reference-dir> <candidate-dir> [--out <file.json>]
//
// mac16 qualification (control docs/workload-routing.md): compares two Stryker.NET full-mutation
// runs of the SAME commit, slice, SDK and Platform pin, one on the qualified route (winbox) and one on
// the candidate (mac16). Each directory is an unpacked `stryker-full-*` / `mutation-qualification-*`
// artifact; every mutation-report.json under it is read, keyed by its report folder (the project).
//
// Passing totals are not enough (an early-return test can hide a host path), so it compares:
//   - the mutant set: file, mutator, replacement and exact span must match one-for-one
//   - each mutant's status; Killed<->Timeout is listed as a timing difference, anything else is real
//   - the test names each side discovered
// Exit 0 only when sets match and there are no real status differences. Timing differences are
// reported for the owner to judge; they never pass silently.
import {readdirSync, readFileSync, statSync, writeFileSync} from 'node:fs'
import path from 'node:path'

function reports(dir) {
  const found = new Map()
  const walk = d => {
    for (const name of readdirSync(d)) {
      const p = path.join(d, name)
      if (statSync(p).isDirectory()) walk(p)
      else if (name === 'mutation-report.json') {
        // .../<project>/reports/mutation-report.json -> <project>
        const project = path.basename(path.dirname(path.dirname(p)))
        if (found.has(project)) throw new Error(`two reports for ${project} under ${dir}`)
        found.set(project, JSON.parse(readFileSync(p, 'utf8')))
      }
    }
  }
  walk(dir)
  if (found.size === 0) throw new Error(`no mutation-report.json under ${dir}`)
  return found
}

const span = l => `${l.start.line}:${l.start.column}-${l.end.line}:${l.end.column}`
function mutants(report) {
  const out = new Map()
  for (const [file, f] of Object.entries(report.files ?? {})) {
    const norm = file.replaceAll('\\', '/')
    for (const m of f.mutants ?? []) {
      const key = `${norm}|${m.mutatorName}|${m.replacement ?? ''}|${span(m.location)}`
      if (out.has(key)) out.set(key, out.get(key) + '+' + m.status) // identical mutants: compare as a multiset
      else out.set(key, m.status)
    }
  }
  return out
}
function testNames(report) {
  const names = new Set()
  for (const tf of Object.values(report.testFiles ?? {})) for (const t of tf.tests ?? []) names.add(t.name)
  return names
}
const DETECTED = new Set(['Killed', 'Timeout'])
function score(ms) {
  let detected = 0, valid = 0
  for (const s of ms.values()) for (const one of s.split('+')) {
    if (['CompileError', 'RuntimeError', 'Ignored', 'Pending'].includes(one)) continue
    valid++
    if (DETECTED.has(one)) detected++
  }
  return valid ? Math.round((detected / valid) * 10000) / 100 : null
}

export function compare(refDir, candDir) {
  const ref = reports(refDir), cand = reports(candDir)
  const result = {projects: [], pass: true}
  for (const project of new Set([...ref.keys(), ...cand.keys()])) {
    const row = {project, missing: null, onlyReference: [], onlyCandidate: [], timing: [], real: [], testsOnlyReference: [], testsOnlyCandidate: []}
    if (!ref.has(project) || !cand.has(project)) {
      row.missing = ref.has(project) ? 'candidate' : 'reference'
      result.pass = false
      result.projects.push(row)
      continue
    }
    const a = mutants(ref.get(project)), b = mutants(cand.get(project))
    for (const [k, s] of a) {
      if (!b.has(k)) row.onlyReference.push(k)
      else if (b.get(k) !== s) {
        const pair = [s, b.get(k)].sort().join('/')
        ;(pair === 'Killed/Timeout' ? row.timing : row.real).push({mutant: k, reference: s, candidate: b.get(k)})
      }
    }
    for (const k of b.keys()) if (!a.has(k)) row.onlyCandidate.push(k)
    const ta = testNames(ref.get(project)), tb = testNames(cand.get(project))
    row.testsOnlyReference = [...ta].filter(n => !tb.has(n))
    row.testsOnlyCandidate = [...tb].filter(n => !ta.has(n))
    row.mutants = {reference: a.size, candidate: b.size}
    row.score = {reference: score(a), candidate: score(b)}
    if (row.onlyReference.length || row.onlyCandidate.length || row.real.length ||
        row.testsOnlyReference.length || row.testsOnlyCandidate.length) result.pass = false
    result.projects.push(row)
  }
  return result
}

if (process.argv[1] && process.argv[1].replaceAll('\\', '/').endsWith('eng/compare-mutation-reports.mjs')) {
  const args = process.argv.slice(2)
  const outAt = args.indexOf('--out')
  const out = outAt >= 0 ? args.splice(outAt, 2)[1] : null
  if (args.length !== 2) { console.error('usage: compare-mutation-reports.mjs <reference-dir> <candidate-dir> [--out file.json]'); process.exit(2) }
  const r = compare(args[0], args[1])
  for (const p of r.projects) {
    if (p.missing) { console.log(`${p.project}: report missing on ${p.missing}`); continue }
    console.log(`${p.project}: mutants ${p.mutants.reference}/${p.mutants.candidate}, score ${p.score.reference}/${p.score.candidate}, ` +
      `only-ref ${p.onlyReference.length}, only-cand ${p.onlyCandidate.length}, real ${p.real.length}, timing ${p.timing.length}, ` +
      `tests only-ref ${p.testsOnlyReference.length}, only-cand ${p.testsOnlyCandidate.length}`)
  }
  console.log(r.pass ? 'MATCH (review any timing rows)' : 'DIFFERENT')
  if (out) writeFileSync(out, JSON.stringify(r, null, 2) + '\n')
  process.exit(r.pass ? 0 : 1)
}
