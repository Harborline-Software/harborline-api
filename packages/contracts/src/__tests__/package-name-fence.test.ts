import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

const repositoryRoot = fileURLToPath(new URL('../../../../', import.meta.url))
const expectedPackageName = '@harborline-software/api-contracts'
const retiredPackageName = ['@harborline-software', 'contracts'].join('/')

describe('API contracts package-name fence', () => {
  it('keeps the API package on its collision-free name', () => {
    const packageJson = JSON.parse(
      readFileSync(resolve(repositoryRoot, 'packages/contracts/package.json'), 'utf8'),
    ) as { name?: string }

    expect(packageJson.name).toBe(expectedPackageName)
  })

  it('keeps the retired API package name out of repository consumers', () => {
    // One git grep over TRACKED files (apps, packages, eng minus the frozen baselines) instead of a
    // recursive read of every file: the walk took 5.8 s under load and timed out a landing gate on
    // 2026-09-04. git grep -I skips binaries; exit status 1 means "no match".
    let output = ''
    try {
      output = execFileSync(
        'git',
        ['grep', '-I', '-l', '--fixed-strings', '--', retiredPackageName, 'apps', 'packages', 'eng', ':!eng/baselines'],
        { cwd: repositoryRoot, encoding: 'utf8' },
      )
    } catch (error) {
      const failure = error as { status?: number; stdout?: string; stderr?: string }
      if (failure.status !== 1) throw error
      output = failure.stdout ?? ''
    }
    const violations = output.split('\n').map(line => line.trim()).filter(Boolean)

    expect(violations).toEqual([])
  }, 60_000)
})
