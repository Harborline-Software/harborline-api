/**
 * Provider manifest v2 + two-layer license gate (ADR 0123 §S2/§S4/§S6).
 *
 * v2 EXTENDS the shipped flight-deck manifest v1
 * (`flight-deck/packages/plugins/manifest-schema.json`) — same field names
 * (`name`, not `displayName`; `kind`; `capabilities[]`; `flavor`; `hardware{}`),
 * adding: the two-layer license components (S4, retiring the single-string
 * `license`), packaging/signing, the provider input-schema ref (S3), content-
 * digest pinning (S6), output-rights (S6). It ships as a `manifestVersion: 2`
 * codemod in a later phase — THIS package only publishes the TYPES.
 *
 * SEC-6 (load-bearing): the manifest FORBIDS credential-bearing fields in the
 * license-component / resolution-result / output-rights surfaces. There is no
 * `token`/`secret`/`apiKey`/`connectionString` field and no open
 * `Record<string, unknown>` bag on any of those surfaces — they CANNOT carry a
 * secret by construction. A load-time manifest-validation test (membrane side)
 * enforces the same at runtime (S2 "no runtime trusts the build"); the type
 * system enforces it here.
 *
 * NET-1 (S4): the license AND-gate COMPOSES OVER `IEntitlementResolver` /
 * `IBankFeedProvider` — it never adds members to those interfaces
 * (interface-expansion blast-radius, earlier repository ticket #247).
 */

import type { DeviceCapabilityTier } from '../device-capability.js'
import type { CapabilityId, ProviderId } from './common.js'
import type { ProviderInputSchemaRef } from './schema.js'

// ---------------------------------------------------------------------------
// S4 — two-layer license components + the AND-gate
// ---------------------------------------------------------------------------

/**
 * The role a license component plays (S4). The single-string `license` field is
 * retired; a provider declares a SET of components, each typed by role:
 *  - inference: `engine` (runtime/app) ∧ `weights` (the model weights it runs)
 *  - bank-feed: `sdk` (the client lib) ∧ `dataSource` (the aggregator/data ToS)
 *  - general:   any component carrying a distinct license.
 */
export type LicenseComponentRole =
  | 'engine'
  | 'weights'
  | 'sdk'
  | 'dataSource'
  | 'other'

/**
 * A single license component (S4).
 *
 * SEC-6: no credential field. A license declaration carries an SPDX expression
 * and use-restriction flags — never a key, token, or connection secret.
 */
export interface LicenseComponent {
  /** The component's role in the provider (S4). */
  role: LicenseComponentRole
  /**
   * The SPDX license expression (e.g. `Apache-2.0`, `GPL-3.0-only`,
   * `OpenRAIL-M`, `proprietary`) or `unknown` when undeterminable at build time.
   * An `unknown` component forces fail-closed for commercial intent (S4).
   */
  spdx: string
  /**
   * Whether this component permits commercial use. `unknown` ⇒ treated as
   * NOT-commercial by the effective-gate MIN (fail-closed, S4).
   */
  commercialUse: 'yes' | 'no' | 'unknown'
  /** Free-form output/use restriction note (e.g. `CC-BY-NC`, `non-commercial weights`). Display only. */
  restrictions?: string | null
}

/**
 * Output-rights block (S6) — surfaced to the operator for an auto-selecting
 * system. SEC-6: no credential field; display/disclosure surface only.
 */
export interface OutputRights {
  /** Who owns the produced output (e.g. `operator`, `provider`, `shared`, `unknown`). */
  ownership: 'operator' | 'provider' | 'shared' | 'unknown'
  /** Whether the output is copyrightable (an open question for generated content — display only). */
  copyrightable: 'yes' | 'no' | 'unknown'
  /** Whether attribution is required, and to whom (free-form display string). */
  attribution?: string | null
  /** Free-form use-restriction note on the OUTPUT (display only). */
  restrictions?: string | null
}

// ---------------------------------------------------------------------------
// S2 — packaging / signing / hardware (extends v1)
// ---------------------------------------------------------------------------

/**
 * Where the provider's compute physically lives. An ORTHOGONAL "where compute
 * is" axis (ADR 0123 three-tier reconciliation) — NOT a slotting tier. v1's
 * `[local|cloud]` is widened to add `remote-worker` (a trusted Tailscale GPU
 * host that still rides the Inner-Loop thin mechanism, ADR 0124 M2).
 */
export type ProviderKind = 'local' | 'cloud' | 'remote-worker'

/**
 * Packaging mode (S6). The mandatory local FLOOR for a capability MUST be
 * `bundled` or `managed-install` — never `remote`/`cloud`-only.
 */
export type Packaging = 'bundled' | 'managed-install' | 'remote' | 'cloud'

/** Code-signing posture (S6 / Consumer A macOS ruling — user-space for v1). */
export type Signing = 'required' | 'user-space' | 'n-a'

/** Hardware-support level for a host profile (mirrors v1 `hardware{}.support`). */
export type HardwareSupport = 'best' | 'good' | 'ok' | 'partial' | 'unsupported'

/** Acceleration backend for a host profile (mirrors v1 `hardware{}.accel`). */
export type HardwareAccel = 'cuda' | 'rocm' | 'mps' | 'cpu' | 'any'

/** Per-host-profile hardware fit (mirrors v1 `hardware{}` matrix; ADR 0116). */
export interface HardwareProfileFit {
  /** Support level on this host profile. */
  support: HardwareSupport
  /** Acceleration backend used on this host profile. */
  accel?: HardwareAccel
  /** Free-form note. */
  notes?: string | null
}

// ---------------------------------------------------------------------------
// S6 — content-digest pinning
// ---------------------------------------------------------------------------

/**
 * Content-digest pin (S6) — resolves reproducibility + supersession-attack +
 * license-regression at once. Per consumer-resolved slot.
 */
export interface ContentDigest {
  /** Digest of the provider adapter/code bytes (sha256, hex). */
  providerHash: string
  /** Digest of the resolved asset (weights/model/parser asset) bytes (sha256, hex). */
  assetHash?: string | null
  /** The manifest schema version this pin was resolved under. */
  manifestVersion: 2
}

// ---------------------------------------------------------------------------
// The provider manifest v2
// ---------------------------------------------------------------------------

/**
 * Provider manifest v2 (S2). Describes one provider the membrane can resolve,
 * load-time-validate, content-pin, and license-gate.
 *
 * SEC-6: NO credential-bearing field. Connection secrets / API keys / tokens are
 * NEVER in the manifest — they are operator-supplied at runtime via the tier-2
 * `category-provider` seam (`UseVendorProviderIfConfigured`, ADR 0096), out of
 * band from this descriptor. The manifest is a DESCRIPTION, not a credential
 * store.
 */
export interface ProviderManifest {
  /** Schema discriminator — pins this as a v2 manifest (codemod target). */
  manifestVersion: 2
  /** Stable provider slug (matches v1 `id`). */
  id: ProviderId
  /** Human-readable name (v1 field name preserved — `name`, NOT `displayName`). */
  name: string
  /** Flight-Deck/Harborline-side manifest version (NOT the upstream tool version). */
  version: string
  /** Where compute lives (orthogonal axis; widened from v1 `[local|cloud]`). */
  kind: ProviderKind
  /** The capabilities this provider supplies (v1 `capabilities[]`). */
  capability: CapabilityId[]
  /**
   * Reference to the provider's input JSON-Schema 2020-12 document (S3). The UI
   * renders controls from it; `null` when the provider takes only CORE inputs.
   */
  inputSchemaRef?: ProviderInputSchemaRef | null
  /**
   * The engine/runtime/app license component (S4). Singular — one engine.
   * Present for inference providers; absent for pure data-feed providers (use
   * `sdkLicense`/`dataSourceTerms`).
   */
  engineLicense?: LicenseComponent | null
  /**
   * The model-weights license components (S4) — an ARRAY (a provider may run
   * several weight sets). Effective `commercialUse` = MIN over all of these ∧
   * the engine ∧ output-restrictions; an `unknown` arm fails closed.
   */
  weightsLicense: LicenseComponent[]
  /** The client-SDK license component (S4, bank-feed). */
  sdkLicense?: LicenseComponent | null
  /** The data-source/aggregator ToS license component (S4, bank-feed). */
  dataSourceTerms?: LicenseComponent | null
  /** Per-host-profile hardware fit matrix (v1 `hardware{}`; ADR 0116). */
  hardware: Record<string, HardwareProfileFit>
  /**
   * Conservative local-AI capacity floor. Required for local-model admission by
   * the DeviceCapabilityProfile resolver; ignored for remote/cloud providers.
   */
  minimumDeviceCapabilityTier?: DeviceCapabilityTier | null
  /** The resolution tier this provider targets (ADR 0116). */
  tier: 'local' | 'remote' | 'cloud'
  /** Packaging mode (S6) — the floor MUST be `bundled`/`managed-install`. */
  packaging: Packaging
  /** Code-signing posture (S6). */
  signing: Signing
  /** Output-rights disclosure block (S6). */
  outputRights?: OutputRights | null
  /** API-shape hint consumed by clients (v1 `flavor`). */
  flavor?: string | null
}

/**
 * The effective, computed license gate result for a resolved provider (S4).
 * The membrane computes `commercialUse = MIN over all license components`; this
 * is the OUTPUT type. TYPES ONLY — the AND-gate logic is build-new (ADR 0123
 * "the AND-gate logic is BUILD-NEW") and composes OVER `IEntitlementResolver`,
 * never expanding it (NET-1).
 *
 * SEC-6: no credential field.
 */
export interface EffectiveLicenseGate {
  /** Computed effective commercial-use eligibility (MIN over components). */
  commercialUse: 'yes' | 'no' | 'unknown'
  /**
   * Whether the gate BLOCKED the provider for the declared intent (fail-closed
   * default, S4). `true` ⇒ blocked unless a timestamped user-attestation
   * override is present.
   */
  blocked: boolean
  /** The license components that drove the MIN (for operator display). */
  components: LicenseComponent[]
  /**
   * Whether a timestamped user-attestation override is in effect (S4 / council
   * Q5). The override TIMESTAMP itself lives in the operator-attestation store,
   * not here — this is the boolean the resolver consumes.
   */
  attestationOverride: boolean
}
