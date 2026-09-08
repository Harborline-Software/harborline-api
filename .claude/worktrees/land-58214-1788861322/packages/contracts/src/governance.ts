/**
 * SPINE-2 governance contract (ADR 0140 D2) — the net-new tag→policy types.
 *
 * Mirrors packages/foundation-governance/Policy/* (the .NET model is canonical):
 *  - EffectKind / Trigger        → Policy/EffectVocabulary.cs
 *  - PolicyEffect / EffectParams → Policy/PolicyEffect.cs
 *  - PolicyBinding / TagClass    → Policy/PolicyBinding.cs
 *  - ResolvedFieldPolicy         → Resolution/ResolvedFieldPolicy.cs
 *
 * The aspect-overlay data types (AspectOverlay, ClassificationAspect, Tag, …) live in
 * `forms.ts` (additive on FieldOverlay/FormSection/HarborlineOverlay); this file carries only
 * the governance LAYER vocabulary. Drift is detected by governance.test.ts (structural
 * `satisfies` + exhaustive enum guards over the closed EffectKind / Trigger unions).
 */

import type { Tag } from './forms.js'

/** What a policy effect does at a trigger (`EffectKind`). */
export type EffectKind =
  | 'Encrypt'
  | 'Redact'
  | 'Mask'
  | 'Audit'
  | 'Retain'
  | 'Erase'
  | 'Reside'
  | 'Consent'

/** The lifecycle point at which an effect fires — the four PEPs (`Trigger`). */
export type Trigger = 'Store' | 'Read' | 'Export' | 'EraseSubject'

/** Typed parameters for a `PolicyEffect` (`EffectParams`). All optional. */
export interface EffectParams {
  /** Mask: reveal the last N characters. Absent ⇒ reveal none. */
  maskRevealLast?: number
  /** Encrypt: seal under the per-subject DEK rather than the tenant DEK. */
  subjectScoped?: boolean
  /** Retain: open-vocab regime token (e.g. `HIPAA`). */
  retainRegime?: string
  /** Retain: audit-event-class token (e.g. `Identity`). */
  retainFloorClass?: string
  /** Retain: authoring floor in days. */
  retainMinimumDays?: number
  /** Reside: allowed-jurisdiction set. */
  resideAllowedJurisdictions?: string[]
  /** Consent: the purpose token. */
  consentPurpose?: string
}

/** One effect of a policy bound to its triggers (`PolicyEffect`). */
export interface PolicyEffect {
  kind: EffectKind
  triggers: Trigger[]
  params: EffectParams
}

/** A required (effect, trigger) a `TagClass` mandates (`RequiredEffect`). */
export interface RequiredEffect {
  kind: EffectKind
  /** Absent ⇒ "this effect at any trigger". */
  trigger?: Trigger
}

/** A security/compliance class declaring the effects a compliant binding must cover (`TagClass`). */
export interface TagClass {
  name: string
  requiredEffects: RequiredEffect[]
}

/** A declarative binding of a tag to the effects it governs (`PolicyBinding`). */
export interface PolicyBinding {
  tag: Tag
  /** Optional security/compliance class; `null` when the tag is unclassed. */
  class?: TagClass | null
  effects: PolicyEffect[]
}

/** The composed (union) policy for a resolved field (`ResolvedFieldPolicy`). */
export interface ResolvedFieldPolicy {
  field: string
  tags: Tag[]
  /** The composed effects keyed by trigger (deduplicated by `(kind, trigger)`). */
  effectsByTrigger: Partial<Record<Trigger, PolicyEffect[]>>
}
