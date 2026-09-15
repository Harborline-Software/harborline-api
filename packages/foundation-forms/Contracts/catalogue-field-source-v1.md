# Catalogue field source v1 — producer contract

Status: normative contract frozen for T-427; runtime implementation and acceptance evidence
are pending. This document defines future producer and consumer obligations. Its presence does
not advertise runtime support, add a seed, change an endpoint, or register a capability.
The current platform pack's capability requirements and the host capability inventory do not
change with this document. MUST, MUST NOT and MAY express requirements of this contract.

## Fixed identities and version domains

| Item | Exact value |
| --- | --- |
| Runtime capability id | `forms.catalogue-field-source` |
| Coordinate schema version | JSON integer `1` |
| Source-mapping schema version | JSON integer `1` |
| First seeded detail definition | `platform.detail.form@1.0.0` |
| Seeded detail content kind | `FormDefinition` |
| Supported source kind in v1 | `FormDefinition` |
| Permission operation and decider | `catalogue:read`, existing `AuthorizationGate` |

These schema integers are independent of a definition version, pack version, M6 manifest
version and support-contract version. No range, wildcard, alias or default substitutes for
an explicit version. Host support means an implemented, registered handler for the exact
capability/coordinate-schema/mapping-schema triple. A manifest declaration or this document
alone cannot establish support. This contract does not prescribe a capability-discovery endpoint.

## Producer declaration and exact member locations

The following is a declaration fragment, not an installable pack or a shipped seed. After
host support exists, an opted-in pack MUST include the capability exactly once in its
`capabilityRequirements` string array. Other unrelated requirements remain permitted.
In an export input document this member is `/capabilityRequirements`; in the signed pack
manifest it is `/envelope/payload/manifest/capabilityRequirements`.

For the selected `FormDefinition` content item, `/contents/i/key` is `platform.detail.form`
and `/contents/i/version` is `1.0.0` for the first seed. Its typed declaration lives at
`/contents/i/content/catalogueFieldSource`, alongside the existing `overlay` and
`fieldsMeta` members. Here `i` means the item's array index. The signed content leaf MUST
carry the same member at `/catalogueFieldSource` in its decoded content body; its bytes
remain covered by the pack's normal content-address and signature verification.

```json
{
  "capabilityId": "forms.catalogue-field-source",
  "coordinateSchemaVersion": 1,
  "sourceMappingSchemaVersion": 1,
  "sourceKind": "FormDefinition",
  "fields": [
    { "fieldId": "formId", "source": "catalogue.entry.formId" },
    { "fieldId": "title", "source": "catalogue.entry.title" },
    { "fieldId": "version", "source": "catalogue.entry.version" },
    { "fieldId": "cascadeLayer", "source": "catalogue.entry.cascadeLayer" }
  ]
}
```

This object has exactly those five required members. `capabilityId` and `sourceKind` are
strings. Both schema versions MUST be integer tokens (no quoted numbers, decimals or
exponents). `fields` is an array of exactly four closed objects, each with exactly the two
required string members `fieldId` and `source`. The pairs and array order above are fixed
in v1, including for a new definition version opting into this v1 profile. A subset,
duplicate, reordered list, extra field or different pairing refuses admission.

Each field MUST also exist in `/content/overlay/fields` and `/content/fieldsMeta` on the
export item. The ordered concatenation of `/content/overlay/sections/*/fields` MUST equal
the four `fieldId` values once each. The typed detail is read-only: no submit, edit or
mutation action, computed source, user-input replacement or rule that supplies values for
these mapped fields is permitted. Existing general form declarations retain their own
schema; only the typed declaration and its required mapping/render correspondence are
closed here. No member added to an unrelated form silently opts that form in.

All contract objects described below are closed unless explicitly called a dictionary.
Readers MUST reject duplicate JSON members, including escaped spellings that decode to
the same name; unknown members; missing required members; nulls where not permitted;
and wrong JSON types. Names and enum values use exact case-sensitive comparison. Readers
MUST NOT trim, case-fold, coerce, reflect over properties, evaluate paths, or ignore malformed
members. Extra object members are malformed shape; a well-shaped unrecognized enum/string
identifier is unknown vocabulary.

## Closed source vocabulary and value shapes

These four strings name typed adapter operations. They are not JSON paths and grant no
permission to fetch an entire `CatalogueEntry.Body`. The producer MUST bind them to the
same immutable source revision and its verified provenance.

| Order | `fieldId` | Closed `source` | Source authority and returned JSON value |
| --- | --- | --- | --- |
| 1 | `formId` | `catalogue.entry.formId` | Form envelope identity (`CatalogueEntry.Id`); nonempty string. |
| 2 | `title` | `catalogue.entry.title` | Form overlay title (`CatalogueEntry.Title` projection); `null` or the localized-text object below. |
| 3 | `version` | `catalogue.entry.version` | Exact immutable form envelope version (`CatalogueEntry.Version`); canonical version string. |
| 4 | `cascadeLayer` | `catalogue.entry.cascadeLayer` | Form envelope cascade layer; one of `Base`, `Pack`, `Tenant`, `Instance`. |

The localized-text object is exactly `{ "defaultLocale": "en", "values": { "en": "Title" } }`
in shape: `defaultLocale` is a nonempty locale-tag string; `values` is a dictionary of
locale tags to strings and MUST contain `defaultLocale`. Other valid locale entries are
permitted. Both lanes receive the same localized value before their ordinary locale
resolution; a lane MUST NOT invent a title from the id. An allowed null title is distinct
from a denied field, which has no declaration or value in the response.

The current catalogue envelope has no top-level `formId` or `cascadeLayer` member. The
first maps the envelope identity; the last requires a typed form-envelope adapter.
Implementations MUST NOT treat these vocabulary strings as evidence that those transport
members already exist or materialize an unrestricted body to obtain them.

## Typed coordinate and immutable source binding

The typed field-read request is a JSON object with exactly `coordinate` and `sourceBinding`.
This defines its payload shape, not a new HTTP route. For example:

```json
{
  "coordinate": {
    "schemaVersion": 1,
    "kind": "FormDefinition",
    "id": "tenant:acme/property-listing",
    "version": "2.3.4",
    "field": "title"
  },
  "sourceBinding": {
    "definitionHash": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "provenance": {
      "kind": "pack",
      "packKey": "acme.properties",
      "packVersion": "2.0.0"
    }
  }
}
```

`coordinate` is closed with exactly the five members shown. `schemaVersion` is integer
`1`; the others are strings. `kind` is `FormDefinition`. `id` is the exact nonempty,
well-formed Unicode source identity, not the detail form's identity. `version` names an
explicit immutable source revision; it is not the detail definition version `1.0.0`.
`field` is exactly one of the four admitted `fieldId` values. Canonical form versions have
three decimal components in `0..2147483647`, no sign or leading zero except `0`, separated
by dots. Prerelease, build metadata, omitted/current/latest versions and version ranges
are outside this profile. A draft or otherwise mutable revision cannot be a typed source.

`sourceBinding` has exactly `definitionHash` and `provenance`. `definitionHash` is
`sha256:` followed by 64 lowercase hexadecimal digits, naming the canonical immutable
source-definition bytes verified by the producer. The illustrative digest above is not
an accepted fixture or a known definition. It MUST be checked against authentic source
metadata; client-supplied metadata never becomes authority by being well-shaped.

`provenance` is one of these closed objects: `{ "kind": "tenant" }`, or an object with
exactly `kind`, `packKey`, `packVersion`, where `kind` is `pack` or `platform`, `packKey`
is a nonempty string and `packVersion` is an exact immutable pack version using the
pack manifest's existing version rules. Null or partial pack coordinates are invalid.
Provenance MUST equal the persisted source authority, including its kind and both pack
coordinates where present. It is not inferred from cascade layer or accepted from a title.
The detail definition's own verified pack/hash/version binding is retained separately;
it MUST NOT replace this selected source binding. Ordinary tenant isolation still applies.

### Canonical authorization target

For a valid coordinate `c`, the sole target spelling is:

```text
/records/{E(c.id)}/catalogue-fields/1/{E(c.kind)}/{E(c.version)}/{E(c.field)}
```

`E` encodes the exact Unicode value as UTF-8, leaves only ASCII letters, digits, `-`, `.`,
`_`, `~` literal, and percent-encodes every other byte using uppercase hex. It performs
no Unicode normalization. Reject invalid Unicode, empty components, control characters,
and components equal to `.` or `..`. A literal `%` becomes `%25`; `/` becomes `%2F`.
Do not encode an already encoded component or decode twice. No query, fragment, trailing
slash, extra segment, percent-encoded unreserved character or lowercase escape is canonical.
Parsing MUST round-trip through `E` and compare the entire target exactly. Both grant
creation and checks use this encoding, including the parent `/records/{E(c.id)}`; path
processing MUST NOT turn an encoded slash into a hierarchy separator.

For the request above the target is exactly
`/records/tenant%3Aacme%2Fproperty-listing/catalogue-fields/1/FormDefinition/2.3.4/title`.
This is an authorization target, not a claim that an HTTP route with that spelling exists.
The `1` segment is the coordinate schema version. Kind, id, version and field all contribute
to the target, so a grant cannot alias a different kind, version or sibling field.

Use ordinary `catalogue:read` and `AuthorizationGate` for every mapped field. Existing root
and record-parent grants retain full read under the existing gate rules. An exact child
grant admits only its field at its version, not a whole-record read. No per-field
`AuthorizationCapabilityBinding` is introduced. A root or parent grant never waives
mapping, coordinate, version, source hash or provenance checks. Do not retry a refused
typed request at parent scope.

## Admission, read order and named refusals

Admission MUST preserve the existing unsupported-pack-content refusal. Before changing
admitted or installed state, verify pack authenticity, require the explicit declarations,
validate closed shapes and known vocabulary, check the exact host support triple, and
validate mapping/render correspondence. Failure is atomic: no partially admitted mapping,
installed mutation, field closure invocation or protected payload read.

At request time validate the coordinate and its binding against the immutable admitted
detail mapping and authentic source identity metadata before constructing a field-read
closure. Bind that closure to the exact tenant, kind/id/version/field, definition hash
and provenance, then call the gate for its canonical target. Only an allowed closure may
read that one protected payload. Identity/provenance validation MUST NOT evaluate a field
getter, read an unrestricted body, or return protected field data as validation metadata.

A newer published version does not redirect a still-valid immutable version. If the
requested revision is missing, stale, withdrawn from typed-read eligibility or changed,
refuse it; never resolve latest/current. A source handle changing after authorization
MUST fail before protected read. A returned internal read-result envelope MUST carry and
match the bound tuple, hash and provenance before any value is serialized. Mismatch
discards the result and fails closed. Such internal identity metadata is not a way to
serialize a denied `formId` or `version` under a different response key.

The following stable logical refusal names are required. They name contract outcomes;
this document does not invent HTTP status codes or replace existing gate/audit DTOs.
Select the first applicable validation stage; within that stage use the listed distinctions.

| Stage | Refusal name | Meaning |
| --- | --- | --- |
| Existing admission | Existing unsupported-content refusal | Pack content kind is not admitted by the host. |
| Typed opt-in | `catalogue-field-source.legacy-mapping-absent` | Typed request addresses a legacy definition with no declaration. |
| Declarations | `catalogue-field-source.missing-support-declaration` | Capability or either required schema version is absent in an opted-in definition, or the pack requirement is absent. |
| Mapping shape | `catalogue-field-source.malformed-source-mapping` | Wrong types, duplicates, unknown members, null declaration, missing mapping members or invalid ordering/pairing/render correspondence. |
| Mapping vocabulary | `catalogue-field-source.unknown-source-mapping` | A well-shaped source string, field id or source-kind name is unrecognized. |
| Host support | `catalogue-field-source.unsupported-capability-or-schema-version` | Explicit capability or schema version is unknown or not supported by the host; no negotiation/downgrade. |
| Mapping support | `catalogue-field-source.unsupported-source-mapping` | Known source kind or mapping vocabulary has no admitted adapter for this profile. |
| Coordinate shape | `catalogue-field-source.malformed-coordinate` | Missing explicit version, invalid type/shape/version/encoding or noncanonical target. |
| Coordinate vocabulary | `catalogue-field-source.unknown-coordinate` | Kind or field name is unrecognized. |
| Coordinate support | `catalogue-field-source.unsupported-coordinate` | Recognized kind/field is outside the admitted mapping or coordinate schema is unsupported. |
| Source binding shape | `catalogue-field-source.malformed-source-binding` | Binding hash/provenance is missing, malformed or contains unknown members. |
| Source resolution | `catalogue-field-source.source-version-unavailable` | Exact immutable revision is absent, stale or ineligible; no latest fallback. |
| Source identity | `catalogue-field-source.source-binding-mismatch` | Authentic pre-read tuple, hash or provenance differs from the request/mapping binding. |
| Authorization | Existing gate denial code | Gate denies this exact field target; preserve its audit correlation. |
| Bound read | `catalogue-field-source.source-changed-after-authorization` | Pinned source identity/hash/provenance changed before the payload read. |
| Result validation | `catalogue-field-source.payload-binding-mismatch` | Returned identity/hash/provenance or value shape differs from the bound source contract. |

Unknown and unsupported are distinct: an unrecognized kind spelling is unknown; a recognized
catalogue kind such as `ViewDefinition` has no v1 form-source adapter and is unsupported.
An absent required version is missing declaration at admission, or malformed coordinate
at request time. A present noninteger is malformed; a well-formed unsupported integer
is unsupported. No refusal permits a raw body, compiled inspector or reflective fallback.

Build rendered declarations and values afresh per request in admitted order. A gate-denied
field MUST be omitted from both, from the serialized response and from each lane's DOM.
Remove references to it in the per-request section projection as well. Do not mutate the
shared admitted definition. Other permitted fields can render; an invalid mapping or
binding refuses the typed request rather than exposing a partial unvalidated result.
General `FormView` metadata-null and `SchemaForm` hidden behavior remain unchanged.

## Legacy definitions and migration

A definition with no `catalogueFieldSource` remains legacy and retains only its existing
general-form behavior. Absence is not an empty v1 mapping; an explicit null is malformed.
A pack-level requirement alone does not synthesize a mapping for its other forms.
Typed detail against legacy definitions returns `catalogue-field-source.legacy-mapping-absent`
before a field closure or payload read. No inferred mapping or implicit migration is allowed.

Opt-in publishes a new immutable definition version and a newly verified pack version
carrying the required capability and declaration, after support is implemented. Existing
signed bytes and previous definition versions MUST NOT be rewritten or reinterpreted.
Changing the mapping vocabulary, pairing, order or encoding requires a separately versioned
contract and explicit host support; changing source data requires its own immutable revision.
The first reserved seed is `platform.detail.form@1.0.0`; it is not added by this document.

## Implementation acceptance obligations

Before acceptance, pin actual fixture identity/version/hash/location, released producer
and both consumer commits, source/detail/pack versions, provenance, expected field order,
values and refusal/audit outcomes. Documentation examples are not fixtures. Both React
and Blazor MUST consume the same seeded read-only form through their shared form runtime.

Exercise root and parent full read, exact-child-only read, sibling/other-version/no-grant
denials, omitted/stale versions, source change after authorization and returned payload
identity/provenance mismatch. Exercise every declaration, mapping and coordinate refusal
above, assert unchanged admitted/installed state, and prove legacy general behavior beside
new-version migration. Preserve unknown-view inertness and unsupported-content refusal.

A denied non-PII sentinel (`piiSensitivity: None`) MUST yield zero unauthorized payload
reads/returns, zero sentinel occurrences in response and DOM, and zero invalid-mapping
field closure invocations. Keep an allowed field read/render positive control beside it.
Record zero parent-scope retries and raw-body fallbacks. Verify both lanes' field order,
values, provenance and authorization consequences. Contract/document checks alone do not
satisfy these runtime obligations or close T-427.
