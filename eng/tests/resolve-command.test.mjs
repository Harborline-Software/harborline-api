import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdirSync, mkdtempSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {resolveCommand} from '../lib/resolve-command.mjs'

test('Windows resolves pnpm/action-setup shim to its JavaScript entry point', () => {
  if (process.platform !== 'win32') return
  const root = mkdtempSync(path.join(tmpdir(), 'pnpm-action-layout-'))
  const shim = path.join(root, 'node_modules', '.bin')
  const cli = path.join(root, 'node_modules', 'pnpm', 'bin', 'pnpm.cjs')
  const originalPath = process.env.PATH
  try {
    mkdirSync(shim, {recursive: true})
    mkdirSync(path.dirname(cli), {recursive: true})
    writeFileSync(path.join(shim, 'pnpm.cmd'), '')
    writeFileSync(cli, '')
    process.env.PATH = `${shim}${path.delimiter}${originalPath}`
    const command = resolveCommand('pnpm', ['install'])
    assert.equal(command.executable, process.execPath)
    assert.deepEqual(command.args, [cli, 'install'])
  } finally {
    process.env.PATH = originalPath
    rmSync(root, {recursive: true, force: true})
  }
})
