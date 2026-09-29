# Harborline.Api.Kernel.SchemaRegistry

Harborline kernel §3.4 Schema Registry. Since T-303, registration and validation run in the platform `Harborline.Kernel.SchemaValidation` library (pinned NuGet from the feed); this package keeps the api-only responsibilities: the `schema:{cid}` ids stored definitions carry, parents, tags, blob threshold, pack-projection staging, and the lens, upcaster, epoch and compaction surface. T-542 retires it once those have owners.

**Validation path shipped.** Migration path (jsonata-style transforms) is a follow-up.

## What this ships

### Contracts

- **`ISchemaRegistry`** — read/write registry: `RegisterAsync(name, version, schemaJson)` + `LookupAsync(name, version)` + `ValidateAsync(name, version, payload)`.
- **`SchemaIdentity`** — content-addressed identifier (`Vendor.Domain.Name@Version` + content hash).
- **`SchemaValidationError`** — typed error with JSON-pointer pointing at the offending field.

### Reference impl

- **`InMemorySchemaRegistry`** — Dictionary-backed reference; backs schemas via `IBlobStore` for content-addressed storage.
- **`JsonSchemaValidator`** — `JsonSchema.Net` adapter for draft 2020-12 validation.

### Validation flow

```
payload + (schema-name, schema-version)
  → registry.LookupAsync → JsonSchema instance
    → validator.Validate(payload) → ValidationResult
      → Success | List<SchemaValidationError>
```

## Migration (deferred)

The follow-up scope adds a `jsonata`-style migration path: schemas can declare "from version N-1 to version N, apply transform T". The registry composes transforms across versions so callers can up-convert legacy payloads at read time without explicit migration code.

## DI

```csharp
services.AddHarborlineKernelSchemaRegistry();
```

## ADR map

- Harborline kernel §3.4 (Schema Registry)
- ADR 0055 (dynamic-forms substrate; schema-registry consumer)

## See also

- `Harborline.Api.Foundation.Taxonomy` — adjacent versioned-reference-data substrate (the "registry" pattern shows up in both)
