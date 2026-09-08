/**
 * `@harborline-software/capability-host/resolution` — the BROWSER-SAFE subpath barrel.
 *
 * The Capability root barrel (`@harborline-software/capability-host`, `src/index.ts`) re-exports the Node-only
 * S7 sandbox (`./sandbox/index.js`) and the subprocess runtimes
 * (`./runtime/say-tts-runtime.js`, …) which pull in `child_process`/`os`. Importing
 * that barrel from a Vite renderer breaks the build (Node built-ins in the browser
 * graph). This subpath exposes ONLY the COMPOSE-TIME resolution surface — the
 * pack-DAG resolver + the composition manifest types — which are pure functions over
 * TYPE-ONLY imports of `@harborline-software/api-contracts` (zero runtime deps). The Harborline App Admin
 * console runs `resolveEdition` / `projectForTenant` client-side off this barrel.
 *
 * Anything added here MUST stay browser-safe (no `node:*`, no `child_process`, no
 * subprocess/sandbox imports). The Node-only host-side surface stays on the root
 * barrel.
 */

// The pack-DAG resolver (ADR 0129 D7) — resolveEdition / projectForTenant +
// CompositionError + ProvenanceMap + ResolvedEdition + TenantTier.
export * from './pack-resolver.js'

// The composition manifest seam (ADR 0125 D2/D3) — CompositionManifest,
// CompositionEntry, MembershipClass, membershipOf. Re-exported so consumers can
// import the manifest types from the one resolution subpath.
export * from './composition.js'

// The pack catalog (ADR 0129 D2/D3) — the reference edition's catalog + seed. Pure data
// (PackManifest[] over type-only @harborline-software/api-contracts), browser-safe. The Admin
// console resolves over REFERENCE_APP_CATALOG with REFERENCE_APP_SEED.
export * from '../catalog/harborline-catalog.js'

// The pack-manifest authoring types (ADR 0129 D1/D2 — from @harborline-software/api-contracts).
// Re-exported here so a consumer that authors a catalog (e.g. the Admin console's
// synthetic same-tier-conflict fixture) imports the type from the one resolution
// subpath, never reaching into @harborline-software/api-contracts directly. Type-only → erased.
export type {
  PackManifest,
  ProvidedCapability,
  ProviderRealization,
  HorizontalFlavor,
  PackRef,
  CapabilityId,
} from '@harborline-software/api-contracts'
