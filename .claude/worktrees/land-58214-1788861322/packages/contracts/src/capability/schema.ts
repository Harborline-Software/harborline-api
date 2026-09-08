/**
 * Provider-declared input JSON-Schema reference (ADR 0123 §S3).
 *
 * Every provider declares the rest of its input surface (beyond the thin CORE)
 * as a JSON-Schema 2020-12 document: image → steps/img2img/ControlNet/LoRA/
 * Ideogram bbox; bank → the per-bank `CsvColumnMapping`/field-map/date-format.
 * The UI renders form controls from this schema; adding a provider whose inputs
 * the schema can express is a manifest entry, NOT a code release.
 *
 * This contract references the schema by id + content digest (it does NOT embed
 * the schema body — that lives in the manifest's bundled files, loaded under the
 * Agent-Skills progressive-disclosure discipline, ADR 0124 Part III). Validation
 * of `providerInputs` against the resolved schema happens at the membrane
 * boundary, not here (TYPES ONLY).
 */

/**
 * A reference to a provider's input JSON-Schema 2020-12 document.
 *
 * The schema body itself is a bundled manifest file. This reference is what
 * rides the contract surface: enough to fetch, content-pin, and version the
 * schema without inlining it.
 */
export interface ProviderInputSchemaRef {
  /**
   * The schema document `$id` (JSON-Schema 2020-12). The provider's declared
   * input surface is identified and versioned by this.
   */
  schemaId: string
  /**
   * Content digest of the resolved schema document (sha256, hex). Pins the
   * exact schema bytes the UI rendered and the membrane validated against —
   * part of the content-digest reproducibility discipline (ADR 0123 §S6).
   */
  schemaHash: string
  /**
   * The JSON-Schema dialect. Fixed to 2020-12 per ADR 0123 §S3; declared
   * explicitly so a future dialect bump is a visible contract change.
   */
  dialect: 'https://json-schema.org/draft/2020-12/schema'
}

/**
 * The caller-supplied provider-specific inputs, conforming to the resolved
 * `ProviderInputSchemaRef`. Opaque at the contract level (its shape is the
 * provider's schema, by design); the membrane validates it against the schema
 * at the boundary. Binary inputs are NOT here — they ride `attachments[]` (R1),
 * referenced from `providerInputs` by `attachmentId`.
 */
export type ProviderInputs = Record<string, unknown>
