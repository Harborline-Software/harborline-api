# Harborline.Api.Analyzers.ProviderNeutrality

Roslyn analyzer that enforces [ADR 0013](../../../docs/adrs/0013-foundation-integrations.md)
provider-neutrality at build time.

## Rules

### HARBORLINE_API_PROVNEUT_001 (Error)

Vendor SDK namespace referenced from a non-providers package.

Code in `apps/*`, `packages/blocks-*`, and `packages/foundation-*` must not reference
the vendor SDK namespaces and provider-boundary types declared in
`BannedVendorNamespaces.txt`. Adding a provider requires a declaration edit, not an
analyzer source edit. Only `packages/providers-*` packages may take vendor-SDK dependencies. The contract
seam — `Harborline.Api.Foundation.Integrations` — is excluded from the rule because
it defines the vendor-neutral interfaces that providers implement.

**Why a mechanical gate?** ADR 0013 declares vendor-neutrality load-bearing.
Without a build-time check the policy is socially enforced ("reviewers reject
violations"); the moment a developer slips, vendor references multiply across
N callers. This analyzer fails the build instead.

## Auto-attach

`Directory.Build.props` auto-wires this analyzer onto every project under `apps/*`,
`packages/blocks-*/`, and `packages/foundation-*/` (excluding the
`Harborline.Api.Foundation.Integrations` contract package + test projects).
Mirrors the `loc-comments` / `loc-unused` analyzer auto-wiring pattern.

The diagnostic ID is frozen as `HARBORLINE_API_PROVNEUT_001`. The canary runner verifies
that exact ID and audits every in-tree suppression against it. Ticket 026's narrow
persistence suppressions document the retained atomic transaction and ADR 0097
password-hasher seams at their use sites.
