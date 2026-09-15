import { describe, expect, it } from 'vitest'
import type { CatalogueFieldReadRequest, CatalogueFieldSource, FormDefinition } from '../forms.js'

const declaration = {
  capabilityId: 'forms.catalogue-field-source',
  coordinateSchemaVersion: 1,
  sourceMappingSchemaVersion: 1,
  sourceKind: 'FormDefinition',
  fields: [
    { fieldId: 'formId', source: 'catalogue.entry.formId' },
    { fieldId: 'title', source: 'catalogue.entry.title' },
    { fieldId: 'version', source: 'catalogue.entry.version' },
    { fieldId: 'cascadeLayer', source: 'catalogue.entry.cascadeLayer' },
  ],
} satisfies CatalogueFieldSource

describe('catalogue field source v1 producer contract', () => {
  it('preserves the integer versions and ordered typed mapping', () => {
    const form = { catalogueFieldSource: declaration } satisfies Pick<FormDefinition, 'catalogueFieldSource'>
    expect(JSON.parse(JSON.stringify(form)).catalogueFieldSource).toEqual(declaration)
    expect(declaration.fields.map(field => field.fieldId)).toEqual(['formId', 'title', 'version', 'cascadeLayer'])
  })

  it('keeps the immutable source version and binding separate from the detail definition', () => {
    const request = {
      coordinate: { schemaVersion: 1, kind: 'FormDefinition', id: 'tenant:acme/form', version: '2.3.4', field: 'title' },
      sourceBinding: { definitionHash: `sha256:${'a'.repeat(64)}`, provenance: { kind: 'pack', packKey: 'acme.forms', packVersion: '2.0.0' } },
    } satisfies CatalogueFieldReadRequest
    expect(JSON.parse(JSON.stringify(request))).toEqual(request)
  })

  it('keeps legacy definitions free of an inferred declaration', () => {
    const legacy = {} satisfies Pick<FormDefinition, 'catalogueFieldSource'>
    expect(JSON.stringify(legacy)).toBe('{}')
  })
})
