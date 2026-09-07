/** A qualified role vocabulary reference (`vocabulary/name`). */
export type RoleReference = string & { readonly __roleReference: unique symbol }

/** A bare record-derived standing identifier. Standings are facts, not roles. */
export type RecordStandingReference = string & { readonly __recordStandingReference: unique symbol }

const bareIdentifier = /^[A-Za-z][A-Za-z0-9._-]*$/

/** Parses the qualified role lane and refuses blank, bare, or multiply-qualified values. */
export function parseRoleReference(value: unknown, field = 'requiredRoles'): RoleReference {
  if (typeof value !== 'string') {
    throw new TypeError(`authorization.gate_reference.required_roles_invalid: field=${field}; value is not a string`)
  }
  const separator = value.indexOf('/')
  if (separator <= 0 || separator !== value.lastIndexOf('/') || separator === value.length - 1) {
    throw new TypeError(`authorization.gate_reference.required_roles_invalid: field=${field}; value=${value}`)
  }
  return value as RoleReference
}

/** Parses the bare standing lane with the same identifier grammar as the .NET contract. */
export function parseRecordStandingReference(
  value: unknown,
  field = 'requiredStandings',
): RecordStandingReference {
  if (typeof value !== 'string' || !bareIdentifier.test(value)) {
    throw new TypeError(`authorization.gate_reference.required_standings_invalid: field=${field}; value=${String(value)}`)
  }
  return value as RecordStandingReference
}
