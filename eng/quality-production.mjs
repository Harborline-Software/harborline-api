import {createHash} from 'node:crypto'
import {readFileSync, writeFileSync, rmSync} from 'node:fs'
import path from 'node:path'

const manifestPath = root => path.join(root, 'artifacts/quality/production.json')
const digest = file => createHash('sha256').update(readFileSync(file)).digest('hex')
export function invalidateQualityProduction(root) { rmSync(manifestPath(root), {force: true}) }
export function beginQualityProduction(root, files) {
  invalidateQualityProduction(root)
  for (const file of files) rmSync(file, {force: true})
}
export function recordQualityProduction(root, {head, run, files}) {
  if (!files.length) throw new Error('quality production has no SARIF')
  writeFileSync(manifestPath(root), JSON.stringify({schemaVersion: 1, head, run,
    files: files.map(file => ({path: path.relative(root, file).replaceAll('\\', '/'), sha256: digest(file)}))}) + '\n')
}
export function requireQualityProduction(root, {head, run, files}) {
  if (typeof head !== 'string' || !head || typeof run !== 'string' || !run) throw new Error('quality: missing verification HEAD/run identity')
  let record
  try { record = JSON.parse(readFileSync(manifestPath(root), 'utf8')) }
  catch { throw new Error('quality: missing fresh exact-clone production; rerun verification') }
  if (record.schemaVersion !== 1 || record.head !== head || record.run !== run || !Array.isArray(record.files) || !record.files.length) throw new Error('quality: stale exact-clone production; rerun verification')
  const actual = files.map(file => ({path: path.relative(root, file).replaceAll('\\', '/'), sha256: digest(file)}))
  if (JSON.stringify(record.files) !== JSON.stringify(actual)) throw new Error('quality: analyzer outputs changed after exact-clone production; rerun verification')
}
