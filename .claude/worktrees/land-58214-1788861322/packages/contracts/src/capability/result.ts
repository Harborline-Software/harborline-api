/**
 * The canonical RESULT envelope + error taxonomy + usage + mid-flight progress
 * envelope (ADR 0123 §S3 / ADR 0124 Part IV + paper-gate R3/FE-3).
 *
 * ONE envelope shape across all four transports and both consumers. The
 * paper-gate (2026-06-16) verified the envelope does NOT fork: all variance is
 * confined to two already-polymorphic slots —
 *   (1) `artifacts[].kind` + per-kind metadata, and
 *   (2) `usage.unit`
 * — i.e. value-space variance, not field-space variance. The six top-level
 * fields are fixed.
 *
 * BINDING (paper-gate R3): capability-typed continuation metadata (a feed's
 * `nextCursor`, an import batch `summary`) lives INSIDE `artifacts[].<kind>`
 * metadata, NEVER as a new top-level envelope field. Promoting any of it to the
 * top level would fork the envelope and break ADR 0124 M3.
 */

import type { DurationMillis, JobId } from './common.js'

/** Terminal/transitional job status. `partial` = some-but-not-all (batch import). */
export type JobStatus = 'accepted' | 'running' | 'succeeded' | 'partial' | 'failed'

// ---------------------------------------------------------------------------
// Artifacts — the polymorphic RESULT slot (R3)
// ---------------------------------------------------------------------------

/**
 * Base artifact shape. `kind` discriminates the per-kind metadata. Adding a
 * capability adds a new `kind` arm — NOT a new envelope field. This is the
 * containment that keeps the envelope from forking.
 */
export interface ArtifactBase {
  /** The artifact kind discriminator. */
  kind: string
}

/** An image artifact (inference). Dimensions land here, not in the envelope. */
export interface ImageArtifact extends ArtifactBase {
  kind: 'image'
  /** Where the image bytes live (blob ref / URI). */
  uri: string
  /** MIME type of the produced image. */
  mime: string
  /** Produced width in pixels (native-2k lands here, not as an envelope field). */
  w: number
  /** Produced height in pixels. */
  h: number
}

/**
 * A text artifact (e.g. `llm` / `generate`). The produced text plus, for the
 * KG-search `generate` capability (ADR 0135 KG-search Slice 2-foundation), the
 * model provenance + the TAINT label.
 *
 * TAINT (load-bearing — §2.8.4 firewall extended to retrieved text): a
 * `generate` proposal is produced from UNTRUSTED retrieved grounding (a stored
 * injection may have detonated at generation), so the artifact is
 * `taint: 'untrusted-derived'` — it re-enters the human-gated path and is
 * NEVER promoted to an autonomous action nor auto-sent outbound. The consumer
 * treats a tainted artifact as a *proposal*, never an instruction.
 */
export interface TextArtifact extends ArtifactBase {
  kind: 'text'
  /** The produced text (an `llm` completion or a `generate` grounded PROPOSAL). */
  text: string
  /** The model that produced the text (e.g. `qwen2.5-7b-instruct`). Absent for a plain `llm` artifact. */
  model?: string
  /** The model version/revision. Absent for a plain `llm` artifact. */
  modelVersion?: string
  /**
   * The taint label. `untrusted-derived` ⇒ produced from untrusted retrieved
   * content (the firewall obligation: a proposal, never an action; reviewed
   * before it leaves). Absent ⇒ no special taint (a plain `llm` artifact).
   */
  taint?: 'untrusted-derived'
}

/**
 * An import-batch artifact (bank-import). Carries the batch metadata + the
 * capability-typed continuation (`nextCursor`) per R3 — INSIDE the artifact,
 * never as a top-level envelope field. Maps the shipped `ImportBatchResult`
 * (`BatchId`/`TotalRows`/`Inserted`/`Skipped`, ADR 0112).
 */
export interface ImportBatchArtifact extends ArtifactBase {
  kind: 'import-batch'
  /** The persisted batch handle (`ImportBatchResult.BatchId`). */
  batchRef: string
  /** Per-batch tallies. */
  summary: ImportBatchSummary
  /**
   * Feed continuation cursor for the next incremental pull (R3). Opaque,
   * round-trips to the caller; capability-typed artifact metadata, NOT a
   * top-level envelope field. Absent for file imports.
   */
  nextCursor?: string | null
}

/** Per-batch tallies for an `import-batch` artifact. */
export interface ImportBatchSummary {
  /** Rows inserted (`ImportBatchResult.Inserted`). */
  inserted: number
  /** Rows skipped as duplicates (`ImportBatchResult.Skipped`). */
  duplicates: number
}

/**
 * An audio artifact (`tts`). The produced audio bytes land here (a file path /
 * blob ref / URI), NOT as an envelope field — the same containment that keeps
 * the envelope from forking applies to a third capability domain (speech).
 */
export interface AudioArtifact extends ArtifactBase {
  kind: 'audio'
  /** Where the audio bytes live (file path / blob ref / URI). */
  uri: string
  /** MIME type of the produced audio (e.g. `audio/x-aiff`, `audio/wav`). */
  mime: string
  /** Duration of the produced audio in milliseconds, if known. */
  durationMs?: number | null
}

/**
 * An embeddings artifact (`embeddings` — the KG vector tier, ADR 0123 amendment
 * 2026-06-24). Carries the produced float vectors AND the model+version that
 * produced them, so the KG consumer's derived index is a VERSIONED projection
 * (G-5): a model change ⇒ re-embed, never a silent mixed-dimension index. The
 * vectors are returned INLINE (a float[][]) — they are small (1024 floats/row)
 * and the consumer quantizes+persists them itself; there is no blob ref.
 */
export interface EmbeddingsArtifact extends ArtifactBase {
  kind: 'embeddings'
  /** One vector per input text, in input order. Each is exactly `dimension` long. */
  vectors: number[][]
  /** The dimension every vector has (echoes the request CORE's `dimension`; BGE-M3 = 1024). */
  dimension: number
  /** The model that produced these vectors (G-5 — pinned per row by the consumer; e.g. `bge-m3`). */
  model: string
  /** The model version/revision (G-5 — a change forces a full re-embed). */
  modelVersion: string
}

/** A single reranked document — references back into the request CORE's `documents` by index. */
export interface RerankScore {
  /** The index of the scored document in the request CORE's `documents[]`. */
  index: number
  /** The cross-encoder relevance score (higher = more relevant). Provider-normalized. */
  score: number
}

/**
 * A rerank artifact (`rerank` — the KG cross-encoder relevance tier, ADR 0123
 * amendment 2026-06-24). Carries the scored documents (by index back into the
 * request, highest-first) + the reranker model+version. Like `embeddings`, the
 * model id rides the artifact so the consumer never silently mixes scorers.
 */
export interface RerankArtifact extends ArtifactBase {
  kind: 'rerank'
  /** The scored documents, highest-score-first; `topK`-truncated when the CORE asked. */
  scored: RerankScore[]
  /** The reranker model that produced these scores (e.g. `bge-reranker-v2-m3`). */
  model: string
  /** The reranker model version/revision. */
  modelVersion: string
}

/**
 * The artifact union encoded today. Capability-typed; the membrane carries each
 * artifact verbatim, keyed on `kind`. New capabilities add a `kind` arm here.
 */
export type Artifact =
  | ImageArtifact
  | TextArtifact
  | ImportBatchArtifact
  | AudioArtifact
  | EmbeddingsArtifact
  | RerankArtifact

// ---------------------------------------------------------------------------
// Usage (ADR 0124 Part V) — fixed shape, value-space variance only
// ---------------------------------------------------------------------------

/** The cost tier a job ran at (also the resolution `tier`, ADR 0116). */
export type UsageTier = 'local' | 'remote' | 'cloud'

/**
 * Typed usage record. Fixed `{unit, quantity, costMicros?, tier}` shape; only
 * the `unit` VALUE varies per capability (`image`/`token`/`line`), never a new
 * field (ADR 0124 Part V).
 */
export interface Usage {
  /** The billing unit — a typed value, not a new field (`image` | `token` | `line` | ...). */
  unit: string
  /** How many units the job consumed. */
  quantity: number
  /** Cost in micro-units of currency, if metered. Absent for unmetered local jobs. */
  costMicros?: number | null
  /** The tier the job ran at. */
  tier: UsageTier
}

// ---------------------------------------------------------------------------
// Error taxonomy (ADR 0124 Part IV.2) — one shape, three fault domains
// ---------------------------------------------------------------------------

/**
 * Where a fault originated. `Resolve.isFallback` (FE-1) triggers off this:
 *  - `input`    — the caller's input is bad (malformed file, oversize) — NOT retryable, no fallback
 *  - `provider` — the provider failed (OOM/503/reauth) — may be retryable → may fall back to a floor
 *  - `membrane` — the membrane itself (idempotency collision, normalization failure) — fail-closed
 */
export type FaultDomain = 'input' | 'provider' | 'membrane'

/**
 * The typed error half of the RESULT envelope. UNIFORM across every transport +
 * fault-domain — no per-provider error shape (the shipped `ConnectionStatus` /
 * `StatementParseException` surfaces map onto this without new shapes).
 */
export interface CapabilityError {
  /** Which plane failed. Drives fallback/retry decisions. */
  faultDomain: FaultDomain
  /** Whether retrying the SAME Invoke could succeed. */
  retryable: boolean
  /** Stable machine code (e.g. `parse.malformed`, `provider.overloaded`, `feed.needs_reauth`, `membrane.duplicate`). */
  code: string
  /** Human-readable message (display string, NOT the branch key). */
  message: string
  /** Suggested back-off in milliseconds for a retryable provider fault, if known. */
  retryAfter?: DurationMillis
}

// ---------------------------------------------------------------------------
// The terminal RESULT envelope — six fixed top-level fields
// ---------------------------------------------------------------------------

/**
 * The canonical RESULT envelope (ADR 0123 §S3). SIX fixed top-level fields:
 * `{ jobId, status, progress, artifacts[], usage, error }`. Identical across
 * `sync`/`poll`/`stream`/`webhook` and across `image`/`bank-import`/`llm`.
 *
 * For a `webhook` transport the same shape is delivered async; for a consumer
 * that has gone away the membrane persists it keyed on `jobId` (durable
 * continuation, paper-gate Case 4).
 */
export interface CapabilityResult {
  /** The membrane-minted job handle. */
  jobId: JobId
  /** Terminal/transitional status. */
  status: JobStatus
  /** Completion fraction 0.0–1.0. */
  progress: number
  /** Produced artifacts — the polymorphic, capability-typed slot (R3). */
  artifacts: Artifact[]
  /** Typed usage record (ADR 0124 Part V). */
  usage: Usage
  /** Typed error half — null on success. */
  error: CapabilityError | null
}

// ---------------------------------------------------------------------------
// Mid-flight progress envelope (FE-3) — separate but uniform shape
// ---------------------------------------------------------------------------

/**
 * The canonical mid-flight progress envelope for `poll`/`stream` (FE-3).
 *
 * ONE fixed shape `{ jobId, status, progress, stage?, delta?, partial? }` with
 * CAPABILITY-TYPED payload slots (`delta`/`partial`), mirroring how `artifacts[]`
 * is capability-typed in the terminal envelope. It does NOT fork: for `llm`,
 * `delta` is a text chunk; for progressive `image`, `partial` is a preview-frame
 * artifact ref. The membrane carries them verbatim, keyed on `capabilityId` +
 * artifact `kind`; it does not interpret them.
 *
 * Without this fixed shape, M3's uniform-envelope promise breaks on the
 * streaming path (FE-3).
 */
export interface ProgressEnvelope {
  /** The job this progress update belongs to. */
  jobId: JobId
  /** Always `running` for a mid-flight update. */
  status: 'running'
  /** Completion fraction 0.0–1.0. */
  progress: number
  /** Optional human-readable stage label (e.g. `"sampling 12/30"`). */
  stage?: string | null
  /**
   * Capability-typed incremental delta (e.g. an `llm` text chunk). Opaque to
   * the membrane; carried verbatim. Mutually-meaningful with `partial` per
   * capability — at most one is populated for a given provider's stream shape.
   */
  delta?: unknown
  /**
   * Capability-typed partial result (e.g. a progressive `image` preview-frame
   * artifact ref). Opaque to the membrane; carried verbatim.
   */
  partial?: unknown
}
