/**
 * The Negotiate face (ADR 0124 Part II + Part IV.5) — connect-time
 * contract-version + per-capability schema-version handshake, so the
 * .NET / Rust / Python / remote runtimes do NOT need a lockstep release.
 *
 * The CONTRACT types (`ContractVersion`, `NegotiateOffer`, `NegotiateResult`)
 * are owned by @harborline-software/api-contracts (the single source, X-1). This module
 * re-exports them and adds the SHELL-SIDE reconciliation BEHAVIOUR (the contract
 * is types-only; the membrane owns the logic).
 */

import type {
  ContractVersion,
  NegotiateOffer,
  NegotiateResult,
  CapabilityId,
} from '@harborline-software/api-contracts'

export type { ContractVersion, NegotiateOffer, NegotiateResult }

/**
 * The shell's own membrane-contract capabilities: the contract version it
 * implements + the per-capability schema-versions it understands. Reconciled
 * against each runtime's `NegotiateOffer`.
 */
export interface ShellNegotiationProfile {
  /** The membrane contract version the shell implements. */
  contractVersion: ContractVersion
  /** capabilityId → the schema-version the shell supports for it. */
  supportedSchemaVersions: Record<CapabilityId, string>
}

/**
 * Parse a `MAJOR.MINOR.PATCH` semver into a tuple. Returns `null` on a malformed
 * string (Negotiate treats an unparseable version as incompatible — fail-closed).
 */
function parseSemver(v: string): [number, number, number] | null {
  const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(v.trim())
  if (m === null) return null
  return [Number(m[1]), Number(m[2]), Number(m[3])]
}

/**
 * Contract-version compatibility: SAME MAJOR is compatible (semver — a minor/
 * patch bump is additive). Different major, or either side unparseable, is
 * incompatible. This is the "no lockstep release" rule made concrete: a runtime
 * one minor behind the shell still negotiates successfully.
 */
export function isContractVersionCompatible(
  shell: ContractVersion,
  runtime: ContractVersion,
): boolean {
  const s = parseSemver(shell)
  const r = parseSemver(runtime)
  if (s === null || r === null) return false
  return s[0] === r[0]
}

/**
 * Reconcile a runtime's `NegotiateOffer` against the shell's profile (the
 * Negotiate face). Produces the `NegotiateResult`: compatibility, the agreed
 * contract version, and the INTERSECTION of capabilities the shell will route to
 * this runtime (a capability is accepted only when both sides speak it AND agree
 * on its schema major version).
 */
export function negotiate(
  shell: ShellNegotiationProfile,
  offer: NegotiateOffer,
): NegotiateResult {
  const compatible = isContractVersionCompatible(
    shell.contractVersion,
    offer.contractVersion,
  )

  if (!compatible) {
    return {
      compatible: false,
      agreedContractVersion: shell.contractVersion,
      acceptedCapabilities: [],
      reason: `contract-version mismatch: shell ${shell.contractVersion} vs runtime ${offer.contractVersion} (major must match)`,
    }
  }

  const accepted: CapabilityId[] = []
  for (const [capabilityId, runtimeSchema] of Object.entries(
    offer.capabilitySchemaVersions,
  )) {
    const shellSchema = shell.supportedSchemaVersions[capabilityId]
    if (shellSchema === undefined) continue // shell does not speak this capability
    if (schemaMajorMatches(shellSchema, runtimeSchema)) {
      accepted.push(capabilityId)
    }
  }

  return {
    compatible: true,
    agreedContractVersion: shell.contractVersion,
    acceptedCapabilities: accepted,
    reason: null,
  }
}

/** A capability schema is accepted when its MAJOR matches (additive minor/patch). */
function schemaMajorMatches(shellSchema: string, runtimeSchema: string): boolean {
  const s = parseSemver(shellSchema)
  const r = parseSemver(runtimeSchema)
  if (s === null || r === null) return false
  return s[0] === r[0]
}
