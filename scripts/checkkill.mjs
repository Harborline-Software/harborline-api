// node checkkill.mjs report "id:TestNameFragment" ... -> whether each named test is among the mutant's killers
import {readFileSync} from 'node:fs'
const [file, ...pairs] = process.argv.slice(2)
const r = JSON.parse(readFileSync(file, 'utf8'))
const names = {}; for (const tf of Object.values(r.testFiles ?? {})) for (const t of tf.tests ?? []) names[t.id] = t.name
const all = Object.values(r.files).flatMap(f => f.mutants)
for (const p of pairs) { const [id, frag] = p.split(':'); const m = all.find(x => x.id === id)
  const k = (m.killedBy ?? []).map(t => names[t] ?? t); const cov = (m.coveredBy ?? []).length
  console.log(`${id} L${m.location.start.line} ${m.status} ${frag}: ${k.some(n => n.includes(frag)) ? 'YES' : 'NO'} (${k.length}/${cov} covering tests kill)`) }
