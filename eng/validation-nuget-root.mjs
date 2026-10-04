// Query NuGet itself, including the current checkout's settings. Snapshot fields
// never grant permission to add an arbitrary package root during collection.
import {execFileSync} from 'node:child_process'
import path from 'node:path'

export function establishNuGetRoot({cwd, env = process.env, run = execFileSync}) {
  const output = run('dotnet', ['nuget', 'locals', 'global-packages', '--list', '--force-english-output'],
    {cwd, env, encoding: 'utf8', timeout: 10000, maxBuffer: 1024 * 1024, stdio: 'pipe'})
  const roots = String(output).split(/\r?\n/).flatMap(line => {
    const match = /^\s*(?:info\s*:\s*)?global-packages:\s*(.+?)\s*$/.exec(line)
    return match ? [match[1]] : []
  })
  if (roots.length !== 1 || !path.isAbsolute(roots[0]) || /[\0\r\n]/.test(roots[0]))
    throw new Error('canonical NuGet root unavailable')
  const root = path.resolve(roots[0])
  env.NUGET_PACKAGES = root
  return root
}

// Parent-owned state is passed directly to collection, never inferred from env.
export function observeNuGetRoot(options) {
  try {return {status: 'resolved', root: establishNuGetRoot(options)}}
  catch {return {status: 'unavailable'}}
}

export function nuGetRootApproved(resolution, env) {
  return resolution?.status === 'resolved' && typeof resolution.root === 'string'
    && path.isAbsolute(resolution.root) && typeof env.NUGET_PACKAGES === 'string'
    && path.isAbsolute(env.NUGET_PACKAGES)
    && path.relative(resolution.root, env.NUGET_PACKAGES) === ''
}
