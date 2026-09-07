/**
 * Contract test for the dynamic-forms `forms` namespace (ADR 0055).
 *
 * These TS types MIRROR the .NET forms-engine shapes (the .NET model is
 * canonical). There is no shared JSON fixture, so the drift-detection here is a
 * STRUCTURAL contract test in two layers:
 *
 *  1. Compile-time (`satisfies`): a representative wire payload — exactly the
 *     camelCase JSON the local-node-host `FormsRoutes` DTOs emit — is asserted
 *     to satisfy the TS interfaces. A field added to / removed from a DTO on the
 *     .NET side (mirrored here) breaks this build; a typo in a field name breaks
 *     this build.
 *  2. Runtime: exhaustive guards over the CLOSED enums (FormDefinitionStatus,
 *     PiiSensitivity, RuleTier, RuleScope, RuleActionKind, ValidationErrorKind)
 *     assert they are EXACTLY the members the .NET enums declare — widening one
 *     on the TS side without the .NET side breaks the `satisfies` exhaustiveness.
 *
 * The discriminating assertions: the `FormView` wire shape (what the React
 * SchemaForm renderer consumes) and the `FormDefinition` authoring shape (what a
 * domain packet carries) both round-trip a realistic payload.
 */

import { describe, it, expect } from 'vitest'

import type {
  Cardinality,
  ControlHint,
  FieldConfig,
  FieldOverlay,
  FieldPlacement,
  FieldWidth,
  FormActionKind,
  FormDefinition,
  FormDefinitionStatus,
  FormItem,
  FormItemKind,
  FormPage,
  LayoutAlign,
  LayoutBreakpoint,
  LayoutDensity,
  FormSubmitResponse,
  FormView,
  FormViewField,
  FormViewItem,
  FormViewSection,
  InternationalizedText,
  OutputType,
  PiiSensitivity,
  PresentationHint,
  PresentationOutcome,
  RuleActionKind,
  RuleDefinition,
  RuleOutcome,
  RuleScope,
  RuleTier,
  ValueState,
  SectionAccess,
  SectionLayout,
  SectionLayoutKind,
  HarborlineOverlay,
  AspectOverlay,
  Immutability,
  MeasureRole,
  ProvenanceKind,
  ValidationError,
  ValidationErrorKind,
  ValidationResult,
  SubmissionBindingHeader,
  SnapshotCaptureMode,
  SubmissionSnapshot,
  SubmissionMintAuditPayload,
} from '../forms.js'
import { HARBORLINE_JSONLOGIC_V1 } from '../forms.js'
import { parseRecordStandingReference, parseRoleReference } from '../authorization-references.js'

const tenantAdmin = parseRoleReference('tenant.roles/admin')
const vendorMaintenance = parseRoleReference('vendor.roles/maintenance')
const handlerStanding = parseRecordStandingReference('handler')

const i18n: InternationalizedText = {
  defaultLocale: 'en',
  values: { en: 'Address', 'ar-AE': 'العنوان' },
}

/** The content-block variant of the FormItem union (F-23). */
type ContentBlockItem = Extract<FormItem, { kind: 'content' }>

describe('forms — FormDefinition authoring shape (mirrors foundation-forms)', () => {
  it('round-trips a representative FormDefinition (packet-carried authoring shape)', () => {
    const access: SectionAccess = {
      readRoles: [tenantAdmin, vendorMaintenance],
      writeRoles: [tenantAdmin],
      readStandings: [handlerStanding],
      writeStandings: [handlerStanding],
      readConditionExpression: undefined,
    }

    const fieldOverlay: FieldOverlay = {
      label: i18n,
      helpText: { defaultLocale: 'en', values: { en: 'Street address' } },
      controlHint: 'address',
      piiSensitivity: 'Sensitive',
      readRoles: [tenantAdmin],
      writeRoles: [tenantAdmin],
      readStandings: [handlerStanding],
      writeStandings: [handlerStanding],
    }

    const rule: RuleDefinition = {
      id: 'section.remediation.required',
      tier: 'JsonSchema',
      scope: 'Section',
      scopeTarget: 'remediation',
      expression: '{"if":{"properties":{"result":{"const":"FAIL"}}}}',
      action: 'Required',
      errorMessage: { defaultLocale: 'en', values: { en: 'Remediation notes are required on FAIL.' } },
    }

    const overlay: HarborlineOverlay = {
      fields: { addressLine1: fieldOverlay },
      sections: [{ id: 'location', title: i18n, fields: ['addressLine1'], access }],
      rules: [rule],
      title: { defaultLocale: 'en', values: { en: 'Inspection' } },
      description: undefined,
      aspects: { access: { readStandings: [handlerStanding], writeStandings: [handlerStanding] } },
    }

    const def = {
      id: 'inspection.metro.v1',
      version: '1.0.0',
      status: 'Published',
      tenant: 'acme-rail',
      owner: { scheme: 'system', value: '__sunfish' },
      schemaRef: 'sha256:cafef00d',
      overlay,
      lineage: undefined,
      createdAt: '2026-06-25T00:00:00.0000000+00:00',
      updatedAt: '2026-06-25T00:00:00.0000000+00:00',
    } satisfies FormDefinition

    expect(def.overlay.sections[0].fields).toEqual(['addressLine1'])
    expect(def.overlay.fields.addressLine1.controlHint satisfies ControlHint).toBe('address')
    expect(def.overlay.fields.addressLine1.piiSensitivity).toBe('Sensitive')
  })
})

describe('forms — FormView render shape (mirrors foundation-forms-engine + FormsRoutes DTO)', () => {
  it('round-trips the camelCase wire payload the node host returns', () => {
    // Exactly the JSON FormViewDto serialises (System.Text.Json Web defaults =
    // camelCase). A non-readable / PII field carries value: null.
    const wire = {
      formId: 'inspection.metro.v1',
      version: '1.0.0',
      title: { defaultLocale: 'en', values: { en: 'Inspection' } },
      description: null,
      sections: [
        {
          id: 'location',
          title: i18n,
          fields: [
            {
              name: 'addressLine1',
              label: i18n,
              helpText: null,
              controlHint: 'address',
              isSensitive: true,
              isReadable: false,
              value: null,
            },
            {
              name: 'result',
              label: { defaultLocale: 'en', values: { en: 'Result' } },
              helpText: null,
              controlHint: 'text',
              isSensitive: false,
              isReadable: true,
              value: 'PASS',
            },
          ],
        },
      ],
    } satisfies FormView

    const sensitive = wire.sections[0].fields[0]
    expect(sensitive.isSensitive).toBe(true)
    expect(sensitive.value).toBeNull()

    const readable = wire.sections[0].fields[1] satisfies FormViewField
    expect(readable.isReadable).toBe(true)
    expect(readable.value).toBe('PASS')
  })

  it('models a submit response + validation-failure body', () => {
    const created = { instanceId: 'forminst:forms/abc123' } satisfies FormSubmitResponse
    expect(created.instanceId).toContain('forminst:')

    const err: ValidationError = { jsonPointer: '/result', message: 'required', kind: 'Schema' }
    const result = { isValid: false, errors: [err] } satisfies ValidationResult
    expect(result.isValid).toBe(false)
    expect(result.errors[0].kind).toBe('Schema')
  })
})

describe('forms — SectionLayout extension (ADR 0055 Rev 6, presentation-only)', () => {
  it('round-trips an authoring FormSection carrying a grid layout + field placement', () => {
    const layout: SectionLayout = { kind: 'grid', columns: 3, gap: 6 }
    const placement: Record<string, FieldPlacement> = {
      addressLine1: { colSpan: 2 },
      city: { colSpan: 1 },
    }
    const access: SectionAccess = { readRoles: [tenantAdmin], writeRoles: [tenantAdmin] }

    // The layout/fieldPlacement coexist with — and never replace — `access`.
    const section = {
      id: 'location',
      title: i18n,
      fields: ['addressLine1', 'city'],
      access,
      layout,
      fieldPlacement: placement,
    } satisfies FormSection

    expect(section.layout?.kind).toBe('grid')
    expect(section.layout?.columns).toBe(3)
    expect(section.fieldPlacement?.addressLine1.colSpan).toBe(2)
    // Presentation-only: the security half is untouched and still present.
    expect(section.access.readRoles).toContain(tenantAdmin)
  })

  it('round-trips a flex layout (direction/wrap/grow) on the render FormViewSection wire shape', () => {
    // Exactly the camelCase JSON FormViewSectionDto serialises — lowercase enum
    // string unions for kind/direction/wrap, numeric placement.
    const wire = {
      id: 'location',
      title: i18n,
      fields: [],
      layout: { kind: 'flex', direction: 'row', wrap: 'wrap', gap: 4 },
      fieldPlacement: { addressLine1: { grow: 1 } },
    } satisfies FormViewSection

    expect(wire.layout?.kind).toBe('flex')
    expect(wire.layout?.direction).toBe('row')
    expect(wire.layout?.wrap).toBe('wrap')
    expect(wire.fieldPlacement?.addressLine1.grow).toBe(1)
  })

  it('back-compat: a section with no layout is a valid FormSection / FormViewSection (stack)', () => {
    const authoring = {
      id: 'legacy',
      title: i18n,
      fields: ['a'],
      access: { readRoles: [], writeRoles: [] },
    } satisfies FormSection
    const view = { id: 'legacy', title: i18n, fields: [] } satisfies FormViewSection

    // Absent layout ⇒ the renderer treats it as 'stack'; the types make it optional.
    expect(authoring.layout).toBeUndefined()
    expect(view.layout).toBeUndefined()
  })

  it('SectionLayoutKind = { stack, flex, grid }', () => {
    const exhaustive = { stack: 1, flex: 1, grid: 1 } satisfies Record<SectionLayoutKind, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['flex', 'grid', 'stack'])
  })
})

describe('forms — pages / wizard steps (F-14, additive)', () => {
  it('round-trips an overlay carrying pages (ordered section ids + a visibleWhen guard) + wizard settings', () => {
    // Exactly the camelCase JSON the node's FormPageDto/WizardSettingsDto serialise.
    const overlay = {
      fields: {},
      sections: [],
      rules: [],
      pages: [
        { id: 'page-1', title: i18n, sections: ['sec-a'] },
        {
          id: 'page-2',
          title: i18n,
          sections: ['sec-b'],
          visibleWhen: '{"==":[{"var":"employment"},"employed"]}',
        },
      ],
      wizard: {
        review: true,
        confirmation: true,
        confirmationMessage: i18n,
        onSuccess: { redirectUrl: 'https://example.test/done', hostCallback: 'formDone' },
      },
    } satisfies HarborlineOverlay

    expect(overlay.pages).toHaveLength(2)
    expect(overlay.pages?.[0].visibleWhen).toBeUndefined()
    expect(overlay.pages?.[1].sections).toEqual(['sec-b'])
    expect(overlay.wizard?.onSuccess?.hostCallback).toBe('formDone')
  })

  it('back-compat: a pageless overlay is a valid HarborlineOverlay (pages/wizard optional)', () => {
    const overlay = { fields: {}, sections: [], rules: [] } satisfies HarborlineOverlay
    expect(overlay.pages).toBeUndefined()
    expect(overlay.wizard).toBeUndefined()
  })

  it('a FormPage lists sections the way a FormSection lists fields (parent lists children)', () => {
    const page = { id: 'p', title: i18n, sections: ['s1', 's2'] } satisfies FormPage
    expect(page.sections).toEqual(['s1', 's2'])
  })
})

describe('forms — closed enums match the .NET enums exactly', () => {
  // Each guard's `satisfies` clause forces exhaustiveness — adding a member to
  // the TS union without listing it here is a compile error, and listing a
  // member the union does not have is also a compile error.

  it('FormDefinitionStatus = { Draft, Published, Deprecated, Withdrawn }', () => {
    const all: FormDefinitionStatus[] = ['Draft', 'Published', 'Deprecated', 'Withdrawn']
    const exhaustive = {
      Draft: 1, Published: 1, Deprecated: 1, Withdrawn: 1,
    } satisfies Record<FormDefinitionStatus, number>
    expect(Object.keys(exhaustive).sort()).toEqual([...all].sort())
  })

  it('PiiSensitivity = { None, Sensitive }', () => {
    const exhaustive = { None: 1, Sensitive: 1 } satisfies Record<PiiSensitivity, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['None', 'Sensitive'])
  })

  it('RuleTier = { JsonSchema, JsonLogic, PowerFx }', () => {
    const exhaustive = { JsonSchema: 1, JsonLogic: 1, PowerFx: 1 } satisfies Record<RuleTier, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['JsonLogic', 'JsonSchema', 'PowerFx'])
  })

  it('RuleScope = { Field, Section, Schema, Row, Table }', () => {
    const exhaustive = { Field: 1, Section: 1, Schema: 1, Row: 1, Table: 1 } satisfies Record<RuleScope, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['Field', 'Row', 'Schema', 'Section', 'Table'])
  })

  it('RuleActionKind = { Visibility, Required, ReadOnly, Validate, Compute, Presentation }', () => {
    const exhaustive = {
      Visibility: 1, Required: 1, ReadOnly: 1, Validate: 1, Compute: 1, Presentation: 1,
    } satisfies Record<RuleActionKind, number>
    expect(Object.keys(exhaustive).sort()).toEqual(
      ['Compute', 'Presentation', 'ReadOnly', 'Required', 'Validate', 'Visibility'])
  })

  it('OutputType = { Value, Validity, Visibility, Presentation }', () => {
    const exhaustive = { Value: 1, Validity: 1, Visibility: 1, Presentation: 1 } satisfies Record<OutputType, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['Presentation', 'Validity', 'Value', 'Visibility'])
  })

  it('ValueState = { Resolved, Error, Pending }', () => {
    const exhaustive = { Resolved: 1, Error: 1, Pending: 1 } satisfies Record<ValueState, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['Error', 'Pending', 'Resolved'])
  })

  it('RuleOutcome + PresentationHint round-trip the SPINE-1 engine shapes', () => {
    const computed = {
      ruleId: 'c.total', target: 'field:total', outputType: 'Value',
      value: { state: 'Resolved', value: 60 },
    } satisfies RuleOutcome
    expect(computed.value?.state).toBe('Resolved')

    const errored = {
      ruleId: 'c.ratio', target: 'field:ratio', outputType: 'Value',
      value: { state: 'Error', error: { code: 'rule.div_by_zero', params: {} } },
    } satisfies RuleOutcome
    expect(errored.value?.error?.code).toBe('rule.div_by_zero')

    const presented = {
      ruleId: 'p.low', target: 'field:score', outputType: 'Presentation',
      presentation: { severity: 'warn', styleToken: 'low' },
    } satisfies RuleOutcome
    expect(presented.presentation?.severity).toBe('warn')

    const hint = { severity: 'error', styleToken: 'danger' } satisfies PresentationHint
    expect(hint.severity).toBe('error')
  })

  it('ValidationErrorKind = { Schema, ResourceBound, Authorization, NotFound }', () => {
    const exhaustive = {
      Schema: 1, ResourceBound: 1, Authorization: 1, NotFound: 1,
    } satisfies Record<ValidationErrorKind, number>
    expect(Object.keys(exhaustive).sort()).toEqual(
      ['Authorization', 'NotFound', 'ResourceBound', 'Schema'])
  })

  it('ControlHint accepts the documented vocabulary AND a packet-supplied novel hint', () => {
    const known: ControlHint = 'taxonomy-coding'
    const novel: ControlHint = 'signal-pad' // (string & {}) — packet may carry an unknown control
    expect([known, novel]).toHaveLength(2)
  })
})


describe('forms — SPINE-2 aspect overlay (ADR 0140 D2, additive)', () => {
  it('round-trips a FieldOverlay carrying a full aspect overlay', () => {
    const aspects = {
      classification: { tags: [{ system: 'shipyard/data-classification', code: 'phi', display: 'PHI' }] },
      access: { readRoles: [tenantAdmin], readStandings: [handlerStanding], readConditionExpression: undefined },
      lifecycle: {
        retention: { regime: 'HIPAA', floorClass: 'Identity', minimumRetentionDays: 2190 },
        residency: { allowedJurisdictions: ['US'] },
        immutability: 'WriteOnce',
        provenance: { kind: 'Stored' },
      },
      discovery: { searchable: true, identifier: false, measure: 'None', reportable: true },
    } satisfies AspectOverlay

    const fieldOverlay = {
      label: i18n,
      piiSensitivity: 'Sensitive',
      aspects,
    } satisfies FieldOverlay

    expect(fieldOverlay.aspects?.classification?.tags[0].code).toBe('phi')
    expect(fieldOverlay.aspects?.lifecycle?.immutability).toBe('WriteOnce')
    expect(fieldOverlay.aspects?.lifecycle?.residency?.allowedJurisdictions).toEqual(['US'])
  })

  it('back-compat: an overlay with no aspects is still a valid FieldOverlay', () => {
    const legacy = { label: i18n } satisfies FieldOverlay
    expect(legacy.aspects).toBeUndefined()
  })

  it('Immutability = { Mutable, AppendOnly, WriteOnce }', () => {
    const exhaustive = { Mutable: 1, AppendOnly: 1, WriteOnce: 1 } satisfies Record<Immutability, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['AppendOnly', 'Mutable', 'WriteOnce'])
  })

  it('MeasureRole = { None, Measure, Dimension }', () => {
    const exhaustive = { None: 1, Measure: 1, Dimension: 1 } satisfies Record<MeasureRole, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['Dimension', 'Measure', 'None'])
  })

  it('ProvenanceKind = { Stored, Computed, Imported }', () => {
    const exhaustive = { Stored: 1, Computed: 1, Imported: 1 } satisfies Record<ProvenanceKind, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['Computed', 'Imported', 'Stored'])
  })
})


describe('forms — FormViewField render-side carriers (FORM-KEY, ADR 0140 D1, additive)', () => {
  it('round-trips readOnly + presentation on a FormViewField (the bounded render extension)', () => {
    const presentation: PresentationOutcome = { severity: 'warn', badge: { defaultLocale: 'en', values: { en: 'Low' } }, styleToken: 'low' }
    const field = {
      name: 'score',
      label: i18n,
      helpText: null,
      controlHint: 'number',
      isSensitive: false,
      isReadable: true,
      value: 42,
      readOnly: true,
      presentation,
    } satisfies FormViewField
    expect(field.readOnly).toBe(true)
    expect(field.presentation?.severity).toBe('warn')
  })

  it('back-compat: a FormViewField with neither readOnly nor presentation is still valid', () => {
    const field = {
      name: 'plain',
      label: i18n,
      isSensitive: false,
      isReadable: true,
    } satisfies FormViewField
    expect(field.readOnly).toBeUndefined()
    expect(field.presentation).toBeUndefined()
  })
})

describe('forms — nested sub-form item tree (ADR 0055 Rev 7, additive)', () => {
  it('FormItemKind = { field, group, collection, reference, content, action }', () => {
    const exhaustive = {
      field: 1,
      group: 1,
      collection: 1,
      reference: 1,
      content: 1,
      action: 1,
    } satisfies Record<FormItemKind, number>
    expect(Object.keys(exhaustive).sort()).toEqual(
      ['action', 'collection', 'content', 'field', 'group', 'reference'])
  })

  it('carries a D4 reference node by unit id + version selector (reuse-by-reference)', () => {
    // Latest-published (propagation channel): pinnedVersion === null.
    const latest: FormItem = {
      kind: 'reference',
      key: 'shipTo',
      reference: { unitId: 'tenant:acme/address-block', version: { pinnedVersion: null } },
    }
    // Pinned (does not drift): a canonical "major.minor.patch" version.
    const pinned: FormItem = {
      kind: 'reference',
      key: 'billTo',
      reference: { unitId: 'tenant:acme/address-block', version: { pinnedVersion: '1.0.0' } },
    }

    expect(latest.kind).toBe('reference')
    if (latest.kind === 'reference') {
      expect(latest.reference.version.pinnedVersion).toBeNull()
      // A reference node has NO inline items — the subtree resolves from the unit.
      expect('items' in latest).toBe(false)
    }
    if (pinned.kind === 'reference') {
      expect(pinned.reference.version.pinnedVersion).toBe('1.0.0')
      expect(pinned.reference.unitId).toBe('tenant:acme/address-block')
    }
  })

  it('round-trips a ≥2-level authoring item tree (group + repeatable collection of fields)', () => {
    const cardinality: Cardinality = { min: 1, max: 50 }
    // A quote form: a top-level total field, a nested "billTo" group, and a
    // repeatable "lineItems" collection whose rows are {description, amount} fields.
    const items: FormItem[] = [
      { kind: 'field', key: 'total' },
      {
        kind: 'group',
        key: 'billTo',
        title: { defaultLocale: 'en', values: { en: 'Bill to' } },
        items: [
          { kind: 'field', key: 'billName' },
          { kind: 'field', key: 'billEmail' },
        ],
      },
      {
        kind: 'collection',
        key: 'lineItems',
        title: { defaultLocale: 'en', values: { en: 'Line items', 'ar-AE': 'البنود' } },
        cardinality,
        items: [
          { kind: 'field', key: 'description' },
          { kind: 'field', key: 'amount' },
        ],
      },
    ]

    // A section carries the tree; its flat `fields` is the top-level fallback.
    const section = {
      id: 'quote',
      title: i18n,
      fields: ['total'],
      access: { readRoles: [tenantAdmin], writeRoles: [tenantAdmin] },
      items,
    } satisfies FormSection

    // Depth-2 proof: the collection contains field leaves two levels below the section.
    const collection = section.items?.[2]
    expect(collection?.kind).toBe('collection')
    if (collection?.kind === 'collection') {
      expect(collection.cardinality?.max).toBe(50)
      expect(collection.items.map((i) => i.key)).toEqual(['description', 'amount'])
      expect(collection.items[0].kind).toBe('field')
    }
    const group = section.items?.[1]
    expect(group?.kind).toBe('group')
  })

  it('round-trips the render-side FormViewItem tree the SchemaForm renderer walks', () => {
    const leaf = (name: string): FormViewField => ({
      name,
      label: { defaultLocale: 'en', values: { en: name } },
      isSensitive: false,
      isReadable: true,
    })
    const items: FormViewItem[] = [
      { kind: 'field', key: 'total', field: leaf('total') },
      {
        kind: 'collection',
        key: 'lineItems',
        title: { defaultLocale: 'en', values: { en: 'Line items' } },
        cardinality: { min: 0 },
        items: [
          { kind: 'field', key: 'description', field: leaf('description') },
          { kind: 'field', key: 'amount', field: leaf('amount') },
        ],
      },
    ]
    const section = {
      id: 'quote',
      title: i18n,
      fields: [leaf('total')],
      items,
    } satisfies FormViewSection

    const coll = section.items?.[1]
    expect(coll?.kind).toBe('collection')
    if (coll?.kind === 'collection') {
      expect(coll.items).toHaveLength(2)
      const first = coll.items[0]
      if (first.kind === 'field') expect(first.field.name).toBe('description')
    }
  })

  it('back-compat: a flat section/view section carries no items (depth-1 tree)', () => {
    const authoring = {
      id: 'legacy',
      title: i18n,
      fields: ['a'],
      access: { readRoles: [], writeRoles: [] },
    } satisfies FormSection
    const view = { id: 'legacy', title: i18n, fields: [] } satisfies FormViewSection
    expect(authoring.items).toBeUndefined()
    expect(view.items).toBeUndefined()
  })
})

describe('forms — F-23 layout breadth (content/action blocks + zone intents, additive)', () => {
  it('round-trips a content block item (heading + paragraph nodes, localized plain text)', () => {
    const item = {
      kind: 'content',
      key: 'instructions',
      content: [
        { kind: 'heading', text: { defaultLocale: 'en', values: { en: 'Before you start', 'ar-AE': 'قبل أن تبدأ' } }, level: 3 },
        { kind: 'paragraph', text: { defaultLocale: 'en', values: { en: 'Inspect every room.' } } },
      ],
    } satisfies FormItem
    const wire = JSON.parse(JSON.stringify(item)) as ContentBlockItem
    expect(wire.content).toHaveLength(2)
    expect(wire.content[0].kind).toBe('heading')
    if (wire.content[0].kind === 'heading') expect(wire.content[0].level).toBe(3)
  })

  it('round-trips both bounded action kinds (config only — never code)', () => {
    const openUrl = {
      kind: 'action',
      key: 'guidelines',
      action: { kind: 'open-url', label: i18n, url: 'https://example.test/guidelines' },
    } satisfies FormItem
    const scroll = {
      kind: 'action',
      key: 'jump',
      action: { kind: 'scroll-to-section', label: i18n, sectionId: 'bedroom' },
    } satisfies FormItem
    if (openUrl.kind === 'action') expect(openUrl.action.url).toContain('https://')
    if (scroll.kind === 'action') expect(scroll.action.sectionId).toBe('bedroom')
  })

  it('FormActionKind is exactly { open-url, scroll-to-section } (bounded, analyzable)', () => {
    const exhaustive = { 'open-url': 1, 'scroll-to-section': 1 } satisfies Record<FormActionKind, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['open-url', 'scroll-to-section'])
  })

  it('a group carries zone intents (layout + per-child placement) — the multi-column zone', () => {
    const zone = {
      kind: 'group',
      key: 'photoStrip',
      title: i18n,
      layout: { kind: 'flex', direction: 'row', wrap: 'wrap', gap: 2, collapseBelow: 'md', density: 'compact' },
      placement: { photoFront: { width: '1/3' }, photoBack: { width: '1/3', align: 'end' } },
      items: [
        { kind: 'field', key: 'photoFront' },
        { kind: 'field', key: 'photoBack' },
      ],
    } satisfies FormItem
    if (zone.kind === 'group') {
      expect(zone.layout?.collapseBelow).toBe('md')
      expect(zone.placement?.photoFront.width).toBe('1/3')
    }
  })

  it('SectionLayout carries the F-23 responsive intents (breakpoint/density/align — never pixels)', () => {
    const layout = {
      kind: 'grid',
      columns: 2,
      gap: 4,
      collapseBelow: 'md',
      density: 'compact',
      align: 'start',
    } satisfies SectionLayout
    expect(layout.collapseBelow).toBe('md')
    expect(layout.density).toBe('compact')
    expect(layout.align).toBe('start')
  })

  it('closed intent-token sets match the .NET validation vocabulary exactly', () => {
    const breakpoints = { sm: 1, md: 1, lg: 1 } satisfies Record<LayoutBreakpoint, number>
    const densities = { comfortable: 1, compact: 1 } satisfies Record<LayoutDensity, number>
    const aligns = { start: 1, center: 1, end: 1, stretch: 1 } satisfies Record<LayoutAlign, number>
    const widths = {
      auto: 1, '1/4': 1, '1/3': 1, '1/2': 1, '2/3': 1, '3/4': 1, full: 1,
    } satisfies Record<FieldWidth, number>
    expect(Object.keys(breakpoints)).toHaveLength(3)
    expect(Object.keys(densities)).toHaveLength(2)
    expect(Object.keys(aligns)).toHaveLength(4)
    expect(Object.keys(widths)).toHaveLength(7)
  })

  it('the render-side FormViewItem mirrors content/action blocks + group zone intents', () => {
    const items: FormViewItem[] = [
      { kind: 'content', key: 'note', content: [{ kind: 'paragraph', text: i18n }] },
      { kind: 'action', key: 'jump', action: { kind: 'scroll-to-section', label: i18n, sectionId: 's1' } },
      {
        kind: 'group',
        key: 'pair',
        layout: { kind: 'grid', columns: 2, collapseBelow: 'sm' },
        placement: { a: { colSpan: 2 } },
        items: [],
      },
    ]
    expect(items.map((i) => i.kind)).toEqual(['content', 'action', 'group'])
  })

  it('back-compat: intent-less layout/placement + a blockless item tree stay byte-identical', () => {
    // A pre-F-23 layout carries none of the new keys after a JSON round-trip.
    const legacyLayout = { kind: 'grid', columns: 2, gap: 4 } satisfies SectionLayout
    const wire = JSON.parse(JSON.stringify(legacyLayout)) as Record<string, unknown>
    expect('collapseBelow' in wire).toBe(false)
    expect('density' in wire).toBe(false)
    expect('align' in wire).toBe(false)

    const legacyPlacement = { colSpan: 2 } satisfies FieldPlacement
    const pWire = JSON.parse(JSON.stringify(legacyPlacement)) as Record<string, unknown>
    expect('width' in pWire).toBe(false)
    expect('align' in pWire).toBe(false)

    // A pre-F-23 group carries no layout/placement keys.
    const legacyGroup = {
      kind: 'group',
      key: 'billTo',
      items: [{ kind: 'field', key: 'billName' }],
    } satisfies FormItem
    const gWire = JSON.parse(JSON.stringify(legacyGroup)) as Record<string, unknown>
    expect('layout' in gWire).toBe(false)
    expect('placement' in gWire).toBe(false)
  })
})

describe('forms — F-17 per-field config (FieldOverlay.config, additive)', () => {
  it('round-trips a FieldOverlay carrying a currency config through JSON', () => {
    const overlay = {
      label: i18n,
      controlHint: 'currency',
      config: { currencyCode: 'AED' },
    } satisfies FieldOverlay
    const wire = JSON.parse(JSON.stringify(overlay)) as FieldOverlay
    expect(wire.config?.currencyCode).toBe('AED')
  })

  it('round-trips a file config (accept + multiple) through JSON', () => {
    const config = { accept: '.pdf,image/*', multiple: false } satisfies FieldConfig
    const overlay = { label: i18n, controlHint: 'file', config } satisfies FieldOverlay
    const wire = JSON.parse(JSON.stringify(overlay)) as FieldOverlay
    expect(wire.config).toEqual({ accept: '.pdf,image/*', multiple: false })
  })

  it('back-compat: an overlay with no config is still a valid FieldOverlay', () => {
    const legacy = { label: i18n } satisfies FieldOverlay
    expect(legacy.config).toBeUndefined()
  })
})

describe('forms — D3 submission payload (ADR 0140 amendment 2026-07-01)', () => {
  it('binding header carries the exact camelCase keys the .NET header writer emits', () => {
    const binding = {
      schemaRef: 'sha256:abc123',
      definitionId: 'tenant:acme/lease',
      definitionVersion: '2.3.1',
      engineVersion: HARBORLINE_JSONLOGIC_V1,
      localeChain: ['ar-AE', 'en'],
      submittedAt: '2026-07-01T12:00:00+00:00',
    } satisfies SubmissionBindingHeader

    expect(binding.engineVersion).toBe('shipyard-jsonlogic/v1')
    expect(binding.localeChain[0]).toBe('ar-AE')
  })

  it('an ordinary submission audit payload omits the snapshot (minimization)', () => {
    const ordinary = {
      op: 'form-instance-mint',
      form: 'ordinary',
      version: '1.0.0',
      encryptedFields: [],
      binding: {
        schemaRef: 'sha256:zzz',
        definitionId: 'ordinary',
        definitionVersion: '1.0.0',
        engineVersion: HARBORLINE_JSONLOGIC_V1,
        localeChain: ['en'],
        submittedAt: '2026-07-01T12:00:00+00:00',
      },
    } satisfies SubmissionMintAuditPayload

    expect(ordinary.snapshot).toBeUndefined()
  })

  it('a compliance-grade submission carries a full-projection snapshot', () => {
    const snapshot = {
      mode: 'full-projection',
      capturedAt: '2026-07-01T12:00:00+00:00',
      projection: { displayName: 'Alice' },
    } satisfies SubmissionSnapshot

    expect(snapshot.mode).toBe('full-projection')
  })

  it('a signed submission carries only a hash-of-DTBS artifact (no cleartext projection)', () => {
    const snapshot = {
      mode: 'signed-dtbs-hash',
      capturedAt: '2026-07-01T12:00:00+00:00',
      signed: {
        hashAlgorithm: 'SHA-256',
        dtbsHash: 'aGFzaA==',
        signatureAlgorithm: 'Ed25519',
        signature: 'c2ln',
        publicKeyRef: 'key:test',
      },
    } satisfies SubmissionSnapshot

    expect(snapshot.projection).toBeUndefined()
    expect(snapshot.signed?.hashAlgorithm).toBe('SHA-256')
  })

  it('SnapshotCaptureMode is exactly {none, full-projection, signed-dtbs-hash}', () => {
    const all: SnapshotCaptureMode[] = ['none', 'full-projection', 'signed-dtbs-hash']
    expect(all).toHaveLength(3)
  })
})
