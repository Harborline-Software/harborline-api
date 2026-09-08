/**
 * Dynamic-forms substrate contracts (ADR 0055).
 *
 * TypeScript mirror of the .NET forms-engine shapes so the React `SchemaForm`
 * renderer (a later @harborline-software/ui-react item — NOT built here) can consume a
 * typed `FormView` over the Harborline App→local-node-host HTTP channel, and so a
 * domain packet's `FormDefinition`/`HarborlineOverlay` round-trips with a typed
 * shape on the TS side.
 *
 * Canonical source (the .NET types these mirror):
 *  - packages/foundation-forms/Models/{FormDefinition,HarborlineOverlay,FormSection,
 *    FieldOverlay,RuleDefinition,RuleEnums,InternationalizedText,SemanticVersion,
 *    FormDefinitionId,IdentityRef,FormDefinitionStatus,FormDefinitionLineage}.cs
 *  - packages/foundation-forms-engine/{FormView,ValidationResult}.cs
 *  - apps/local-node-host/Health/FormsRoutes.cs (the wire DTOs — camelCase JSON)
 *
 * Field names are camelCase to match the ASP.NET Core Web serialiser the node
 * host returns (FormsRoutes wire DTOs).
 *
 * NOTE: the .NET model is the canonical source; these interfaces MIRROR it.
 * A drift between the two is what `__tests__/forms.test.ts` pins.
 */

import type { RoleReference, RecordStandingReference } from './authorization-references.js'

// ── i18n ────────────────────────────────────────────────────────────────────

/**
 * Locale-keyed text — mirrors the .NET `InternationalizedText`
 * (`DefaultLocale` + locale-tag→string map). Keys are RFC 5646 language tags
 * ("en", "en-US", "es-MX", "ar-AE"). The renderer resolves against the actor's
 * locale chain, falling back to `defaultLocale`.
 */
export interface InternationalizedText {
  defaultLocale: string
  values: Record<string, string>
}

// ── Enums (closed; mirror the .NET enums) ─────────────────────────────────────

/** Lifecycle status of a `FormDefinition` revision (`FormDefinitionStatus`). */
export type FormDefinitionStatus = 'Draft' | 'Published' | 'Deprecated' | 'Withdrawn'

/** PII sensitivity classification informing tenant-key encryption at rest (`PiiSensitivity`). */
export type PiiSensitivity = 'None' | 'Sensitive'

/**
 * Expression-language tier of a rule (`RuleTier`). As of SPINE-1 (ADR 0140 + CIC
 * 2026-06-30): Tier-1 `JsonSchema` = constraints/pattern; Tier-2 `JsonLogic` = the
 * ONE shipyard-jsonlogic/v1 engine doing logic AND compute; `PowerFx` is DEMOTED
 * (no longer a tier — a future advanced-function provider; the v1 evaluator rejects it).
 */
export type RuleTier = 'JsonSchema' | 'JsonLogic' | 'PowerFx'

/** Scope at which a rule evaluates (`RuleScope`). `Row`/`Table` are the child-table grains (SPINE-1). */
export type RuleScope = 'Field' | 'Section' | 'Schema' | 'Row' | 'Table'

/** What a rule does when it evaluates true (`RuleActionKind`). `Presentation` is SPINE-1 Decision DA. */
export type RuleActionKind = 'Visibility' | 'Required' | 'ReadOnly' | 'Validate' | 'Compute' | 'Presentation'

/**
 * Presentation payload carried by a `Presentation` rule (`PresentationHint`; SPINE-1
 * Decision DA — additive ADR-0055 contract extension). Applied when the rule is true.
 */
export interface PresentationHint {
  severity?: 'info' | 'warn' | 'error'
  badge?: InternationalizedText
  styleToken?: string
}

/**
 * Opaque UI control-selection hint (`FieldOverlay.ControlHint`). The .NET
 * keystone treats it as an opaque string and the engine maps it to a concrete
 * control; this union documents the values ADR 0055 §"Form rendering" names,
 * widened with `(string & {})` so a packet may carry a control the catalog
 * does not yet know without breaking the type.
 */
export type ControlHint =
  | 'text'
  | 'textarea'
  | 'date'
  | 'datetime'
  | 'address'
  | 'geo-point'
  | 'taxonomy-coding'
  | 'attachment'
  | 'reference'
  | 'variant'
  | 'recurrence-rule'
  | (string & {})

// ── Definition / overlay (authoring side — mirrors foundation-forms) ──────────

// ── Section layout (ADR 0055 Rev 6 amendment — input-group flex/grid layout) ──

/**
 * How a section's fields are arranged on screen (`SectionLayout.kind`).
 *
 * ADR 0055 Rev 6 additive amendment: a section is no longer only a linear
 * stack — it may declare a 2D layout. `'stack'` (the default / absent case)
 * preserves the prior one-field-per-row behaviour byte-for-byte, so every
 * pre-Rev-6 definition renders identically. `'flex'` and `'grid'` map to CSS
 * flexbox / CSS grid on the rendered `<fieldset>`. The renderer (`SchemaForm`)
 * maps the values to logical-property utilities so they stay RTL-correct.
 *
 * Presentation-only: it does NOT touch `SectionAccess` (the security-load-bearing
 * half) or the validation / rule surface.
 */
export type SectionLayoutKind = 'stack' | 'flex' | 'grid'

/** Flexbox main-axis direction for a `'flex'` section (logical, not physical). */
export type FlexDirection = 'row' | 'column'

/** Flex wrapping for a `'flex'` section. */
export type FlexWrap = 'nowrap' | 'wrap'

/** A spacing step (maps to a `gap-*` token: 0=0, 1=0.25rem … 8=2rem). */
export type LayoutGap = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 8

// ── Responsive layout INTENTS (F-23 — goal §10, 2026-07-01) ───────────────────
//
// All layout breadth is expressed as bounded INTENT tokens — width fractions,
// spans, alignment, density, a collapse breakpoint — NEVER pixel positioning.
// The renderer maps each token to logical-property utilities (RTL-safe); the
// breakpoint intent is CONTAINER-relative (the form's own width), so a
// two-column zone collapses to one column in a narrow pane as well as on a
// narrow viewport. Every member is additive: a definition carrying none of
// them stays byte-identical on the wire and renders exactly as before.

/**
 * The width threshold below which a `'flex'`/`'grid'` layout collapses to a
 * single-column stack (`LayoutBreakpoint`). Container-relative intent tokens
 * (small ≈ 24rem, medium ≈ 28rem, large ≈ 32rem of the FORM's width) — never
 * a pixel value in the definition.
 */
export type LayoutBreakpoint = 'sm' | 'md' | 'lg'

/** Vertical-rhythm density intent (`LayoutDensity`). Absent ⇒ `'comfortable'`. */
export type LayoutDensity = 'comfortable' | 'compact'

/** Cross-axis alignment intent (`LayoutAlign`) for a laid-out container or one item. */
export type LayoutAlign = 'start' | 'center' | 'end' | 'stretch'

/**
 * Width intent of one item in a `'flex'` layout (`FieldWidth`) — a bounded
 * fraction token, never a pixel width. `'auto'` (absent) = natural size.
 */
export type FieldWidth = 'auto' | '1/4' | '1/3' | '1/2' | '2/3' | '3/4' | 'full'

/**
 * Section layout configuration (`SectionLayout`). Absent ⇒ `'stack'`.
 *
 * - `'stack'`: a vertical column, one field per row (the legacy behaviour).
 * - `'flex'`: CSS flexbox — `direction` (row/column), `wrap`, `gap`. A field may
 *   declare a `grow` in its `FieldPlacement`.
 * - `'grid'`: CSS grid — `columns` equal-width tracks, `gap`. A field may span
 *   multiple columns via its `FieldPlacement.colSpan`.
 *
 * F-23 responsive intents (all additive; absent ⇒ prior behaviour byte-identical):
 * `collapseBelow` collapses a flex/grid arrangement to a single column when the
 * FORM is narrower than the breakpoint; `density` tightens the vertical rhythm;
 * `align` sets the cross-axis alignment of the laid-out items.
 */
export interface SectionLayout {
  kind: SectionLayoutKind
  /** flex only — main-axis direction. Default `'row'`. */
  direction?: FlexDirection
  /** flex only — wrap behaviour. Default `'wrap'`. */
  wrap?: FlexWrap
  /** grid only — number of equal-width column tracks (1–4). Default `2`. */
  columns?: number
  /** flex + grid — gap between items (a spacing token step). Default `4`. */
  gap?: LayoutGap
  /** F-23: collapse to a single column below this container-relative breakpoint. */
  collapseBelow?: LayoutBreakpoint
  /** F-23: vertical-rhythm density. Absent ⇒ `'comfortable'`. */
  density?: LayoutDensity
  /** F-23: cross-axis alignment of the laid-out items. Absent ⇒ the renderer default. */
  align?: LayoutAlign
}

/**
 * Per-field placement within a laid-out section (`FieldPlacement`), keyed by
 * field name on a `SectionLayout`-aware surface. Optional everywhere; absent ⇒
 * the field takes its natural size / one grid track.
 */
export interface FieldPlacement {
  /** grid only — how many column tracks this field spans (1–4). */
  colSpan?: number
  /** flex only — `flex-grow` factor (0 = don't grow). */
  grow?: number
  /** F-23, flex only — bounded width-fraction intent (never pixels). */
  width?: FieldWidth
  /** F-23 — per-item cross-axis alignment override. */
  align?: LayoutAlign
}

/** Read / write authorization for a `FormSection` (`SectionAccess`). Roles are opaque tokens. */
export interface SectionAccess {
  readRoles: RoleReference[]
  writeRoles: RoleReference[]
  /** Record-derived facts admitted separately from role vocabulary names. */
  readStandings?: RecordStandingReference[]
  /** Record-derived facts admitted separately from role vocabulary names. */
  writeStandings?: RecordStandingReference[]
  /** Optional Tier-1 (JSON-Schema if/then/else) conditional further narrowing read access. */
  readConditionExpression?: string
}

// ── Recursive form-item tree (ADR 0055 Rev 7 — nested sub-form items) ──────────
//
// The structural core that lets a form nest sub-forms to arbitrary depth. Mirrors
// the .NET `Harborline.Api.Foundation.Forms.FormItem` discriminated union (one record + a
// `Kind` discriminator on the .NET side; a discriminated union here). A `field` node
// references a field name in `HarborlineOverlay.fields` (the SAME registry as the flat
// list — no parallel per-node overlay). Fail-closed bounded at authoring by
// `FormTreeLimits` (INV-S2 extension). Wire kind is the lowercase string union,
// matching the `SectionLayoutKind` precedent.

/** The kind of a `FormItem` node (`FormItemKind`). `reference` (D4, ADR 0135 amendment
 * 2026-07-01) points at a reusable unit — the subtree resolves from the unit, not inline.
 * `content` / `action` (F-23) are NON-INPUT leaf blocks: authored display-only content
 * and a declarative action affordance. Neither binds an instance value — they never
 * appear in the candidate document, the submitted body, or the rule surface. */
export type FormItemKind = 'field' | 'group' | 'collection' | 'reference' | 'content' | 'action'

// ── Static content + action blocks (F-23 — goal §10, 2026-07-01) ──────────────

/**
 * One node of a static content block (`ContentNode`) — heading/paragraph-level
 * localized text ONLY. There is deliberately NO html/markup node kind: the text
 * is rendered as plain text (fail-closed against markup injection) and the node
 * kinds are a closed set the admission validator enforces.
 */
export type ContentNode =
  | {
      kind: 'heading'
      /** Localized heading text (plain text — never markup). */
      text: InternationalizedText
      /** Heading level intent (2–6; clamped by the renderer). Default 3. */
      level?: number
    }
  | {
      kind: 'paragraph'
      /** Localized body text (plain text — never markup). */
      text: InternationalizedText
    }

/**
 * The bounded action kinds an action block may declare (`FormActionKind`). A
 * definition carries CONFIG ONLY — no imperative code (ratified Decision 1;
 * mirrors the `OnSuccessConfig` precedent):
 *   - `'open-url'`      — the HOST consumes it (the renderer never navigates itself);
 *                          admission requires an absolute http(s) URL, fail-closed.
 *   - `'scroll-to-section'` — the renderer scrolls to + focuses the target section;
 *                          admission requires a declared section id.
 */
export type FormActionKind = 'open-url' | 'scroll-to-section'

/** Declarative config of an action block (`FormActionConfig`) — label + a bounded
 * action kind + its single target. Config only; never code. */
export interface FormActionConfig {
  kind: FormActionKind
  /** Localized button label. */
  label: InternationalizedText
  /** `open-url` only — an absolute http(s) URL (admission-validated, fail-closed). */
  url?: string
  /** `scroll-to-section` only — a declared section id (admission-validated). */
  sectionId?: string
}

/**
 * How a `ReusableUnitRef` (D4) selects which immutable unit version resolves at the reference
 * site. `pinnedVersion: null` tracks the latest published version (the propagation channel —
 * a newly published unit version reaches every latest-published referencer); a canonical
 * `"{major}.{minor}.{patch}"` string pins to that exact version (does not drift).
 */
export interface ReusableUnitVersionSelector {
  pinnedVersion: string | null
}

/**
 * A reference (D4) from a definition to a reusable form unit by id + version selector —
 * reuse-by-reference, not copy. Phase 1 carries NO per-property override: every property of
 * the referenced unit is CP-locked at the reference site (the per-unit AP-override endpoint is
 * deferred behind three named kill-triggers).
 */
export interface ReusableUnitRef {
  /** The referenced reusable unit's id. */
  unitId: string
  /** Which version resolves (pinned, or latest-published). */
  version: ReusableUnitVersionSelector
}

/** Instance-count bounds of a `collection` item (`Cardinality`). A `group` is implicitly cardinality-1. */
export interface Cardinality {
  /** Minimum required instances (default 0). */
  min: number
  /** Maximum permitted instances; absent ⇒ unbounded (still evaluation-bounded). */
  max?: number | null
}

// ── Collection tabular presentation (F-24 — grid/table input, item 5, 2026-07-02) ──
//
// A collection's `table` config rides the `collection` FormItem the way F-23 zone
// intents (`layout`) ride a `group` — it is PRESENTATION ONLY. It does NOT change the
// data shape (the collection is still cardinality-N rows of the row-template subtree)
// and it is NOT a new item kind. Presence ⇒ the renderer lays the rows out as a
// keyboard-navigable grid with a header row (+ optional totals footer); absence ⇒ the
// legacy stacked-fieldset ("list") presentation, byte-identical to a pre-item-5
// definition. Every member is a bounded intent token (reusing the F-23 vocab) — never
// a pixel width. Admission is fail-closed: a `table` on a non-collection, or an
// unknown token, is a 422.

/**
 * Per-column presentation config for a table-presented collection (`CollectionColumn`),
 * keyed by the row-template field key. Bounded intent tokens only.
 */
export interface CollectionColumn {
  /** Column width intent — reuses the F-23 bounded fraction tokens. Absent ⇒ auto. */
  width?: FieldWidth
  /** Column cell horizontal alignment. Absent ⇒ the renderer default (`'start'`). */
  align?: LayoutAlign
}

/**
 * Tabular presentation of a `collection` (`CollectionTableConfig`). Presentation-only;
 * additive (absent ⇒ the legacy stacked "list" presentation, byte-identical).
 */
export interface CollectionTableConfig {
  /** Optional per-column config keyed by row-template `field` key (width / align). */
  columns?: Record<string, CollectionColumn>
  /**
   * Row-template numeric/currency `field` keys to show a column SUM total for, in a
   * footer totals row. Each key MUST be a `field` item in the collection's row template
   * (admission-validated). Absent/empty ⇒ no totals row.
   */
  totals?: string[]
}

/**
 * One node in a form's recursive item tree (`FormItem`). A `group` is a
 * cardinality-1 nested object; a `collection` is a cardinality-N repeatable group
 * (the child-table / grid is its tabular presentation). Container children are
 * themselves `FormItem`s ⇒ arbitrary depth. A flat form carries no `items` and is a
 * depth-1 tree by definition (byte-identical to a pre-Rev-7 definition).
 */
export type FormItem =
  | {
      kind: 'field'
      /** The field name — an entry in `HarborlineOverlay.fields`. */
      key: string
    }
  | {
      kind: 'group'
      /** The object key the child values nest under. */
      key: string
      /** Optional localized container label (nested fieldset legend). */
      title?: InternationalizedText
      /** Child items (≥1). */
      items: FormItem[]
      /**
       * F-23 zone intents: an optional 2D layout for THIS group's children (the
       * "multi-column zone" — e.g. a photo strip as a wrapping flex row, a
       * two-column pair collapsing to one on a narrow pane). Additive; absent ⇒
       * the legacy single-column group, byte-identical to a pre-F-23 definition.
       */
      layout?: SectionLayout
      /** F-23: per-child placement (span/grow/width/align) keyed by child item key. */
      placement?: Record<string, FieldPlacement>
      /** SPINE-2 (ADR 0140 D2) group-grain aspect overlay — a container grain the resolver
       * walks between the section and each nested field (monotonic-union classification: a
       * group tag flows to its children). Additive; absent ⇒ byte-identical. */
      aspects?: AspectOverlay
    }
  | {
      kind: 'collection'
      /** The collection key + the rule-engine section id its rows address (`row.`/`table.`). */
      key: string
      /** Optional localized container label (collection heading). */
      title?: InternationalizedText
      /** The row-template child items (≥1). */
      items: FormItem[]
      /** Instance-count bounds; absent ⇒ 0..∞ (within the engine cap). */
      cardinality?: Cardinality
      /** F-24 (item 5): optional tabular presentation. Absent ⇒ the legacy stacked
       * "list" presentation (byte-identical). Presentation-only — does NOT change the
       * row data shape; rejected on any non-collection kind at admission. */
      table?: CollectionTableConfig
      /** SPINE-2 (ADR 0140 D2) collection-grain aspect overlay — a container grain the
       * resolver walks between the section and each row-template field (monotonic-union
       * classification: a collection tag flows to its columns). Additive; absent ⇒
       * byte-identical. */
      aspects?: AspectOverlay
    }
  | {
      kind: 'reference'
      /** The reference-site container key the unit's values nest under. */
      key: string
      /** The reusable-unit reference (D4). The child subtree resolves from the unit at
       * resolution time (reuse-by-reference); there is no inline `items` on a reference node. */
      reference: ReusableUnitRef
    }
  | {
      kind: 'content'
      /** Stable block key, unique among siblings. NOT a value key — a content
       * block never appears in the candidate document. */
      key: string
      /** The ordered content nodes (≥1; closed node kinds, fail-closed at admission). */
      content: ContentNode[]
    }
  | {
      kind: 'action'
      /** Stable block key, unique among siblings. NOT a value key. */
      key: string
      /** The declarative action config (bounded kinds; fail-closed at admission). */
      action: FormActionConfig
    }

/** A grouping of fields; the unit at which authorization is enforced (`FormSection`). */
export interface FormSection {
  id: string
  title: InternationalizedText
  /** Ordered field names belonging to this section (must appear in `HarborlineOverlay.fields`). */
  fields: string[]
  access: SectionAccess
  /**
   * Optional 2D layout for this section (ADR 0055 Rev 6). Absent ⇒ `'stack'`
   * (legacy one-field-per-row). Presentation-only; does not affect `access`.
   */
  layout?: SectionLayout
  /**
   * Optional per-field placement keyed by field name (grid colSpan / flex grow),
   * honoured only when `layout.kind` is `'grid'` / `'flex'`.
   */
  fieldPlacement?: Record<string, FieldPlacement>
  /** SPINE-2 (ADR 0140 D2) section-grain aspect overlay. Additive; absent ⇒ byte-identical. */
  aspects?: AspectOverlay
  /**
   * Optional recursive form-item tree (ADR 0055 Rev 7 — nested sub-form items).
   * Present ⇒ the section's structure is the tree (nested groups / repeatable
   * collections); `fields` is then the flat authorization/order fallback. Absent ⇒
   * a depth-1 flat section, byte-identical to a pre-Rev-7 definition. `field` items
   * reference the SAME `HarborlineOverlay.fields` registry.
   */
  items?: FormItem[]
}

/**
 * Per-control config forwarded to the renderer (F-17 field library). A typed,
 * OPEN-BY-KEY bag of the settings a control needs that its `controlHint` alone
 * does not express — e.g. a `currency` field's ISO-4217 code, a `file` field's
 * accept filter + multi-select flag. The renderer reads these keys by name
 * (`SchemaForm` maps `config.currencyCode` → the CurrencyField, `config.accept` /
 * `config.multiple` → the DropZone).
 *
 * The keys enumerated here are the UNIVERSE of config a definition may carry.
 * WHICH keys a given field type may legitimately carry is owned per-type by the
 * builder's field-type registry (`FieldTypeDefinition` config schema) — the
 * authoring layer validates a field's config against its type's declared schema
 * and drops out-of-schema keys FAIL-CLOSED, so arbitrary config never reaches the
 * wire. Absent ⇒ the control renders with its zero-config defaults.
 */
export interface FieldConfig {
  /** `currency`: ISO-4217 currency code (e.g. `'USD'`, `'EUR'`, `'AED'`). */
  currencyCode?: string
  /** `file`: accept filter — a comma-separated MIME-type / extension list. */
  accept?: string
  /** `file`: whether the control accepts multiple files (default `true` when absent). */
  multiple?: boolean
}

/**
 * Per-field rendering + authorization hints layered on the field's JSON Schema
 * definition (`FieldOverlay`).
 */
export interface FieldOverlay {
  label: InternationalizedText
  helpText?: InternationalizedText
  controlHint?: ControlHint
  /** PII sensitivity (default `'None'` on the .NET side). */
  piiSensitivity?: PiiSensitivity
  /** Optional field-level read-role override, narrowing the section's `readRoles`. */
  readRoles?: RoleReference[]
  /** Optional field-level write-role override, narrowing the section's `writeRoles`. */
  writeRoles?: RoleReference[]
  /** Optional field-level read-standing override. */
  readStandings?: RecordStandingReference[]
  /** Optional field-level write-standing override. */
  writeStandings?: RecordStandingReference[]
  /** SPINE-2 (ADR 0140 D2) field-grain aspect overlay. Additive; absent ⇒ byte-identical. */
  aspects?: AspectOverlay
  /**
   * Per-control config the renderer reads (F-17 — currency code, file accept /
   * multiple, …). Additive; absent ⇒ byte-identical to a pre-F-17 definition.
   */
  config?: FieldConfig
}

/** One cross-field rule attached to a `FormDefinition` (`RuleDefinition`). */
export interface RuleDefinition {
  id: string
  tier: RuleTier
  scope: RuleScope
  /**
   * Field name (Field), section id (Section), empty string (Schema),
   * `section/field` (Row), or `section/fn/col` (Table).
   */
  scopeTarget: string
  /** Opaque expression text in the language indicated by `tier`. */
  expression: string
  action: RuleActionKind
  errorMessage?: InternationalizedText
  /** Presentation payload for a `Presentation`-action rule (SPINE-1 Decision DA). */
  presentation?: PresentationHint
}

// ── Rule evaluation result (SPINE-1 — the net-new RuleOutcome family) ─────────
//
// The single neutral output the SPINE-1 rule engine produces. The CANONICAL TS
// runtime that emits these is `@harborline-software/rule-engine` (the dual-tier engine); these
// interfaces are the contract MIRROR for typed consumers (the SchemaForm renderer)
// that depend only on @harborline-software/api-contracts. Both tiers emit byte-identical values —
// the shared conformance corpus is the proof.

/** Output-type taxonomy of a `RuleOutcome` (`OutputType`). */
export type OutputType = 'Value' | 'Validity' | 'Visibility' | 'Presentation'

/** State of a `ComputedValue` (`ValueState`). `Pending` is the client tier's async state. */
export type ValueState = 'Resolved' | 'Error' | 'Pending'

/** Severity of a `PresentationOutcome` (`Severity`). */
export type Severity = 'info' | 'warn' | 'error'

/** A localizable rule error — a stable code + stringified params, never English prose. */
export interface RuleError {
  code: string
  params: Record<string, string>
}

/** A computed value outcome (`ComputedValue`). Exactly one of `value`/`error` per `state`. */
export interface ComputedValue {
  state: ValueState
  value?: unknown
  error?: RuleError
}

/** A validation verdict (`Validity`). `error` present iff `ok` is false. */
export interface Validity {
  ok: boolean
  error?: RuleError
}

/** Merged show/hide/required/readonly state for a cell (`VisibilityState`). */
export interface VisibilityState {
  visible: boolean
  required: boolean
  readOnly: boolean
}

/** A presentation hint outcome (`PresentationOutcome`). */
export interface PresentationOutcome {
  severity?: Severity | null
  badge?: InternationalizedText
  styleToken?: string
}

/** The single neutral evaluation result both tiers emit (`RuleOutcome`). One payload per `outputType`. */
export interface RuleOutcome {
  ruleId: string
  /** Canonical cell key: `field:x` / `row:s/r/f` / `agg:s/fn/col` / `section:id` / `schema:`. */
  target: string
  outputType: OutputType
  value?: ComputedValue
  validity?: Validity
  visibility?: VisibilityState
  presentation?: PresentationOutcome
}

// ── Pages / wizard steps (F-14 — goal §10, 2026-07-01) ────────────────────────
//
// A form MAY declare ordered pages; pages contain the existing sections (which
// contain fields / containers — the Rev-7 item tree is untouched). Pages are a
// PRESENTATION/NAVIGATION grain layered ABOVE sections: `FormPage.sections` lists
// section ids the same way `FormSection.fields` lists field names (parent lists
// children). ADDITIVE + back-compat non-negotiable: a pageless definition omits
// `pages`/`wizard` entirely (byte-identical on the wire) and renders exactly as
// today — one implicit page.

/**
 * Declarative on-success config (`OnSuccessConfig`) — CONFIG FIELDS ONLY, no
 * imperative code in the definition. The HOST consumes them after a successful
 * final submit: `redirectUrl` is where the host navigates; `hostCallback` is the
 * name of a host-registered callback to invoke. Both optional.
 */
export interface OnSuccessConfig {
  redirectUrl?: string
  hostCallback?: string
}

/**
 * Wizard chrome settings (`WizardSettings`) for a paged form. All additive:
 * absent ⇒ defaults (no review step; confirmation step shown with the renderer's
 * default localized message).
 */
export interface WizardSettings {
  /** Auto-generated read-only review step before the final submit. Default `false`. */
  review?: boolean
  /** Show the confirmation step after a successful submit. Default `true` (absent ⇒ shown). */
  confirmation?: boolean
  /** Custom localized confirmation message; absent ⇒ the renderer's default string. */
  confirmationMessage?: InternationalizedText
  /** Declarative on-success behavior the host consumes. */
  onSuccess?: OnSuccessConfig
}

/**
 * One wizard page/step (`FormPage`). Carries an ordered list of section ids
 * (each MUST exist in `HarborlineOverlay.sections`; when pages are declared, every
 * section MUST be assigned to exactly one page — the store enforces this
 * fail-closed at registration).
 *
 * `visibleWhen` is an optional SPINE-1 (shipyard-jsonlogic/v1) boolean guard
 * expression, JSON-stringified — the SAME expression language rules use
 * (ratified Decision 1: expression-only). A page whose guard evaluates false is
 * SKIPPED by wizard navigation; flow is always linear next/prev over the
 * currently-visible pages (navigation is NEVER a rule output — no goto). Guard
 * evaluation is fail-closed: an erroring/pending guard hides the page.
 */
export interface FormPage {
  id: string
  title: InternationalizedText
  /** Ordered section ids on this page (must appear in `HarborlineOverlay.sections`). */
  sections: string[]
  /** Optional SPINE-1 JsonLogic visibility guard (stringified). False/error ⇒ page skipped. */
  visibleWhen?: string
  /**
   * Optional page-level validation checks (F-20): ordered ids of `Validate`-action
   * rules in `HarborlineOverlay.rules`. The wizard gates Next/submit on this page on
   * every referenced rule's Validity being ok (fail-closed: an erroring/pending
   * check blocks). Checks are RULES (ratified Decision 1: expression-only) — this
   * member only BINDS them to a page; there is no page-local expression grammar.
   * Admission rejects an unknown id or a non-`Validate` rule reference.
   */
  checks?: string[]
}

// ── Async validation checks (F-20 — lookup-backed, e.g. duplicate-vendor) ──────

/**
 * A lookup-backed asynchronous validation check (F-20). Unlike a SPINE-1 rule
 * (a pure expression over the candidate), an async check calls a HOST-REGISTERED,
 * contract-typed connector (e.g. a duplicate-vendor lookup) with the named input
 * values and attaches its verdict to `field`.
 *
 * Runtime semantics (client tier): debounced while typing (never blocks input),
 * cancellable (a newer keystroke aborts the in-flight call), with the pending /
 * failure states surfaced as stable localizable codes. At page-Next and at final
 * submit the check is FAIL-CLOSED: a failed check blocks with `failCode`; an
 * unreachable/errored connector blocks with `forms.check.unavailable`; a check
 * still pending blocks with `forms.check.pending`.
 *
 * The definition carries CONFIG ONLY (connector name + field wiring + codes) —
 * no imperative code, mirroring the `OnSuccessConfig` precedent. Server-side
 * re-execution of connectors is a follow-up (the node validates the config
 * fail-closed at admission but does not call connectors at submit).
 */
export interface AsyncValidationCheck {
  /** Stable check id, unique within the overlay. */
  id: string
  /** Registry key of the host-registered connector this check calls. */
  connector: string
  /** The field name the verdict attaches to (must exist in `HarborlineOverlay.fields`). */
  field: string
  /**
   * Additional field names whose values feed the connector's input bag (the
   * `field` value is always included). Each must exist in `HarborlineOverlay.fields`.
   */
  inputs?: string[]
  /**
   * Stable, locale-independent failure code surfaced when the connector reports a
   * negative verdict (e.g. `duplicate-vendor`). The client localizes off this;
   * the connector may add structured params.
   */
  failCode: string
  /** Debounce while typing, in milliseconds. Default 400. */
  debounceMs?: number
  /**
   * SPINE-2 (ADR 0140 D2 §6): an explicit author acknowledgment that this check
   * MAY feed fields whose effective classification is sensitive (a class-required
   * tag — `pii`/`identifier`/`phi`/`pci`/`cui`) to its host connector. Absent /
   * `false` ⇒ admission REJECTS the definition fail-closed if the check's target
   * or any input field resolves to a sensitive class (`aspect.sensitive_input_unacknowledged`),
   * so a tagged value is never silently handed to a connector. Additive; absent ⇒
   * byte-identical to a pre-SPINE-2 check.
   */
  allowsSensitiveInputs?: boolean
}

/** The Harborline overlay carried alongside a `FormDefinition`'s JSON Schema (`HarborlineOverlay`). */
export interface HarborlineOverlay {
  /** Per-field overlay map keyed by JSON property name. */
  fields: Record<string, FieldOverlay>
  /** Ordered sections; section order is the default field-presentation order. */
  sections: FormSection[]
  /** Cross-field rules (interpreted by the rule evaluator; stored as data here). */
  rules: RuleDefinition[]
  title?: InternationalizedText
  description?: InternationalizedText
  /** SPINE-2 (ADR 0140 D2) form-grain aspect overlay — the coarsest resolver grain. */
  aspects?: AspectOverlay
  /**
   * Optional ordered wizard pages (F-14). Present ⇒ the renderer is a multi-step
   * wizard over the pages; absent ⇒ a pageless definition, byte-identical to
   * pre-F-14 and rendered as today (one implicit page).
   */
  pages?: FormPage[]
  /** Optional wizard chrome settings (F-14). Only meaningful when `pages` is present. */
  wizard?: WizardSettings
  /**
   * Optional lookup-backed async validation checks (F-20). Additive; absent ⇒
   * byte-identical to a pre-F-20 definition.
   */
  asyncChecks?: AsyncValidationCheck[]
}

/** Optional extension lineage — this definition extends another (`FormDefinitionLineage`). */
export interface FormDefinitionLineage {
  parentDefinitionId: string
  /** Canonical `"{major}.{minor}.{patch}"`. */
  parentVersion: string
}

/** An authoring/owner identity reference (`IdentityRef`); wire form `"{scheme}:{value}"`. */
export interface IdentityRef {
  scheme: string
  value: string
}

/**
 * The canonical dynamic-forms keystone record (`FormDefinition`). Carries a
 * content-addressed `schemaRef` into the kernel schema registry (the JSON
 * Schema blob lives there, NOT inlined) plus the Harborline `overlay` + lifecycle.
 */
export interface FormDefinition {
  id: string
  /** Canonical `"{major}.{minor}.{patch}"`. */
  version: string
  status: FormDefinitionStatus
  /** Owning tenant id. */
  tenant: string
  owner: IdentityRef
  /** Content-addressed CID into the kernel schema registry (`SchemaId`). */
  schemaRef: string
  overlay: HarborlineOverlay
  lineage?: FormDefinitionLineage
  /** ISO-8601 UTC. */
  createdAt: string
  /** ISO-8601 UTC. */
  updatedAt: string
}

// ── View (render side — mirrors foundation-forms-engine + FormsRoutes DTOs) ────

/**
 * A single field within a rendered `FormViewSection` (`FormViewField`).
 * PII / role-gated fields are returned with `value: null` and
 * `isReadable: false`; the engine never places PII cleartext into a view.
 */
export interface FormViewField {
  name: string
  label: InternationalizedText
  helpText?: InternationalizedText | null
  controlHint?: string | null
  /** True when the field is PII-classified — its value is never populated. */
  isSensitive: boolean
  /** True when the active token's roles may read this field's section and it is not PII. */
  isReadable: boolean
  /**
   * The field's value, populated only for a readable, non-PII field of a
   * bound instance. `null`/absent otherwise. `unknown` because the value's
   * shape is the field's JSON-Schema type (string/number/object/array/…).
   */
  value?: unknown
  /**
   * Whether the field is rendered read-only — shown but not editable (a disabled
   * input), DISTINCT from a redacted PII row (`isReadable: false`). Projected from
   * a `ReadOnly` rule's {@link VisibilityState.readOnly} (SPINE-1). Additive;
   * absent ⇒ editable (today's behavior). Server-populated once the deferred
   * render-time rule projection lands; until then a client projection sets it.
   */
  readOnly?: boolean
  /**
   * Optional presentation hint (badge / severity / style token) projected from a
   * `Presentation` rule outcome (SPINE-1 Decision DA). Additive; absent ⇒ no badge.
   */
  presentation?: PresentationOutcome
}

/**
 * One node in a rendered `FormViewSection`'s item tree (`FormViewItem`, ADR 0055
 * Rev 7 — nested sub-form items). The render-side mirror of the authoring `FormItem`:
 * a `field` node carries a rendered `FormViewField`; a `group` / `collection` node
 * carries child `items` ⇒ arbitrary depth. What the recursive `SchemaForm` renderer
 * walks.
 */
export type FormViewItem =
  | { kind: 'field'; key: string; field: FormViewField }
  | {
      kind: 'group'
      key: string
      title?: InternationalizedText
      items: FormViewItem[]
      /** F-23 zone intents, projected from the authoring group. Absent ⇒ legacy column. */
      layout?: SectionLayout
      /** F-23: per-child placement keyed by child item key. */
      placement?: Record<string, FieldPlacement>
    }
  | {
      kind: 'collection'
      key: string
      title?: InternationalizedText
      items: FormViewItem[]
      cardinality?: Cardinality
      /** F-24 (item 5): optional tabular presentation projected from the authoring
       * collection. Absent ⇒ the legacy stacked "list" presentation. */
      table?: CollectionTableConfig
    }
  | { kind: 'content'; key: string; content: ContentNode[] }
  | { kind: 'action'; key: string; action: FormActionConfig }

/** A section of a `FormView` — a titled group of fields (`FormViewSection`). */
export interface FormViewSection {
  id: string
  title: InternationalizedText
  fields: FormViewField[]
  /**
   * Optional 2D layout projected from the authoring section (ADR 0055 Rev 6).
   * Absent ⇒ `'stack'`. Presentation-only; does not affect authorization.
   */
  layout?: SectionLayout
  /** Optional per-field placement keyed by field name (grid colSpan / flex grow). */
  fieldPlacement?: Record<string, FieldPlacement>
  /**
   * Optional rendered item tree (ADR 0055 Rev 7 — nested sub-form items). Present ⇒
   * the renderer walks the tree (nested groups / repeatable collections); absent ⇒
   * the flat `fields`, byte-identical to a pre-Rev-7 view.
   */
  items?: FormViewItem[]
}

/**
 * The rendered, localized projection of a form definition, optionally bound to
 * an instance (`FormView`). What `GET /api/local-node/forms/{formId}` returns;
 * what the React `SchemaForm` renderer consumes.
 */
export interface FormView {
  formId: string
  /** Canonical `"{major}.{minor}.{patch}"`. */
  version: string
  title?: InternationalizedText | null
  description?: InternationalizedText | null
  sections: FormViewSection[]
}

// ── Validation (mirrors foundation-forms-engine ValidationResult) ─────────────

/** The category of a `ValidationError` (`ValidationErrorKind`). */
export type ValidationErrorKind = 'Schema' | 'ResourceBound' | 'Authorization' | 'NotFound'

/** A single validation failure, located by JSON Pointer (`ValidationError`). */
export interface ValidationError {
  /** RFC 6901 pointer into the candidate document (empty string = root). */
  jsonPointer: string
  /**
   * Human-readable English failure description — the FALLBACK a client renders when it has no
   * localized template for {@link code}. A localizing client keys a translated template off
   * `code` and interpolates {@link params} instead.
   */
  message: string
  kind: ValidationErrorKind
  /**
   * Stable, locale-independent failure code (the failing JSON-Schema keyword —
   * `required` / `type` / `minimum` / `maximum` / `enum` / `minLength` / `maxLength` /
   * `pattern` / `additional-properties` / … — or an engine code like `not-object` /
   * `candidate-too-large` / `form-not-found`). A client keys a localized message template
   * off this. Absent only for a legacy error with no resolvable code.
   */
  code?: string
  /**
   * The error's structured values for client interpolation — e.g. `{ min: '1' }`,
   * `{ max: '5' }`, `{ allowed: '["pass","fail"]' }`, `{ field: 'assetId' }`. Absent when
   * the code carries no parameters.
   */
  params?: Record<string, string>
}

/** Outcome of validate-for-save (`ValidationResult`); the 422 body on submit. */
export interface ValidationResult {
  isValid: boolean
  errors: ValidationError[]
}

/** 201 response after a successful `POST /api/local-node/forms/{formId}/submit`. */
export interface FormSubmitResponse {
  /** The new instance's canonical entity id (`scheme:authority/localPart`). */
  instanceId: string
}

// ── D3 submission payload (ADR 0140 amendment 2026-07-01) ─────────────────────
//
// The immutable submission payload = FINAL VALUES ONLY (a Compute-action output is
// itself a final value) + a mandatory binding header, with a CONDITIONAL, governed
// visibility/derived-state snapshot. These types MIRROR the canonical .NET shapes in
// packages/foundation-forms/Submission/*.cs and the JSON the engine writes onto the
// Op.Mint audit envelope (`SubmissionBindingHeader.WriteTo` + the FormEngine snapshot
// writer). The final values themselves live in the entity store, referenced by
// `schemaRef` + the minted instance id — they are not repeated in this envelope.

/** The canonical SPINE-1 rule/compute engine identifier stamped into a binding header. */
export const HARBORLINE_JSONLOGIC_V1 = 'shipyard-jsonlogic/v1'

/**
 * The mandatory, near-free binding header stamped on every submission
 * (`SubmissionBindingHeader`). The "prove what was submitted, under which schema +
 * rules + locale" record — a handful of identifiers + a timestamp, no field data.
 */
export interface SubmissionBindingHeader {
  /** Content-addressed CID of the JSON Schema the submission validated against. */
  schemaRef: string
  /** The form definition id. */
  definitionId: string
  /** Canonical `"{major}.{minor}.{patch}"` version of the definition revision. */
  definitionVersion: string
  /** The rule/compute engine identifier in force (e.g. `"shipyard-jsonlogic/v1"`). */
  engineVersion: string
  /** Ordered actor locale-preference chain (RFC 5646 tags) in force at submit time. */
  localeChain: string[]
  /** ISO-8601 UTC instant the submission was persisted. */
  submittedAt: string
}

/**
 * How (if at all) a submission's visibility/derived-state snapshot was captured
 * (`SnapshotCaptureMode`). The fat snapshot is NOT a default — an ordinary submission
 * captures nothing (no `snapshot` key at all on its audit envelope).
 */
export type SnapshotCaptureMode = 'none' | 'full-projection' | 'signed-dtbs-hash'

/**
 * The GDPR-minimal signed artifact for a signed submission (`SignedDtbsHash`): a hash
 * of the rendered data-to-be-signed + a signature over that hash, stored WITHOUT any
 * cleartext projection. `dtbsHash` / `signature` are base64 strings on the wire.
 */
export interface SignedDtbsHash {
  hashAlgorithm: string
  /** base64. */
  dtbsHash: string
  signatureAlgorithm: string
  /** base64. */
  signature: string
  publicKeyRef?: string
}

/**
 * The conditional, governed snapshot artifact (`SubmissionSnapshot`). Present on the
 * audit envelope ONLY when the capture gate fired (signed submission, or a form
 * carrying a SPINE-2 `capture-as-shown` / compliance-grade tag). Exactly one payload
 * member is populated per `mode`.
 */
export interface SubmissionSnapshot {
  mode: Exclude<SnapshotCaptureMode, 'none'>
  /** ISO-8601 UTC capture instant. */
  capturedAt: string
  /** Populated iff `mode === 'full-projection'` — the as-shown projection (at-rest body). */
  projection?: unknown
  /** Populated iff `mode === 'signed-dtbs-hash'`. */
  signed?: SignedDtbsHash
}

/**
 * The D3 submission provenance record as it rides the `Op.Mint` audit envelope
 * (`form-instance-mint`). Additive over the pre-D3 payload — the legacy top-level
 * `op` / `form` / `version` / `encryptedFields` keys are preserved; `binding` is
 * always present and `snapshot` is present only when the gate fired.
 */
export interface SubmissionMintAuditPayload {
  op: 'form-instance-mint'
  /** Legacy alias of `binding.definitionId` (pre-D3 back-compat). */
  form: string
  /** Legacy alias of `binding.definitionVersion` (pre-D3 back-compat). */
  version: string
  encryptedFields: string[]
  binding: SubmissionBindingHeader
  /** Absent for an ordinary (minimized) submission — its absence is the minimization proof. */
  snapshot?: SubmissionSnapshot
}

// ── D2 save-and-resume submission drafts (ADR 0135 amendment 2026-07-01) ──────
//
// A submission-draft is the mutable, pre-submission working state of a form, keyed
// server-side by (TenantId, case/subject id, PartyId). The client mints the case id
// locally (a GUID/ULID) so starting a draft needs no server round-trip. These MIRROR the
// node wire DTOs in apps/local-node-host/Health/FormDraftRoutes.cs (camelCase JSON) over:
//   PUT    /api/local-node/forms/{formId}/drafts/{caseId}
//   GET    /api/local-node/forms/{formId}/drafts/{caseId}
//   DELETE /api/local-node/forms/{formId}/drafts/{caseId}
//   GET    /api/local-node/forms/drafts

/** The PUT-draft request body: the partial candidate values + an optional data subject. */
export interface DraftSaveRequest {
  /** The partial candidate field values (a JSON object). */
  values: Record<string, unknown>
  /** Optional data subject the values pertain to (for legal-hold / retention / crypto-shred visibility). */
  subjectId?: string
}

/** 200 response after a successful draft save — echoes the resolved key parts. */
export interface DraftSavedResponse {
  /** The client-minted case id the draft is keyed by. */
  caseId: string
  /** The server-resolved tenant id. */
  tenantId: string
  /** The server-derived party id ("N"-format GUID). */
  partyId: string
}

/** A resumed draft on the wire (the GET/list response element). */
export interface DraftView {
  caseId: string
  formId: string
  /** The partial candidate values to rehydrate the form with. */
  values: Record<string, unknown>
  subjectId?: string | null
  /** ISO-8601 UTC last-saved instant. */
  updatedAt: string
}

// ── SPINE-2 aspect overlay (ADR 0140 D2) — additive on FieldOverlay/FormSection/HarborlineOverlay ──
// Mirrors packages/foundation-forms/Models/AspectOverlay.cs (the .NET model is canonical).

/** Field mutability posture (`Immutability`). Ordinal — raises monotonically. */
export type Immutability = 'Mutable' | 'AppendOnly' | 'WriteOnce'

/** How a field value originates (`ProvenanceKind`). */
export type ProvenanceKind = 'Stored' | 'Computed' | 'Imported'

/** Reporting role of a field (`MeasureRole`). */
export type MeasureRole = 'None' | 'Measure' | 'Dimension'

/** An open-vocabulary classification token — a CodeableConcept (ADR 0056) (`Tag`). */
export interface Tag {
  /** The coding system (e.g. `shipyard/data-classification`). Identity is `(system, code)`. */
  system: string
  code: string
  display?: string
}

/** The classification aspect (#5) — tags that drive policy (`ClassificationAspect`). */
export interface ClassificationAspect {
  tags: Tag[]
}

/** The access aspect (#6) narrowing — monotonic-tighten over `SectionAccess` (`AccessAspect`). */
export interface AccessAspect {
  readRoles?: RoleReference[]
  writeRoles?: RoleReference[]
  readStandings?: RecordStandingReference[]
  writeStandings?: RecordStandingReference[]
  readConditionExpression?: string
}

/** A declared retention floor (`RetentionRequirement`). */
export interface RetentionRequirement {
  /** Open-vocab regulatory regime token (e.g. `HIPAA`). */
  regime: string
  /** Audit-event-class token (e.g. `Identity`) the class→AuditEventClass bridge maps. */
  floorClass: string
  /** Authoring-declared minimum hold, in days, for the monotonic-strengthen comparison. */
  minimumRetentionDays: number
}

/** A declared data-residency constraint (`ResidencyRequirement`). */
export interface ResidencyRequirement {
  /** ISO jurisdiction codes the value MAY reside in (intersected across grains). */
  allowedJurisdictions: string[]
  prohibitedJurisdictions?: string[]
}

/** Optional provenance (`Provenance`). */
export interface Provenance {
  kind: ProvenanceKind
  /** Rule id (Computed) or source system (Imported); else absent. */
  source?: string
}

/** The lifecycle aspect (#7) — retention · residency · immutability · provenance (`LifecycleAspect`). */
export interface LifecycleAspect {
  retention?: RetentionRequirement
  residency?: ResidencyRequirement
  /** Default `'Mutable'` on the .NET side. */
  immutability?: Immutability
  provenance?: Provenance
}

/** The discovery aspect (#8) — search / reporting facets (`DiscoveryAspect`). */
export interface DiscoveryAspect {
  searchable?: boolean
  identifier?: boolean
  facet?: boolean
  /** Default `'None'` on the .NET side. */
  measure?: MeasureRole
  reportable?: boolean
}

/** The additive eight-aspect overlay layered on a grain (`AspectOverlay`). */
export interface AspectOverlay {
  classification?: ClassificationAspect
  access?: AccessAspect
  lifecycle?: LifecycleAspect
  discovery?: DiscoveryAspect
}
