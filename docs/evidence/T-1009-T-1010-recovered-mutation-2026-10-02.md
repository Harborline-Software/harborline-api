# T-1009 / T-1010 mutation evidence for API #341

This records the recovered Stryker.NET reports for the test-only change at
`cda980055b5a437952c6ad9a1dbae3ce3b86122c`. It supplies raw statuses and named
killing tests; it does not certify `kernel-core-ck-4` or close either ticket.

## Report identities and availability

The exact bytes are published on `archive/pr341-tenancy-founder-mutation-20261002`
at commit `2eaf29b79246ed46088e1d173603dbb2e974656c`, under
`docs/evidence/mutation/`, following the owner's explicit approval of this public
archive disclosure. A fresh remote fetch verified that all three report bytes
and the manifest match the SHA-256 values and byte counts prepared locally.

| Raw report | SHA-256 |
| --- | --- |
| [t1010-founder-attach.json](https://github.com/Harborline-Software/harborline-api/blob/2eaf29b79246ed46088e1d173603dbb2e974656c/docs/evidence/mutation/t1010-founder-attach.json) | `093a7e844006088b4f456121df13e393e14c934d3c8440db5bb9ecdc1f62a1ac` |
| [t1009-tenancy-inputs.json](https://github.com/Harborline-Software/harborline-api/blob/2eaf29b79246ed46088e1d173603dbb2e974656c/docs/evidence/mutation/t1009-tenancy-inputs.json) | `63a48cdc3f17d5e8462ced7d0471abf7265a15fbf0e6e4ea56f637e55878275a` |
| [t1009-locator-attach-web-authorities.json](https://github.com/Harborline-Software/harborline-api/blob/2eaf29b79246ed46088e1d173603dbb2e974656c/docs/evidence/mutation/t1009-locator-attach-web-authorities.json) | `68135a3a6b2ed2f32d6d837f444d97d7aa5d86ea765bdac3b3e7241b48ad78bc` |

The store report contains all five changed test files exactly as at the source
head above, after normalizing line endings. The combined locator / attach / web
authority report matches the four relevant changed test files exactly; its store
test snapshot predates the 64-space digest correction. Use the later store
report for that guard. Mutated production source in these reports matches the
source head. The separate founder report predates the later T-1009 test additions
and is retained as historical evidence, rather than an exact final-tree run.

## Match method

The original ticket IDs come from the archived baseline at
`5da90b94fdc78cf5ace18cb5009154f04e9790e9` on
`archive/ck4-tenant-slice-2026-09-30`. Match by file, start/end line and column,
mutator and replacement. IDs change between runs. The table below uses the later
combined report for founder, locator and web-authority entries, and the final
store report for store entries. Killing-test names are resolved from each raw
report's `testFiles` using its `killedBy` IDs.

An independent read-only review reproduced all 38 matches, resolved each named
killing test and verified the three raw-report hashes. The recovered final-store
command selected `**/Identity/TenantMembershipAuthorityStore.cs` with filter
`FullyQualifiedName~TenantMembershipAuthorityStoreTests`, through
`node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped`.
Logs identify Stryker.NET 5.0.0 and pinned Windows SDK
`11.0.100-rc.1.26425.128`. The historical scoped config disabled since and set
break 0; it supplies no whole-slice floor proof. The exact combined-run filter
and execution-time Platform dependency pin were not independently recovered.

| Baseline ID | File:line | Mutator | Recovered mutant ID | Raw status | Named killing test |
| --- | --- | --- | --- | --- | --- |
| 8915 | FounderTenantMembershipAttachService.cs:182 | Block removal mutation | 9275 | Killed | `Founder_attach_without_the_founder_account_is_skipped_and_writes_no_tenant_authority` |
| 8918 | FounderTenantMembershipAttachService.cs:189 | Block removal mutation | 9278 | Killed | `Founder_attach_without_the_installation_identity_is_skipped_and_writes_no_tenant_authority` |
| 8930 | FounderTenantMembershipAttachService.cs:242 | Block removal mutation | 9290 | Killed | `Founder_attach_whose_party_binding_never_resolves_is_unavailable_and_writes_no_tenant_authority` |
| 8933 | FounderTenantMembershipAttachService.cs:259 | Conditional (false) mutation | 9293 | Survived | none |
| 8939 | FounderTenantMembershipAttachService.cs:267 | Logical mutation | 9299 | Killed | `Founder_attach_with_no_issuable_claim_and_nothing_redeemed_is_unavailable_and_writes_no_tenant_authority` |
| 8943 | FounderTenantMembershipAttachService.cs:268 | Block removal mutation | 9303 | Killed | `Founder_attach_with_no_issuable_claim_and_nothing_redeemed_is_unavailable_and_writes_no_tenant_authority` |
| 8952 | FounderTenantMembershipAttachService.cs:326 | Statement mutation | 9312 | Killed | `Roster_party_provider_refuses_a_missing_party_id` |
| 10772 | InstallationTenantCandidateLocator.cs:171 | Statement mutation | 11132 | Killed | `Tenant_Candidate_Locator_Fails_Closed_On_A_Receipt_Naming_A_Non_Guid_Tenant` |
| 10776 | InstallationTenantCandidateLocator.cs:188 | Statement mutation | 11136 | NoCoverage | none |
| 14274 | TenantMembershipAuthorityStore.cs:1469 | Statement mutation | 14654 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14275 | TenantMembershipAuthorityStore.cs:1470 | Statement mutation | 14655 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14276 | TenantMembershipAuthorityStore.cs:1471 | Statement mutation | 14656 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14277 | TenantMembershipAuthorityStore.cs:1472 | Statement mutation | 14657 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14278 | TenantMembershipAuthorityStore.cs:1473 | Statement mutation | 14658 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14279 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14659 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14281 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14661 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14282 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14662 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14283 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14663 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14286 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14666 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14287 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14667 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14288 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14668 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14289 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14669 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14290 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14670 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14291 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14671 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14292 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14672 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14293 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14673 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14294 | TenantMembershipAuthorityStore.cs:1474 | Logical mutation | 14674 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14302 | TenantMembershipAuthorityStore.cs:1476 | Equality mutation | 14682 | Killed | `An_envelope_exactly_at_an_identity_bound_is_prepared` |
| 14304 | TenantMembershipAuthorityStore.cs:1477 | Equality mutation | 14684 | Killed | `An_envelope_exactly_at_an_identity_bound_is_prepared` |
| 14311 | TenantMembershipAuthorityStore.cs:1481 | Equality mutation | 14691 | Killed | `An_envelope_exactly_at_an_identity_bound_is_prepared` |
| 14313 | TenantMembershipAuthorityStore.cs:1482 | Equality mutation | 14693 | Killed | `An_envelope_exactly_at_an_identity_bound_is_prepared` |
| 14318 | TenantMembershipAuthorityStore.cs:1485 | Equality mutation | 14698 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14321 | TenantMembershipAuthorityStore.cs:1486 | Equality mutation | 14701 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14323 | TenantMembershipAuthorityStore.cs:1487 | Equality mutation | 14703 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14326 | TenantMembershipAuthorityStore.cs:1488 | Equality mutation | 14706 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 14330 | TenantMembershipAuthorityStore.cs:1491 | Statement mutation | 14710 | Killed | `An_envelope_past_an_identity_bound_is_refused_before_it_is_written` |
| 15932 | WebTenantSelectionAuthority.cs:83 | Statement mutation | 16315 | Killed | `Selection_authority_refuses_session_options_with_a_non_positive_lifetime` |
| 16249 | WebTenantSwitchAuthority.cs:77 | Statement mutation | 16632 | Killed | `Switch_authority_refuses_session_options_with_a_non_positive_lifetime` |

## Limits and equivalence claims

T-1009 has **31 listed mutants Killed and one NoCoverage**. Baseline 10776
(current 11136) removes `ThrowIfNull(services)` at
`InstallationTenantCandidateLocator.cs:188`. The following
`AddSingleton<IInstallationTenantCandidateLocator, InstallationTenantCandidateLocator>()`
also rejects a null collection. This supports an exception-type/parameter
equivalence argument, not a recorded kill or a proof of identical stack traces
and every observable effect. The raw status remains NoCoverage; ticket-owner
acceptance of the bounded equivalence argument remains separate.

T-1010 has **five listed mutants Killed and one Survived**. Baseline 8933
(current 9293) forces the false branch of `claim is null` at
`FounderTenantMembershipAttachService.cs:259`. The real `RedeemAsync` checks
`claim is null` and returns `ClaimRejected`; the caller then performs the same
redeemed-claim resolution. This supports equality of the ordinary refusal/status
outcome. It does not prove full observational equivalence: the mutated path also
calls the injected time provider before rejecting the claim. Do not mark this
survivor universally equivalent or give it mutation-kill credit.

The unlisted string-message mutant at store line 1491 remains Survived. The
listed mutant at that same line removes the throw statement and is Killed; they
are different mutations. The corrected 64-space digest case kills baseline
14278 in the final store report.

These are scoped runs over existing files, not the prescribed whole tenancy
slice rerun. They do not establish a new whole-slice score or justify changing
its floor. No threshold, gate or ticket acceptance requirement is waived.
Skipped JavaScript mutation jobs supply no evidence for these C# tests.

## Oracles and validation scope

The new tests use literal length/version bounds, refusal codes and parameter
names; the store theories invoke `PrepareAsync` and check that refused inputs
write no authority. Founder tests invoke the real service and assert literal
statuses. The four T-1010 early-return tests now capture the complete grant
snapshot before invoking the service and assert it is unchanged afterward,
ordered by grant ID and serialized by value. This invariant covers any subject,
including an unexpected principal, and preserves the fixture's installer grants.
The fixture's production-derived founder principal is not an expected value in
these four assertions.

The recovered reports describe the earlier `cda9800` test snapshot, whose
founder absence assertions used a production-derived principal. Those historical
kills do not establish the corrected complete-snapshot invariant. Native focused validation on 2026-10-02 rebuilt the host tests and passed
all 12 `FounderTenantMembershipAttachTests` (zero skipped). A temporary test-only
red control appended a real grant for `oracle-unexpected-subject` after each of
the four service calls: all four failed at the complete-snapshot assertion,
while the other eight tests passed. The injection was removed; after forcing
recompilation of the restored source, all 12 tests passed again. These checks
use SDK 11.0.100-rc.1.26425.128 and the existing T-1010 local feed version
`0.0.0-alpha.0.h43ec71c33440`; the feed was copied into this isolated clone,
without changing the checked-in Platform pin or shared worktrees. This focused
result is not a whole API gate or a new mutation report. The prior author's
1,468 passing filtered tests with 18 skipped remain prior results.
