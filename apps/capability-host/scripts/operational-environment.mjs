const LEGACY_PREFIX = 'HULL_'
const CAPABILITY_HOST_PREFIX = 'CAPABILITY_HOST_'

/** Refuse stale capability-host configuration instead of silently ignoring it. */
export function assertNoLegacyOperationalVariables(env = process.env) {
  const legacyName = Object.keys(env)
    .filter((name) => name.startsWith(LEGACY_PREFIX))
    .sort()[0]
  if (legacyName == null) return

  const replacement = CAPABILITY_HOST_PREFIX + legacyName.slice(LEGACY_PREFIX.length)
  throw new Error(
    `Legacy capability-host environment variable ${legacyName} is not supported; use ${replacement}.`,
  )
}
