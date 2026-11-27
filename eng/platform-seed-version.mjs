import {spawnSync} from 'node:child_process'
import path from 'node:path'

export const refusal = 'could not read the platform pack version from the preload service'

// ADR-0097 D6: the platform owns the manifest. Do not parse C# source or duplicate its revision.
export function readPlatformSeedVersion(feed, run = spawnSync) {
  const result = run('dotnet', ['run', '--file', path.join(import.meta.dirname, 'read-platform-seed-version.cs'),
    '--no-launch-profile', '--verbosity', 'quiet',
    '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '--', feed],
  {encoding: 'utf8', env: process.env})
  const versions = (result.stdout ?? '').split(/\r?\n/).filter(line => line.startsWith('platform-seed-version='))
  const version = versions[0]?.slice('platform-seed-version='.length)
  if (result.status !== 0 || versions.length !== 1
    || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(version ?? '')) {
    throw new Error(refusal)
  }
  return version
}

if (import.meta.main) {
  try { console.log(readPlatformSeedVersion(process.argv[2] ?? path.resolve(import.meta.dirname, '../.feed'))) }
  catch { console.error(`removal-exercise: ${refusal}`); process.exitCode = 1 }
}
