// Per-mutant summary of a Stryker.NET json report: counts per mutated file, then every non-Ignored,
// non-CompileError mutant with id, line:col, mutator, status and the killing tests' names.
import {readFileSync} from 'node:fs'
const [file, lineFilter] = process.argv.slice(2)
const r = JSON.parse(readFileSync(file, 'utf8'))
const names = {}
for (const tf of Object.values(r.testFiles ?? {})) for (const t of tf.tests ?? []) names[t.id] = t.name
for (const [f, {mutants = []}] of Object.entries(r.files)) {
  const live = mutants.filter(m => m.status !== 'Ignored' && m.status !== 'CompileError')
  if (!live.length) continue
  const c = {}; for (const m of mutants) c[m.status] = (c[m.status] ?? 0) + 1
  const det = (c.Killed ?? 0) + (c.Timeout ?? 0), val = det + (c.Survived ?? 0) + (c.NoCoverage ?? 0)
  console.log(`# ${f.replaceAll(String.fromCharCode(92), '/').replace(/.*r4-t984api\//, '')} ${JSON.stringify(c)} score ${(100 * det / val).toFixed(2)}`)
  for (const m of live.sort((a, b) => a.location.start.line - b.location.start.line)) {
    if (lineFilter && !lineFilter.split(',').includes(String(m.location.start.line))) continue
    const k = (m.killedBy ?? []).map(id => names[id] ?? id)
    console.log(`${m.id}\t${m.location.start.line}:${m.location.start.column}\t${m.status}\t${m.mutatorName}\t${String(m.replacement ?? '').replace(/\s+/g, ' ').slice(0, 70)}\t${k.slice(0, 2).join(' ; ')}${k.length > 2 ? ` (+${k.length - 2})` : ''}`)
  }
}
