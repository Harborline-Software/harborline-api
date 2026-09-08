/**
 * Capability namespace — shared scalar/brand types.
 *
 * Part of the Capability membrane contract surface (ADR 0123 §S3 + ADR 0124).
 * Per ADR 0123 OQ-1 / ADR 0124 X-1 these inference/capability types are
 * **TS-canonical** — `@harborline-software/api-contracts` is the single source; the .NET
 * local-node and Python workers mirror these shapes, not the reverse.
 *
 * TYPES ONLY — no runtime behaviour. The Capability membrane normalizes every
 * provider-runtime's native wire into these shapes at the M3 boundary
 * (ADR 0124 "adopt the patterns, OWN the envelope").
 *
 * Framework-neutral: no React, no .NET, no transport-library imports.
 */

/**
 * A stable capability identifier. Consumers resolve on this, NEVER on a
 * provider name (ADR 0123 §S1 — "capability is stable, provider is data").
 * Closed-vocab in practice (`image`, `bank-import`, `tts/fast`, `stt/quality`,
 * `music`, `llm`, ...); typed as a branded string so the membrane can route
 * generically without baking the vocabulary into the contract.
 *
 * Examples: `'image'`, `'bank-import'`, `'tts/quality'`, `'llm'`.
 */
export type CapabilityId = string

/**
 * A stable provider identifier within a capability (the churning data behind
 * the stable capability). Resolved at runtime from the manifest registry.
 *
 * Examples: `'comfyui-sdxl'`, `'ideogram-4'`, `'acme-bank-csv'`, `'plaid-feed'`.
 */
export type ProviderId = string

/**
 * An ISO-8601 duration or a millisecond count, used for membrane-level
 * deadlines in a capability CORE. Modeled as milliseconds (number) for
 * wire simplicity; the membrane owns the deadline, not the provider.
 */
export type DurationMillis = number

/**
 * Opaque, membrane-minted job handle returned by every `jobId`-returning
 * transport (`poll`/`stream`/`webhook`) and present on the terminal RESULT
 * envelope for all transports (ADR 0124 Part IV). The provider's native
 * job-handle is mapped to this at the membrane boundary.
 */
export type JobId = string

/**
 * Caller-supplied correlation identifier, propagated across the
 * shell → runtime → provider → remote hops (ADR 0124 Part IV.4). Distinct
 * from `idempotencyKey`: `correlationId` is for tracing/observability;
 * `idempotencyKey` is for exactly-once financial-write semantics.
 */
export type CorrelationId = string
