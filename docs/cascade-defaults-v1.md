# CascadeDefaults v1

T-454 / ADR 0087. The supported content body is:

```json
{
  "schemaVersion": 1,
  "title": "Governance defaults",
  "defaults": [
    {
      "classification": [],
      "personalData": false,
      "masking": { "revealLast": 0 },
      "retention": { "regime": "GDPR", "floorClass": "Identity", "minimumRetentionDays": 30 },
      "conflictPolicy": "ask",
      "trackChanges": true
    },
    { "recordType": "contact", "masking": { "revealLast": 2 } },
    { "recordType": "contact", "field": "title", "masking": { "revealLast": 4 }, "trackChanges": false }
  ]
}
```

The carrying pack supplies package identity. `recordType` is the projected form definition ID;
`field` is its overlay field key and requires `recordType`. A declaration with neither targets the
whole package. Each coordinate occurs once across a package's Defaults items. An absent or null axis
inherits; each declared axis independently overrides the coarser declaration.

Classification uses the existing `{system, code, display?}` Tag contract. Personal data adds the
existing PII policy. Masking uses a nonnegative reveal count. Retention uses the existing retention
requirement and recognized audit floor class. Supported regimes are `HIPAA`, `PCI_DSS_v4`, `SOC2`,
`GDPR`, and `EU_AI_Act`. Store and definition-envelope verdicts take the longest of the tenant
minimum, declared minimum days, and the existing regime preset's class-specific floor. Their maximum
hold never precedes that minimum. `ask` is the only admitted conflict policy in v1; it is currently
projected metadata, not a selector consumed by the synchronization conflict resolver. Change tracking
adds the existing store-audit effect. These defaults supplement existing form governance and never
replace the authorization gate.

The installer checks both the signed seed and the composed tenant override before mutation. Unknown
properties, duplicate JSON members, duplicate coordinates, malformed values, and unread schema
versions fail closed with `pack.defaults.malformed` or `pack.defaults.unsupported_version`, including
a content-relative JSON pointer. An unwired host retains the unsupported-content refusal.
Composed tenant defaults must preserve every effective publisher restriction at package, type and
field scopes. `pack.defaults.relax_forbidden` rejects a deletion that would weaken inheritance,
including emptying the defaults array. This check runs before a narrowing is stored and again at
installation and projection.

The authorized seed projector supplies an in-memory tenant projection after applying the ordinary
tenant override and ownership rules. Activation, supersession, deactivation, incompatible packs and
unresolved ownership retract stale rows. The resolver and Defaults catalogue read this projection;
they do not inspect installed seed bytes. Catalogue availability describes whether this projection
is wired, so a wired empty projection is available-empty. Replaying installation state reconstructs
the projection; it is not a second persistence authority. Whole-pack catalogue-field and grant-shape
refusals prevent Defaults publication.

The existing tenant override API accepts structural narrowing. For example, removing the field
declaration above makes `contact.title` inherit masking with two visible characters and change
tracking enabled. Arbitrary scalar policy editing belongs to the later T-455 Settings work.

Focused evidence lives in `CascadeDefaultsTests`: signed v1 admission, mutation-free refusal,
six-axis resolution, tenant provenance/isolation, authorized override, upgrade, reactivation,
deactivation, unresolved/resolved ownership, catalogue availability, and an actual FormEngine
save/read with unclassified masking and auditing. Retention tests exercise the real store and
definition-envelope verdicts. The existing role restriction stays enforced. This slice does not
claim conflict-policy consumption or new record-erasure behavior.
