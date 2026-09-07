#!/usr/bin/env node

// Windows cannot spawn the npm/npx/pnpm `.cmd` shims without `shell: true`, and
// `shell: true` changes argument-quoting semantics inside the exact code the gate
// receipt attests. Instead, resolve those commands to their JavaScript entry points
// and run them through the current Node host: spawn semantics stay identical on
// every platform, and recorded command arrays keep the logical name ('npm', ...)
// because callers translate only at the spawn call itself. Non-Windows platforms
// and every other executable pass through untouched.

import {existsSync} from 'node:fs'
import {delimiter, dirname, join} from 'node:path'

const isWindows = process.platform === 'win32'
const cache = new Map()

function shimDirectory(name) {
  for (const directory of (process.env.PATH ?? '').split(delimiter)) {
    if (directory && existsSync(join(directory, `${name}.cmd`))) return directory
  }
  return null
}

function npmCliScript() {
  // npm_execpath is set when this process was itself launched via `npm run`.
  const fromEnvironment = process.env.npm_execpath
  if (fromEnvironment && fromEnvironment.endsWith('npm-cli.js') && existsSync(fromEnvironment)) return fromEnvironment
  // Prefer the PATH shim's npm (what typing `npm` runs) over Node's bundled copy.
  const candidates = []
  const shim = shimDirectory('npm')
  if (shim) candidates.push(join(shim, 'node_modules', 'npm', 'bin', 'npm-cli.js'))
  candidates.push(join(dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js'))
  return candidates.find(candidate => existsSync(candidate)) ?? null
}

function entryScript(name) {
  if (name === 'npm') return npmCliScript()
  if (name === 'npx') {
    const npmCli = npmCliScript()
    if (npmCli) {
      const npxCli = join(dirname(npmCli), 'npx-cli.js')
      if (existsSync(npxCli)) return npxCli
    }
    return null
  }
  if (name === 'pnpm') {
    const shim = shimDirectory('pnpm')
    if (shim) {
      const cli = join(shim, 'node_modules', 'pnpm', 'bin', 'pnpm.cjs')
      if (existsSync(cli)) return cli
    }
    return null
  }
  return null
}

export function resolveCommand(executable, args) {
  if (!isWindows || !['npm', 'npx', 'pnpm'].includes(executable)) return {executable, args}
  if (!cache.has(executable)) cache.set(executable, entryScript(executable))
  const script = cache.get(executable)
  if (!script) throw new Error(`unable to resolve ${executable} to a JavaScript entry point on Windows`)
  return {executable: process.execPath, args: [script, ...args]}
}
