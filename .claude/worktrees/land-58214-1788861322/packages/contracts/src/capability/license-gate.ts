/**
 * The S4 multi-component license AND-gate EVALUATOR (ADR 0123 §S4).
 *
 * `manifest.ts` declares the license-component TYPES + the `EffectiveLicenseGate`
 * OUTPUT type; per ADR 0123 *"the AND-gate logic is BUILD-NEW"* the evaluation is
 * implemented HERE (not on the manifest type, not by expanding
 * `IEntitlementResolver` — NET-1). This is the first runtime behaviour in the
 * capability namespace; it is a PURE function (no I/O, no resolver dependency) so
 * it composes OVER the entitlement resolver at the membrane boundary rather than
 * inside it.
 *
 * The gate is the structural reason a NON-permissive model is blocked fail-closed:
 * the KG search's `embeddings` floor (BGE-M3) + `rerank` floor (bge-reranker-v2-m3)
 * are both MIT and PASS; a CC-BY-NC or Llama-community multilingual model would be
 * `commercialUse: 'no'`/`'unknown'` on its weights arm and the MIN drops the whole
 * provider to blocked — which is WHY the MIT models were chosen (ADR 0123 amendment
 * 2026-06-24, S4).
 *
 * THE RULE (S4): `commercialUse = MIN over { engineLicense, weightsLicense[*],
 * sdkLicense, dataSourceTerms }`, where the order is `no < unknown < yes`. An
 * `unknown` component forces fail-closed for commercial intent (treated strictly
 * below `yes`); ANY `no` makes the whole provider `no`. `blocked` is the
 * fail-closed default for a non-`yes` effective gate, liftable only by a
 * timestamped operator attestation (the boolean is supplied by the caller; the
 * timestamp itself lives in the operator-attestation store, not here).
 *
 * SEC-6: the gate consumes only license SPDX/commercial-use flags — never a
 * credential. The output carries no secret (the `EffectiveLicenseGate` type forbids
 * one by construction).
 */

import type {
  EffectiveLicenseGate,
  LicenseComponent,
  ProviderManifest,
} from './manifest.js'

/** Commercial-use eligibility as a totally-ordered lattice: `no < unknown < yes`. */
const COMMERCIAL_ORDER: Record<LicenseComponent['commercialUse'], number> = {
  no: 0,
  unknown: 1,
  yes: 2,
}

/** Inverse of {@link COMMERCIAL_ORDER} for mapping a rank back to its label. */
const COMMERCIAL_BY_RANK: LicenseComponent['commercialUse'][] = ['no', 'unknown', 'yes']

/**
 * Collect every license component a manifest declares, in a stable order
 * (engine, then each weights arm, then sdk, then dataSource). The MIN runs over
 * exactly this set — a provider that declares NO components at all is treated as
 * `unknown` (fail-closed: an undeclared license is not a clean commercial yes).
 */
export function collectLicenseComponents(manifest: ProviderManifest): LicenseComponent[] {
  const components: LicenseComponent[] = []
  if (manifest.engineLicense != null) components.push(manifest.engineLicense)
  for (const w of manifest.weightsLicense) components.push(w)
  if (manifest.sdkLicense != null) components.push(manifest.sdkLicense)
  if (manifest.dataSourceTerms != null) components.push(manifest.dataSourceTerms)
  return components
}

/**
 * Evaluate the S4 license AND-gate for a resolved provider manifest.
 *
 * `commercialUse` = MIN over all declared components (`no < unknown < yes`);
 * a provider with no components is `unknown`. `blocked` is the fail-closed default
 * for any non-`yes` effective gate UNLESS the caller supplies a timestamped
 * operator-attestation override (`attestationOverride: true`). A `yes` gate is
 * never blocked (no override needed).
 *
 * Pure: no I/O, no resolver call — composes OVER `IEntitlementResolver` (NET-1).
 *
 * @param manifest             the resolved provider manifest (S2/S4)
 * @param attestationOverride  whether a timestamped operator attestation is in
 *                             effect (S4 / council Q5). Defaults to `false`
 *                             (fail-closed: no silent override).
 */
export function evaluateLicenseGate(
  manifest: ProviderManifest,
  attestationOverride = false,
): EffectiveLicenseGate {
  const components = collectLicenseComponents(manifest)

  // MIN over the lattice. No components ⇒ `unknown` (an undeclared license is not
  // a clean commercial yes — fail-closed).
  const minRank =
    components.length === 0
      ? COMMERCIAL_ORDER.unknown
      : components.reduce(
          (acc, c) => Math.min(acc, COMMERCIAL_ORDER[c.commercialUse]),
          COMMERCIAL_ORDER.yes,
        )
  const commercialUse = COMMERCIAL_BY_RANK[minRank]

  // Fail-closed: anything below `yes` is BLOCKED unless a timestamped attestation
  // override is in effect. A `yes` gate is never blocked.
  const blocked = commercialUse !== 'yes' && !attestationOverride

  return {
    commercialUse,
    blocked,
    components,
    attestationOverride,
  }
}
