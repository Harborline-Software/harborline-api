# Terminology content and resolver contract v1

`TerminologyOverride` content uses `schemaVersion: 1`. Its `stableId` and `version`
must exactly match the signed content item key and version. Labels never participate
in identity, routing, or ownership. `tenantId` is optional for portable packages; if
present it must exactly equal the installing tenant. Runtime scope always comes from
the authorized projection tenant.

Required fields are `schemaVersion`, `stableId`, `version`, `defaultLocale`,
`sourceRevision`, `packageTranslations`, and `tenantOverrides`. The two translation
maps use canonical locale names and contain only `text` and `sourceRevision` strings.
The package default locale must have a current package translation. Signed source
content has an empty `tenantOverrides` map; only the tenant patch supplies that layer.
Unknown fields, duplicate properties/locales, missing/empty required values, mismatched coordinates,
cross-tenant claims and unsupported schema versions refuse admission.

Tenant patches use the existing durable pack override merge-patch representation,
changing only `tenantOverrides`. Admission validates the signed source and composed candidate
and refuses changes to package translations, source revision, identity, scope or
default locale. A removed locale override restores the package value. No new store
or Settings endpoint is introduced by this runtime slice. The current `Narrow` API
deliberately refuses wording changes; a dedicated authorized terminology editor
write seam remains follow-up work. Runtime tests populate existing override rows
directly to exercise composition, and do not claim editor admission evidence.

Resolution starts with package translations, overlays the tenant map, then selects
the user's exact locale, successive parent locales, and finally the package default.
The response names the selected locale and layer, whether fallback occurred, and
whether its source revision differs from the current package source revision.
Stale translations remain visible with `isStale: true`; they never silently acquire
a current revision. Missing identities resolve to absence. Invalid user locales
refuse before resolution. The request fixture specifies the consumer invocation;
it does not advertise a Settings HTTP capability.

The host catalogue and resolver read the same admitted active typed projection.
The existing projection dispatcher supplies seed-plus-override content after active
pack ownership decisions. Deactivated, superseded, incompatible and non-owner rows
are removed, and failed re-admission leaves the affected pack absent. Catalogue
provenance names the actual supplying pack and version; the body retains both
translation layers and revision metadata. An absent consumer remains unavailable.

Fixture: `_shared/conformance/terminology/v1.json`. This contract supplies API runtime
evidence for T-233 only; tenant Settings wiring, shell parity and T-118 clean-node
acceptance remain separate work.
