// Build-time observation only; candidate execution cannot mint a trusted attestation.
import {readFileSync, writeFileSync, existsSync, realpathSync} from 'node:fs'
import path from 'node:path'
import {digest} from './validation-reuse.mjs'
import {readCompilerObservation} from './validation-compiler-inputs.mjs'

export function compilerRoots(source, env = process.env) {
  const roots = [{name: 'source', directory: source}]
  for (const [name, variable] of [['packages', 'NUGET_PACKAGES'], ['sdk', 'DOTNET_ROOT'],
    ['platform', 'HARBORLINE_PLATFORM_REPO'], ['quality', 'HARBORLINE_QUALITY_REPO'], ['control', 'HARBORLINE_CONTROL_REPO']])
    if (env[variable]) roots.push({name, directory: env[variable]})
  return roots.sort((a, b) => b.directory.length - a.directory.length)
}

export function beforeCompile({argsFile, source, captureSession, env}) {
  if (!/^[0-9a-f-]{36}$/.test(captureSession ?? '')) throw new Error('capture session missing')
  const roots = compilerRoots(source, env)
  const files = []
  const names = readFileSync(argsFile.replace(/\.args$/, '.inputs'), 'utf8').replace(/^\uFEFF/, '').split(/\r?\n/).filter(Boolean)
  for (const name of names) {
    const absolute = path.resolve(name)
    const root = roots.find(item => {
      const relative = path.relative(path.resolve(item.directory), absolute)
      if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) return false
      if (!existsSync(absolute)) return false
      const physical = path.relative(realpathSync(item.directory), realpathSync(absolute))
      return physical !== '..' && !physical.startsWith(`..${path.sep}`) && !path.isAbsolute(physical)
    })
    if (!root) throw new Error('compiler input unavailable or outside approved roots')
    files.push({identity: `${root.name}/${path.relative(root.directory, absolute).replaceAll('\\', '/')}`,
      sha256: digest(readFileSync(absolute))})
  }
  writeFileSync(argsFile.replace(/\.args$/, '.before'), JSON.stringify({captureSession, files}))
}

export function afterCompile({argsFile, source, captureSession, env}) {
  const before = JSON.parse(readFileSync(argsFile.replace(/\.args$/, '.before'), 'utf8'))
  const observation = readCompilerObservation({argsFile, roots: compilerRoots(source, env), capturePhase: true})
  const beforeMatches = before.captureSession === captureSession && observation.completeCompilerObservation
    && observation.files.every(file => before.files.some(prior => prior.identity === file.identity && prior.sha256 === file.sha256))
  const snapshot = {schemaVersion: 1, captureSession, beforeMatches,
    argsDigest: digest(readFileSync(argsFile)), contextDigest: digest(readFileSync(argsFile.replace(/\.args$/, '.context'))),
    files: observation.files}
  writeFileSync(argsFile.replace(/\.args$/, '.snapshot'), JSON.stringify(snapshot))
  return snapshot
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  const [phase, argsFile, source, captureSession] = process.argv.slice(2)
  try {
    if (phase === 'before') beforeCompile({argsFile, source, captureSession})
    else if (phase === 'after') afterCompile({argsFile, source, captureSession})
    else throw new Error('unknown capture phase')
  } catch {
    // Observation failure cannot change the build gate result; absence remains incomplete.
    console.error('compiler input snapshot unavailable; no completeness claim')
  }
}
