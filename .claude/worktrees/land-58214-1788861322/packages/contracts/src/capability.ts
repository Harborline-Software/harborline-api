/**
 * Capability namespace — the Capability membrane contract surface.
 *
 * The X-1 v0 build-gate (ADR 0123 X-1 / ADR 0124 X-1): `@harborline-software/api-contracts` is
 * the SINGLE SOURCE for the Capability membrane's envelope/manifest/resolution types.
 * These inference/capability types are the retained TypeScript compatibility surface. ADR 0162
 * supersedes the former TS-canonical ruling for inter-language boundaries; those consumers use
 * the generated `@harborline-software/api-contracts/protocol` port and DTO projection. The
 * .NET local-node and Python workers mirror these shapes; this package is the
 * authority.
 *
 * TYPES + JSON-Schema only — NO runtime behaviour. The membrane normalizes each
 * provider-runtime's native wire into these shapes at the M3 boundary
 * (ADR 0124 "adopt the patterns, OWN the envelope").
 *
 * This namespace encodes the three BINDING P1 paper-gate placements
 * (`_shared/research/p1-capability-membrane-schema-paper-gate-2026-06-16.md`):
 *  - **R1** — `attachments[]` is a request-envelope face (`invoke.ts`
 *    `RequestAttachment`/`InvokeRequest.attachments`), parallel to RESULT
 *    `artifacts[]`, never a CORE field.
 *  - **R2** — the bank-import `source` discriminator (file | feed) is the ONE
 *    universal CORE field; `format`/`cursor` are NOT top-level CORE fields
 *    (`cores.ts` `BankImportSource`).
 *  - **R3** — capability-typed continuation metadata (`nextCursor`, batch
 *    `summary`) lives inside `artifacts[].<kind>` metadata, never a top-level
 *    envelope field; the FE-3 mid-flight progress envelope is a fixed shape with
 *    capability-typed `delta`/`partial` slots (`result.ts`).
 *
 * And the binding council conditions:
 *  - **SEC-2** — `idempotencyKey` REQUIRED + fail-closed on financial Invoke
 *    (`invoke.ts`).
 *  - **SEC-6** — license-component / resolution-result / output-rights surfaces
 *    forbid credential-bearing fields by construction (`manifest.ts`,
 *    `resolution.ts`).
 *  - **NET-1** — the S4 license AND-gate composes over `IEntitlementResolver`,
 *    never expands it (`manifest.ts` `EffectiveLicenseGate`).
 *  - **NET-2 / Address** — superset-of-ADR-0061 transport mapping is a membrane
 *    concern (not encoded as input types here).
 *  - **FE-1** — closed `resolutionState` enum + resolution-result
 *    (`resolution.ts`).
 *  - **FE-3** — canonical mid-flight progress envelope (`result.ts`
 *    `ProgressEnvelope`).
 *
 * NOT wired into flight-deck/Harborline yet (a later Capability phase) — this publishes
 * the namespace + exports + drift tests only. Framework-neutral.
 */

export * from './capability/common.js'
export * from './capability/cores.js'
export * from './capability/schema.js'
export * from './capability/invoke.js'
export * from './capability/result.js'
export * from './capability/resolution.js'
export * from './capability/manifest.js'
// S4 license AND-gate EVALUATOR (the build-new gate logic; ADR 0123 §S4 / NET-1).
// The first runtime behaviour in the namespace — a pure MIN-over-components fn
// that composes OVER IEntitlementResolver, never expands it.
export * from './capability/license-gate.js'
export * from './capability/negotiate.js'
// Observe(health) REPORT roll-up (ADR 0124 Part IV.6) — the cross-runtime shape
// the renderer/SDK/CLI Observe faces return (lifted from the client.ts mirror).
export * from './capability/health.js'
// Pack manifest — the canned-domain composition unit (ADR 0129 D1). Pure types;
// the X-1 single source for the pack-DAG composition layer above the membrane.
export * from './capability/pack.js'
