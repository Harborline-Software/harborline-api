import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const LEGACY_PREFIX = 'HULL_'
const CAPABILITY_HOST_PREFIX = 'CAPABILITY_HOST_'

/** Refuse stale capability-host configuration instead of silently ignoring it. */
export function assertNoLegacyOperationalVariables(
  env: NodeJS.ProcessEnv = process.env,
): void {
  const legacyName = Object.keys(env)
    .filter((name) => name.startsWith(LEGACY_PREFIX))
    .sort()[0]
  if (legacyName == null) return

  const replacement = CAPABILITY_HOST_PREFIX + legacyName.slice(LEGACY_PREFIX.length)
  throw new Error(
    `Legacy capability-host environment variable ${legacyName} is not supported; use ${replacement}.`,
  )
}

/** Locate the Python guard beside this module in source and packaged layouts. */
export function resolvePythonOperationalEnvironmentHelper(): string | null {
  const here = dirname(fileURLToPath(import.meta.url))
  const candidates = [
    join(here, 'operational_environment.py'),
    join(here, '..', '..', 'src', 'runtime', 'operational_environment.py'),
  ]
  return candidates.find((candidate) => existsSync(candidate)) ?? null
}
