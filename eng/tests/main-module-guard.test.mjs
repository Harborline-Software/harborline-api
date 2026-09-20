// node --test eng/tests/main-module-guard.test.mjs
//
// T-672. Copied from harborline-platform's tooling/tests/main-module-guard.test.mjs and adapted to
// this repository's layout and to the guard idiom the api already had.
//
// Node realpaths the entry module, so `import.meta.url` is the RESOLVED path while `process.argv[1]`
// is the path as typed. A main-module guard that compares the two is FALSE whenever the CLI is
// reached through a symlink or junction: the script body never runs, and it exits 0 in silence. A
// gate step that exits 0 having done nothing reads as ok, so there is no signal at all.
//
// The robust form, already used by eng/verify-receipt.mjs, is a suffix match on the normalised
// argv[1]: a link changes the prefix and never the filename.
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import test from 'node:test'

const repositoryRoot = path.resolve(import.meta.dirname, '../..')

function linkedRepositoryRoot(t) {
  const scratch = mkdtempSync(path.join(tmpdir(), 'main-module-guard-'))
  const link = path.join(scratch, 'repo-link')
  try {
    symlinkSync(repositoryRoot, link, process.platform === 'win32' ? 'junction' : 'dir')
  } catch (error) {
    rmSync(scratch, {recursive: true, force: true})
    if (error.code === 'EPERM' || error.code === 'EACCES') return undefined
    throw error
  }
  t.after(() => rmSync(scratch, {recursive: true, force: true}))
  return link
}

test('a CLI invoked through a linked path still runs its main body, and still refuses bad input', t => {
  const link = linkedRepositoryRoot(t)
  if (!link) return t.skip('symlink creation not permitted on this host')
  // Called with no arguments this CLI must throw its usage error. Under the argv[1] guard it exited
  // 0 in silence instead, which is the whole defect: the caller cannot tell it from success.
  const result = spawnSync(process.execPath, [path.join(link, 'eng/normalize-eslint-sarif.mjs')],
    {cwd: repositoryRoot, encoding: 'utf8'})
  assert.notEqual(result.status, 0, 'CLI exited 0 through the linked path, so its body never ran')
  assert.match(result.stderr, /usage: normalize-eslint-sarif\.mjs/)
})

test('a second CLI reports its usage through a linked path rather than exiting silently', t => {
  const link = linkedRepositoryRoot(t)
  if (!link) return t.skip('symlink creation not permitted on this host')
  const result = spawnSync(process.execPath, [path.join(link, 'eng/affected-tests.mjs')],
    {cwd: repositoryRoot, encoding: 'utf8'})
  assert.notEqual(result.status, 0, 'CLI exited 0 through the linked path, so its body never ran')
  assert.match(result.stderr, /usage: node eng\/affected-tests\.mjs/)
})

test('importing a CLI as a module does not run its main body', async () => {
  // process.argv[1] here is this test file, so the guard must stay false on import.
  const module = await import('../normalize-eslint-sarif.mjs')
  assert.equal(typeof module.normalizeEslintSarifFile, 'function')
})

test('no entry point compares process.argv[1] against import.meta', () => {
  const offenders = []
  const walk = directory => {
    for (const entry of readdirSync(directory, {withFileTypes: true})) {
      if (entry.name === 'node_modules' || entry.name === 'tests') continue
      const full = path.join(directory, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (entry.name.endsWith('.mjs')) {
        for (const line of readFileSync(full, 'utf8').split('\n')) {
          if (line.includes('process.argv[1]') && line.includes('import.meta')) {
            offenders.push(`${path.relative(repositoryRoot, full)}: ${line.trim()}`)
          }
        }
      }
    }
  }
  for (const root of ['eng', 'tooling']) walk(path.join(repositoryRoot, root))
  assert.deepEqual(offenders, [],
    'a guard comparing argv[1] to import.meta is false under a linked path; match the suffix instead, as eng/verify-receipt.mjs does')
})
