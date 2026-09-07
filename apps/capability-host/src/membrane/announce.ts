/**
 * The Announce face (ADR 0124 Part II — control plane) — a runtime declaring,
 * at connect time, which capabilities it hosts and which provider-manifests back
 * them. This is the SHELL side of the seam: the shell CONSUMES a runtime-manifest
 * the runtime publishes (the runtime side mints it; see `runtime/`).
 *
 * Registry-tier metadata per ADR 0124 Part III: the Announce payload is the
 * lightweight routing metadata the Resolve pipeline reads — it is NOT the Invoke
 * data plane. Per ADR 0123 manifest-v2 the provider descriptors are the
 * `ProviderManifest` type from @harborline-software/api-contracts (the single source).
 *
 * TYPES ONLY (the wire that carries them is the transport in `loopback-transport`).
 */

import type { CapabilityId, ProviderManifest } from '@harborline-software/api-contracts'

import type { ContractVersion } from './negotiate.js'

/**
 * One capability a runtime hosts, with the provider-manifests that realize it.
 * `schemaVersion` is the per-capability schema-version the runtime speaks (the
 * value Negotiate reconciles against the shell's supported set).
 */
export interface AnnouncedCapability {
  /** The stable capability this runtime hosts (the membrane routes on this). */
  capabilityId: CapabilityId
  /** The per-capability schema-version the runtime hosts (semver). */
  schemaVersion: string
  /**
   * The provider-manifests (ADR 0123 manifest-v2) that realize this capability
   * inside the runtime. The single source is @harborline-software/api-contracts `ProviderManifest`;
   * Announce carries them verbatim — it does not redefine the manifest shape.
   */
  providers: ProviderManifest[]
}

/**
 * A runtime's connect-time announcement (the Announce face payload). The runtime
 * declares its identity, the membrane contract version it speaks, and the
 * capabilities + provider-manifests it hosts. The shell ingests this to populate
 * its routing registry (Registry tier) before Negotiate.
 */
export interface RuntimeManifest {
  /** Stable runtime identity (used in OTel resource attrs + addressing). */
  runtimeId: string
  /** Human-readable runtime name (display). */
  name: string
  /** The membrane contract version this runtime implements (Negotiate reconciles it). */
  contractVersion: ContractVersion
  /** The capabilities this runtime hosts, each with its backing provider-manifests. */
  capabilities: AnnouncedCapability[]
}

/**
 * The shell-side digest of an ingested `RuntimeManifest`: a flat lookup from
 * `capabilityId` → the announced capability, so the Resolve pipeline can ask
 * "does any connected runtime host capability X, and with which providers?"
 * without re-walking every runtime manifest.
 */
export interface RuntimeRegistryEntry {
  /** The runtime that published the manifest. */
  runtimeId: string
  /** The membrane contract version the runtime announced. */
  contractVersion: ContractVersion
  /** capabilityId → the announced capability hosted by this runtime. */
  byCapability: ReadonlyMap<CapabilityId, AnnouncedCapability>
}

/** Build a shell-side registry entry from an ingested runtime manifest. */
export function indexRuntimeManifest(manifest: RuntimeManifest): RuntimeRegistryEntry {
  const byCapability = new Map<CapabilityId, AnnouncedCapability>()
  for (const cap of manifest.capabilities) {
    byCapability.set(cap.capabilityId, cap)
  }
  return {
    runtimeId: manifest.runtimeId,
    contractVersion: manifest.contractVersion,
    byCapability,
  }
}
