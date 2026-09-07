/**
 * The Negotiate face (ADR 0124 Part II + Part IV.5).
 *
 * Connect-time contract-version + per-capability schema-version handshake, so
 * the .NET / Rust / Python / remote runtimes do NOT need a lockstep release.
 * Part of the v0 minimum-viable membrane. TYPES ONLY.
 */

import type { CapabilityId } from './common.js'

/** The membrane contract version a runtime speaks (semver). */
export type ContractVersion = string

/**
 * A runtime's connect-time announcement of which contract version it speaks and
 * which capability schema-versions it hosts (Negotiate). The shell reconciles
 * this against its own supported set; no lockstep-release requirement.
 */
export interface NegotiateOffer {
  /** The membrane contract version this runtime implements. */
  contractVersion: ContractVersion
  /** Per-capability schema-version the runtime hosts (capabilityId → semver). */
  capabilitySchemaVersions: Record<CapabilityId, string>
}

/** The shell's reconciliation of a `NegotiateOffer`. */
export interface NegotiateResult {
  /** Whether the runtime's contract version is compatible with the shell. */
  compatible: boolean
  /** The contract version the two sides agreed on. */
  agreedContractVersion: ContractVersion
  /** Capabilities the shell will route to this runtime (intersection). */
  acceptedCapabilities: CapabilityId[]
  /** Human-readable note when `compatible` is false (display string). */
  reason?: string | null
}

// ---------------------------------------------------------------------------
// Observe — tri-state health (ADR 0124 Part IV.6 / Part V)
// ---------------------------------------------------------------------------

/**
 * Tri-state health (ADR 0124 Part IV.6). Degraded-but-alive (SQLITE_BUSY, GPU
 * throttle, mesh flap) is the COMMON case and must be representable — a binary
 * up/down loses it.
 */
export type HealthState = 'up' | 'degraded' | 'down'

/**
 * A health probe result (ADR 0124 Part V — the Observe face). The three probe
 * KINDS (`startup`/`liveness`/`readiness`) have distinct consequences; this is
 * the result shape they share.
 */
export interface HealthProbe {
  /** Which probe produced this result. */
  kind: 'startup' | 'liveness' | 'readiness'
  /** Tri-state health. */
  state: HealthState
  /** Human-readable detail (display string). */
  detail?: string | null
}
