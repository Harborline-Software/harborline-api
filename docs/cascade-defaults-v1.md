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

## Released platform seed

Platform pack `harborline.platform@1.2.0` carries `platform.defaults.pack-author@1.0.0`:

```json
{"schemaVersion":1,"title":"Pack author change tracking","defaults":[{"recordType":"platform.pack.author","trackChanges":true}]}
```

This applies the existing store-audit effect to the pack-author form only. It declares no other
axes and adds no navigation, views, forms, or compiled UI. The 13 Workshop surfaces and 39 view
definitions are unchanged; the platform seed contains 62 items.

`PackContentCanonicalizer` produces content CID
`bafkreiczgrb2t4g3jtgc5cdxtjn4247wyqlw5h7r47bsggwp5bkpbgqjia` for this body. The regression pins
that CID, exports the complete embedded platform source through `PackExporter`, verifies and installs
the signed bytes, projects the Defaults row, checks tenant/pack/content provenance, and executes the
store-audit effect for the projected pack-author field. Preload tests check the available nonempty
Defaults catalogue and unchanged surface inventory.

The committed file is an export source, not a pre-signed release artifact. Ordinary platform preload
canonicalizes and signs it with the node's own-roster signer; signature bytes include the node
identity, issuance time and nonce and therefore have no single committed file digest. No release
signature or content-addressed payload is hand-edited.
The LF-normalized export-source SHA-256 is
`218f1d806e8413daa06a37ad20dc9e83ac9b15b573491bd43e075ae20338e146`.

### Authorization ownership

The two platform bindings now use the existing sealed `sys.platform-roles/administrator` key
(binding content version `1.1.0`). The old `platform/...` keys never resolved in the sealed vocabulary.
The redundant `platform.binding.audit-read` item is removed: T-217/L628 requires exactly one
effective definition naming Auditor, and `AuthorizationDefinitionAdmission` reserves it for the
founding `harborline.access-grant` `audit:read` definition. `AuthorizationSeedHostedService` installs
that definition before platform preload. This is an explicit bootstrap prerequisite, not a claim
that a released access-grant pack exists or an invented pack-manifest dependency.

The fixture test runs that founding seed, admits the complete platform pack through the real
authorization writer, and proves both platform Administrator bindings plus the sole access-grant
Auditor definition. T-398 joins admitted bindings across publishers by operation; it requires no
duplicate Auditor offer from the carrying platform pack. The platform still supplies its real
roles and Administrator bindings as M4/M6 require. Neither the Auditor's authority nor the
admission invariant is broadened, and no view becomes administrator-only by this correction.

The canonicalizer-generated binding CIDs are pinned in `CascadeDefaultsTests`:
`platform.binding.catalogue-read` is `bafkreift42gowbpcxbfygt36n7tah63ujbb7ekxvzr2deasxigqgp3cb3q`;
`platform.binding.records-read` is `bafkreib6tp3widcpawwq43y3kvzqk2hqgxgtabsqxnskemfstgkjlww3xy`.
