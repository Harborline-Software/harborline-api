# ck-4 tenant slice triage, 2026-09-30

DES-0029 `kernel-core-ck-4` (Tenancy), under T-984. This note triages every Survived and NoCoverage mutant of the `scope-tenancy-identity-tenant` Stryker slice at the release candidate, api `9076915d`, and records the tests that kill the silent-failure ones.

## The slice and the runs

The slice (`eng/baselines/mutation-slices.json`) mutates the files under `Data/Identity/` in `apps/local-node-host` that match `*Tenant*.cs`, `*Scope*.cs` or `*Isolation*.cs` and that no earlier slice owns: `TenantMembershipAuthorityStore.cs`, `WebTenantSwitchAuthority.cs`, `WebTenantSelectionAuthority.cs`, `FounderTenantMembershipAttachService.cs` and `InstallationTenantCandidateLocator.cs`. `LiveTenantMembershipAuthorityAdmission.cs` matches the globs but belongs to the earlier `refusals-admission` slice.

The nightly run, api `mutation.yml` run 36644970273 at `9076915d`, scored 45.94% (1,038 tested, 581 killed, 456 survived, 229 no coverage) and kept no report: `actions/upload-artifact` skips hidden files by default and `.stryker/` is a dot-folder. api #299 fixed that. Both runs below reproduce the workflow's command on winbox, one Stryker run at a time, in a worktree detached at `9076915d` with the local feed built at platform pin `0f308049`:

`node eng/mutation-report.mjs --full --only apps/local-node-host/tests/tests.csproj --slice scope-tenancy-identity-tenant` (Stryker.NET 5.0.0, `VSTEST_CONNECTION_TIMEOUT=300`).

| Run | Tests | Tested | Killed | Timeout | Survived | No coverage | Score | Report SHA-256 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| CI 36644970273, candidate | 4,779 | 1,038 | 581 | 1 | 456 | 229 | 45.94 | not kept |
| Local before, candidate | 4,779 | 1,038 | 582 | 37 | 419 | 229 | 48.86 | `c2b9e6a8e1b713ca90d3535e28ce6131e93fa9d58d325956ba295877a01ca85e` |
| Local after, candidate plus the three new tests | 4,782 | 1,042 | 586 | 1 | 455 | 225 | 46.33 | `8fa954d8b1b4ef6979d0191a2db809c786d19bc5d03734c40828924a11be9154` |

The CI timeout count is inferred from its score. The first local run overlapped the runner's own slice jobs on the same host and timed out 37 mutants; 35 of them survived in the second run, as in CI, so the like-for-like comparison is CI 45.94 before and 46.33 after. Three mutants flipped from Killed to Survived between the local runs (13710, 14274, 14275); that is test-order flakiness in whole-host runs, not a change in the tests. All four S mutants below went from Survived or NoCoverage to Killed. The triage covers the union of both runs: the 648 Survived and NoCoverage mutants of the first run plus the 38 that survived only in the second, 686 in all. Mutant ids are identical in both reports.

Both raw reports (about 25 MB each; they embed every host test source) are archived byte-for-byte on the orphan branch `archive/ck4-tenant-slice-2026-09-30` at commit `5da90b94`, under `docs/evidence/mutation/`, as `ck4-tenant-slice-before-2026-09-30.json` and `ck4-tenant-slice-after-2026-09-30.json`. The SHA-256 values above identify them.

## Classes

- **S, silent-failure risk**: the mutant could let a request act under the wrong tenant, skip a scope or isolation check, widen a query past its tenant filter, or accept a spoofed tenant, with no error anywhere on the path.
- **E, equivalent or out of contract**: `ConfigureAwait`, message text, a null guard that still ends in an exception, redundant or dead code.
- **G, real but not silent**: a behaviour change that fails loudly, or that only affects a non-security path. Where the mutant weakens a tenant-binding clause but a later check on the same path still refuses loudly, it is G and marked T in the tables.

Each mutant was read in context, not classified by mutator name. For the tenant-binding clauses the deciding question was whether any later check on the production path refuses. Two facts decided most of them. `InstallationIdentityCoordinatorService.ResolveUsableMembershipAsync` never compares the returned membership's tenant with the tenant it was asked for, so the store's own tenant binding is the only guard there. Every selected-session request goes through `WebSelectedSessionPrincipalAuthority`, which re-resolves the membership and re-checks the party's verified tenant, principal and party id, so a session minted from a bad party binding is refused on first use.

| Class | Mutants |
| --- | ---: |
| S, silent-failure risk | 4 (1 Survived, 3 NoCoverage, 0 Timeout-then-Survived) |
| E, equivalent or out of contract | 297 (200 Survived, 93 NoCoverage, 4 Timeout-then-Survived) |
| G, real but not silent | 385 (218 Survived, 133 NoCoverage, 34 Timeout-then-Survived) |
| Total | 686 |

## S mutants and the tests that kill them

| Mutant | Location | Status | Mutation | Killing test |
| --- | --- | --- | --- | --- |
| 8906 | FounderTenantMembershipAttachService.cs:158 (RunAsync) | NoCoverage | Block removal mutation: ` return FounderTenantMembershipAttachStatus.TenantDiverged;  -> (removed)` | `Founder_attach_on_a_host_serving_another_team_refuses_and_writes_no_tenant_authority` |
| 13780 | TenantMembershipAuthorityStore.cs:666 (LoadAsync) | Survived | Logical mutation: `\|\| -> &&` | `A_tenant_store_refuses_an_authority_document_written_for_another_tenant` |
| 13785 | TenantMembershipAuthorityStore.cs:669 (LoadAsync) | NoCoverage | Statement mutation: `throw new InvalidOperationException( "identity.tenant_authority_invali -> (removed)` | `A_tenant_store_refuses_an_authority_document_written_for_another_tenant` |
| 14025 | TenantMembershipAuthorityStore.cs:1169 (ValidateIntegrity) | NoCoverage | Statement mutation: `throw InvalidAuthorityDocument() -> (removed)` | `A_membership_row_naming_another_tenant_is_refused_even_when_the_document_is_consistent` |


### Red first

Each test was written against the candidate and passed, then failed with its mutant applied by hand. The production file was restored after each run, and no production code changed.

| Mutant applied by hand | Test | Result with the mutant |
| --- | --- | --- |
| `TenantMembershipAuthorityStore.cs:666`, `\|\|` to `&&` (13780) | `A_tenant_store_refuses_an_authority_document_written_for_another_tenant` | Failed: another tenant's document was read without an exception |
| `TenantMembershipAuthorityStore.cs:669`, throw removed (13785) | `A_tenant_store_refuses_an_authority_document_written_for_another_tenant` | Failed: the foreign document passed every later integrity check, because its tenant id, memberships and receipts agree with each other |
| `TenantMembershipAuthorityStore.cs:1169`, throw removed (14025) | `A_membership_row_naming_another_tenant_is_refused_even_when_the_document_is_consistent` | Failed: the store returned the foreign-tenant membership |
| `FounderTenantMembershipAttachService.cs:159`, return removed (8906) | `Founder_attach_on_a_host_serving_another_team_refuses_and_writes_no_tenant_authority` | Failed: `RunAsync` returned `Attached` and wrote the founder's Administrator grant into the genesis tenant |

The three tests are tagged `[Trait("Holds", "kernel-core-ck-4")]`. The founder test replaces the file's "not covered" note: the fixture already stands up the real coordinator and grant graph, so the divergence guard is now driven through `RunAsync`.

No S survivor revealed a tenancy defect in production code. The paths are sound at the candidate; they were untested.

## E list

176 mutants turn `ConfigureAwait(false)` into `ConfigureAwait(true)`; the host has no synchronization context, so each is equivalent. Ids: 8907, 8909, 8913, 8916, 8919, 8925, 8927, 8928, 8931, 8935, 8938, 8945, 10733, 13664, 13676, 13678, 13680, 13682, 13690, 13697, 13701, 13707, 13709, 13715, 13717, 13719, 13721, 13722, 13727, 13729, 13731, 13744, 13763, 13769, 15939, 15942, 15945, 15954, 15957, 15963, 15965, 15972, 15973, 15977, 15978, 15985, 15986, 15989, 15991, 15999, 16000, 16003, 16007, 16009, 16059, 16066, 16069, 16083, 16086, 16092, 16093, 16095, 16100, 16106, 16108, 16113, 16116, 16119, 16122, 16129, 16131, 16133, 16135, 16145, 16149, 16157, 16159, 16160, 16163, 16166, 16175, 16182, 16193, 16195, 16198, 16200, 16201, 16203, 16210, 16214, 16216, 16219, 16238, 16240, 16259, 16264, 16268, 16270, 16275, 16286, 16292, 16293, 16300, 16301, 16305, 16307, 16308, 16314, 16316, 16321, 16322, 16325, 16326, 16330, 16331, 16334, 16337, 16368, 16373, 16376, 16385, 16405, 16411, 16415, 16417, 16419, 16421, 16422, 16424, 16432, 16440, 16442, 16447, 16450, 16452, 16453, 16458, 16462, 16473, 16475, 16478, 16485, 16487, 16489, 16491, 16493, 16503, 16509, 16512, 16517, 16519, 16527, 16529, 16530, 16583, 16584, 16586, 16593, 16597, 16599, 16602, 16621, 16623, 16632, 16640, 16642, 16643, 16646, 16649, 16660, 16672, 16674, 13673*, 13675*, 13710*, 16147*.

63 mutants empty an exception message string; the refusal still throws the same exception type. Ids: 8953, 10773, 13713, 13725, 13737, 13738, 13758, 13767, 13778, 13779, 13786, 13800, 13809, 13818, 13825, 13832, 13850, 13858, 13862, 13874, 13880, 13884, 13891, 13907, 13915, 13919, 13931, 13937, 13941, 13948, 13958, 14231, 14238, 14249, 14256, 14271, 14331, 16013, 16016, 16035, 16043, 16044, 16057, 16141, 16153, 16209, 16226, 16247, 16347, 16348, 16366, 16433, 16457, 16499, 16514, 16515, 16523, 16537, 16561, 16581, 16592, 16609, 16693.

| Mutant | Location | Mutation | Reason |
| --- | --- | --- | --- |
| 8911 | FounderTenantMembershipAttachService.cs:174 | Block removal mutation: ` return FounderTenantMembershipAttachStatus.SkippedNoFounder;  -> (removed)` | null guard: without it rootGrant.AccountId throws NullReferenceException on the next statement |
| 8944 | FounderTenantMembershipAttachService.cs:279 | String mutation: `web-admission-evidence/v1 -> (removed)` | hash domain separator; the evidence digest is recomputed identically on replay and only compared with itself |
| 10729 | InstallationTenantCandidateLocator.cs:49 | Statement mutation: `ArgumentNullException.ThrowIfNull(candidates) -> (removed)` | null guard: candidates.ToArray() throws on null anyway |
| 10765 | InstallationTenantCandidateLocator.cs:157 | Null coalescing mutation (remove right): ` ?? [] -> (removed)` | null guard: a "null" receipt payload still throws in SelectMany |
| 10774 | InstallationTenantCandidateLocator.cs:175 | String mutation: `D -> (removed)` | Guid.ToString("") is the "D" format |
| 13661 | TenantMembershipAuthorityStore.cs:264 | Statement mutation: `cancellationToken.ThrowIfCancellationRequested() -> (removed)` | the awaited GetOrCreateAsync honours the same token |
| 13666 | TenantMembershipAuthorityStore.cs:317 | Statement mutation: `ArgumentException.ThrowIfNullOrWhiteSpace(tenantId) -> (removed)` | a blank tenant id still fails the document binding and the GUID check on every path |
| 13689 | TenantMembershipAuthorityStore.cs:412 | Statement mutation: `ArgumentException.ThrowIfNullOrWhiteSpace(grantId) -> (removed)` | dead code: ITenantMembershipGrantLookup has no production caller (only an arch-test allow-list names it) |
| 13691 | TenantMembershipAuthorityStore.cs:414 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | dead code: ITenantMembershipGrantLookup has no production caller (only an arch-test allow-list names it) |
| 13692 | TenantMembershipAuthorityStore.cs:414 | Equality mutation: `= -> !` | dead code: ITenantMembershipGrantLookup has no production caller (only an arch-test allow-list names it) |
| 13693 | TenantMembershipAuthorityStore.cs:415 | Conditional (true) mutation: `membership is null ? null : Project(membership -> (true?null :Project(membership)` | dead code: ITenantMembershipGrantLookup has no production caller (only an arch-test allow-list names it) |
| 13694 | TenantMembershipAuthorityStore.cs:415 | Conditional (false) mutation: `membership is null ? null : Project(membership -> (false?null :Project(membership)` | dead code: ITenantMembershipGrantLookup has no production caller (only an arch-test allow-list names it) |
| 13695 | TenantMembershipAuthorityStore.cs:415 | Equality mutation: `(block) -> ot n` | dead code: ITenantMembershipGrantLookup has no production caller (only an arch-test allow-list names it) |
| 13711 | TenantMembershipAuthorityStore.cs:475 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | absent receipt row: SingleOrDefault returns null and the next member access throws |
| 13723 | TenantMembershipAuthorityStore.cs:546 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | absent receipt row: SingleOrDefault returns null and the next member access throws |
| 13732 | TenantMembershipAuthorityStore.cs:580 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | absent intent: SingleOrDefault returns null and the next member access throws |
| 13845 | TenantMembershipAuthorityStore.cs:793 | Statement mutation: `ArgumentException.ThrowIfNullOrWhiteSpace(accountId) -> (removed)` | a blank account id matches no membership and is refused as identity.membership_unavailable |
| 13846 | TenantMembershipAuthorityStore.cs:794 | Statement mutation: `ArgumentException.ThrowIfNullOrWhiteSpace(membershipId) -> (removed)` | a blank membership id matches no membership and is refused as identity.membership_unavailable |
| 13864 | TenantMembershipAuthorityStore.cs:818 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | an absent row throws from Single() instead of the explicit refusal; both are loud |
| 13878 | TenantMembershipAuthorityStore.cs:852 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | an absent row throws from Single() instead of the explicit refusal; both are loud |
| 13895 | TenantMembershipAuthorityStore.cs:884 | String mutation: `string.Empty -> "Stryker was here!"` | placeholder overwritten by the computed envelope hash before the envelope is stored |
| 13901 | TenantMembershipAuthorityStore.cs:908 | Statement mutation: `ArgumentException.ThrowIfNullOrWhiteSpace(accountId) -> (removed)` | a blank account id matches no membership and is refused as identity.membership_unavailable |
| 13902 | TenantMembershipAuthorityStore.cs:909 | Statement mutation: `ArgumentException.ThrowIfNullOrWhiteSpace(membershipId) -> (removed)` | a blank membership id matches no membership and is refused as identity.membership_unavailable |
| 13921 | TenantMembershipAuthorityStore.cs:933 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | an absent row throws from Single() instead of the explicit refusal; both are loud |
| 13935 | TenantMembershipAuthorityStore.cs:969 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | an absent row throws from Single() instead of the explicit refusal; both are loud |
| 13952 | TenantMembershipAuthorityStore.cs:1001 | String mutation: `string.Empty -> "Stryker was here!"` | placeholder overwritten by the computed envelope hash before the envelope is stored |
| 13973 | TenantMembershipAuthorityStore.cs:1048 | String mutation: `string.Empty -> "Stryker was here!"` | placeholder overwritten by the computed envelope hash before the envelope is stored |
| 13986 | TenantMembershipAuthorityStore.cs:1098 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | absent audit row: SingleOrDefault returns null and the next member access throws |
| 13996 | TenantMembershipAuthorityStore.cs:1119 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | absent audit row: SingleOrDefault returns null and the next member access throws |
| 14070 | TenantMembershipAuthorityStore.cs:1224 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | an absent row throws from Single() instead of the explicit refusal; both are loud |
| 14124 | TenantMembershipAuthorityStore.cs:1275 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | an absent row throws from Single() instead of the explicit refusal; both are loud |
| 14178 | TenantMembershipAuthorityStore.cs:1322 | Linq method mutation (SingleOrDefault() to Single()): `OrDefault -> (removed)` | an absent row throws from Single() instead of the explicit refusal; both are loud |
| 15990 | WebTenantSelectionAuthority.cs:227 | Statement mutation: `ValidateTenantReceipt(home, receipt) -> (removed)` | redundant: RequireDurableTenantReceiptAsync validates the identical receipt before any session is minted |
| 15993 | WebTenantSelectionAuthority.cs:236 | Logical mutation: `\|\| -> &&` | redundant: a receipt naming another tenant is refused by ValidateTenantReceipt in RequireDurableTenantReceiptAsync on the next statement; the Finalizing arm is always true here |
| 15997 | WebTenantSelectionAuthority.cs:238 | Block removal mutation: ` return null;  -> (removed)` | redundant: a receipt naming another tenant is refused by ValidateTenantReceipt in RequireDurableTenantReceiptAsync on the next statement; the Finalizing arm is always true here |
| 16002 | WebTenantSelectionAuthority.cs:248 | Block removal mutation: ` return null;  -> (removed)` | dead code: CompleteWithInstallationAuditAsync returns Completed or throws |
| 16046 | WebTenantSelectionAuthority.cs:331 | Null coalescing mutation (remove right): ` ?? [] -> (removed)` | null guard: a "null" tenant list still throws on tenants.Length |
| 16078 | WebTenantSelectionAuthority.cs:387 | String mutation: `D -> (removed)` | Guid.ToString("") is the "D" format |
| 16089 | WebTenantSelectionAuthority.cs:413 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | equivalent: the tenant was proven to be in candidates a few lines earlier |
| 16102 | WebTenantSelectionAuthority.cs:457 | String mutation: `[] -> (removed)` | initial value never read: PersistReceiptAndFinalizeAsync overwrites it before any reader |
| 16125 | WebTenantSelectionAuthority.cs:498 | String mutation: `identity.session_selection_aborted -> (removed)` | failure-code text on an aborted row |
| 16189 | WebTenantSelectionAuthority.cs:641 | Remove checked expression: `ecked(challenge.OwnerVersion + 1) -> allenge.OwnerVersion + 1` | overflow guard on a version that cannot reach long.MaxValue |
| 16231 | WebTenantSelectionAuthority.cs:718 | String mutation: `string.Empty -> "Stryker was here!"` | placeholder overwritten by the computed envelope hash before the envelope is stored |
| 16243 | WebTenantSelectionAuthority.cs:742 | Null coalescing mutation (remove right): ` ?? [] -> (removed)` | null guard: a "null" receipt list still throws on receipts.Length |
| 16258 | WebTenantSwitchAuthority.cs:91 | String mutation: `D -> (removed)` | Guid.ToString("") is the "D" format |
| 16328 | WebTenantSwitchAuthority.cs:261 | Block removal mutation: ` return null;  -> (removed)` | dead code: the Preparing and Committing arms always leave the home Finalizing here |
| 16333 | WebTenantSwitchAuthority.cs:276 | Block removal mutation: ` return null;  -> (removed)` | dead code: CompleteWithInstallationAuditAsync returns Completed or throws |
| 16350 | WebTenantSwitchAuthority.cs:327 | Null coalescing mutation (remove right): ` ?? [] -> (removed)` | null guard: a "null" tenant list still fails SequenceEqual with an exception |
| 16375 | WebTenantSwitchAuthority.cs:378 | Block removal mutation: ` return null;  -> (removed)` | null guard: a missing candidate still throws on candidate.DisplayLabel before a payload exists |
| 16400 | WebTenantSwitchAuthority.cs:411 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | equivalent: PinnedGrantOwnerVersions.Count == 1 is checked first |
| 16402 | WebTenantSwitchAuthority.cs:413 | Linq method mutation (Single() to SingleOrDefault()): `(block) -> OrDefault` | equivalent: PinnedGrantOwnerVersions.Count == 1 is checked first |
| 16436 | WebTenantSwitchAuthority.cs:497 | String mutation: `[] -> (removed)` | initial value never read: PersistReceiptsAndFinalizeAsync overwrites it before any reader |
| 16481 | WebTenantSwitchAuthority.cs:594 | String mutation: `identity.session_switch_aborted -> (removed)` | failure-code text on an aborted row |
| 16510 | WebTenantSwitchAuthority.cs:663 | Statement mutation: `ValidateRevocationReceipt(home, payload, revocation) -> (removed)` | redundant: RequireDurableTenantReceiptsAsync revalidates the same receipt before rotation |
| 16513 | WebTenantSwitchAuthority.cs:673 | Statement mutation: `ValidateSelectionReceipt(home, payload, selection) -> (removed)` | redundant: RequireDurableTenantReceiptsAsync revalidates the same receipt before rotation |
| 16614 | WebTenantSwitchAuthority.cs:841 | String mutation: `string.Empty -> "Stryker was here!"` | placeholder overwritten by the computed envelope hash before the envelope is stored |
| 16648 | WebTenantSwitchAuthority.cs:902 | Boolean mutation: `fals -> tru` | change-tracking of a row that is only read; SaveChanges writes nothing for it |
| 16670 | WebTenantSwitchAuthority.cs:956 | String mutation: `N -> (removed)` | opaque revocation row id: unique in either format |

## G groups by file

### FounderTenantMembershipAttachService.cs (7)

| Method | Mutants | Ids (id:line; T = tenant-binding clause a later check still refuses loudly; * = Timeout in the first run, Survived in the second) |
| --- | ---: | --- |
| RunAsync | 6 | 8915:182, 8918:189, 8930:242, 8933:259, 8939:267, 8943:268 |
| FounderRosterPartyProvider | 1 | 8952:326 |

### InstallationTenantCandidateLocator.cs (2)

| Method | Mutants | Ids (id:line; T = tenant-binding clause a later check still refuses loudly; * = Timeout in the first run, Survived in the second) |
| --- | ---: | --- |
| CanonicalTenantId | 1 | 10772:171 |
| AddInstallationTenantCandidateClassification | 1 | 10776*:188 |

### TenantMembershipAuthorityStore.cs (189)

| Method | Mutants | Ids (id:line; T = tenant-binding clause a later check still refuses loudly; * = Timeout in the first run, Survived in the second) |
| --- | ---: | --- |
| IsAdmissionBlockedAsync | 1 | 13702:431 |
| AbortSessionSelectionAsync | 1 | 13716:494 |
| AbortSessionRevocationAsync | 1 | 13728:565 |
| GetFinalizationReceiptAsync | 1 | 13736:583 |
| MutateAsync | 8 | 13741:596, 13742:596, 13747:601, 13754:610, 13757:612, 13762:618, 13765:625, 13766:629 |
| LoadAsync | 2 | 13774:657, 13777:659 |
| Prepare | 13 | 13797:702, 13799:704, 13801:708, 13808:714, 13817:721, 13824:725, 13831:729, 13791*:691, 13834*:734, 13835*:739, 13836*:740, 13838*:742, 13839*:743 |
| PrepareSessionSelection | 11 | 13843:791, 13844:792, 13849:797, 13855:805, 13857:807, 13859:810, 13861:812, 13863:815, 13865:819, 13868:820, 13873:822 |
| FinalizeSessionSelection | 3 | 13883:858, 13887:863, 13890:867 |
| PrepareSessionRevocation | 13 | 13899:906, 13900:907, 13903:910, 13906:913, 13909:917, 13912:920, 13914:922, 13916:925, 13918:927, 13920:930, 13922:934, 13925:935, 13930:937 |
| FinalizeSessionRevocation | 3 | 13940:975, 13944:980, 13947:984 |
| Finalize | 4 | 13959:1022, 13962:1025, 13967:1033, 13956*:1020 |
| SealSessionRevocationReceipts | 1 | 13997:1120 |
| ValidateIntegrity | 72 | 14032:1178, 14043:1190, 14050:1206, 14068:1218, 14071:1225, 14073:1227, 14075:1227, 14076:1227, 14077:1227, 14078:1227, 14079:1227, 14080T:1227, 14081T:1227, 14082:1227, 14100:1243, 14104:1257, 14107:1262, 14109:1262, 14110:1262, 14114:1264, 14118:1267, 14119:1267, 14120:1267, 14122:1269, 14126:1276, 14131:1279, 14133:1279, 14134:1279, 14135:1279, 14136T:1279, 14137T:1279, 14138:1279, 14139:1279, 14142:1280, 14154:1289, 14158:1304, 14161:1309, 14163:1309, 14164:1309, 14168:1311, 14172:1314, 14173:1314, 14174:1314, 14176:1316, 14179:1323, 14180:1323, 14185:1326, 14187:1326, 14188:1326, 14189:1326, 14190:1326, 14191T:1326, 14192T:1326, 14193:1326, 14194:1326, 14197:1327, 14198:1328, 14210:1337, 14002T*:1156, 14004T*:1156, 14005*:1156, 14006*:1156, 14007*:1156, 14022T*:1166, 14038*:1187, 14045*:1203, 14053*:1211, 14055*:1211, 14056*:1211, 14083*:1227, 14086*:1228, 14212*:1344 |
| AbortSessionSelection | 11 | 14223:1371, 14224:1372, 14225:1373, 14227:1375, 14228:1377, 14230:1379, 14232:1382, 14234:1384, 14235:1386, 14237:1388, 14239:1395 |
| AbortSessionRevocation | 11 | 14241:1405, 14242:1406, 14243:1407, 14245:1409, 14246:1411, 14248:1413, 14250:1416, 14252:1418, 14253:1420, 14255:1422, 14257:1429 |
| Abort | 4 | 14263:1442, 14264:1444, 14267:1447, 14270:1451 |
| ValidateEnvelopeIdentity | 29 | 14285T:1474, 14286:1474, 14287:1474, 14288:1474, 14289:1474, 14290:1474, 14291:1474, 14292:1474, 14293:1474, 14294:1474, 14302:1476, 14304:1477, 14311:1481, 14313:1482, 14318:1485, 14321:1486, 14323:1487, 14326:1488, 14330:1491, 14274*:1469, 14275*:1470, 14276*:1471, 14277*:1472, 14278*:1473, 14279*:1474, 14281*:1474, 14282*:1474, 14283*:1474, 14284T*:1474 |

### WebTenantSelectionAuthority.cs (62)

| Method | Mutants | Ids (id:line; T = tenant-binding clause a later check still refuses loudly; * = Timeout in the first run, Survived in the second) |
| --- | ---: | --- |
| SelectAsync | 11 | 15938:93, 15946:126, 15948:126, 15949:126, 15953:128, 15958:151, 15962:153, 15967:165, 15971:174, 15979:205, 16006:266 |
| RequireDurableTenantReceiptAsync | 2 | 16012:289, 16014:292 |
| ValidateTenantReceipt | 9 | 16017:308, 16019:308, 16020:308, 16021:308, 16022:308, 16023T:308, 16027:310, 16029:311, 16034:316 |
| ValidateStoredSelection | 6 | 16037:323, 16042:325, 16047T:338, 16049:338, 16050:338, 16056:342 |
| GetOrCreateHomeAsync | 6 | 16098:435, 16109:472, 16112:474, 16114:475, 16115:477, 16117:479 |
| AbortPreparingAsync | 2 | 16126:499, 16127:499 |
| AdvanceHomeAsync | 4 | 16137:524, 16140:529, 16142:533, 16143:533 |
| PersistReceiptAndFinalizeAsync | 3 | 16152:551, 16154:556, 16155:556 |
| MintSelectedSessionAsync | 7 | 16164:578, 16167:584, 16169:584, 16170:584, 16174:586, 16184:599, 16197:656 |
| CompleteWithInstallationAuditAsync | 10 | 16205:675, 16208:680, 16211:688, 16220:698, 16225:701, 16230:713, 16233:729, 16234:729, 16235:733, 16236:733 |
| DeserializeReceipt | 1 | 16244:743 |
| WebTenantSelectionAuthority | 1 | 15932*:83 |

### WebTenantSwitchAuthority.cs (125)

| Method | Mutants | Ids (id:line; T = tenant-binding clause a later check still refuses loudly; * = Timeout in the first run, Survived in the second) |
| --- | ---: | --- |
| SwitchAsync | 12 | 16260:94, 16278:136, 16280:136, 16285:149, 16287:158, 16299:176, 16309:219, 16311:219, 16312:219, 16320:232, 16329:265, 16336:292 |
| ValidateStoredSwitchTenants | 1 | 16339:309 |
| ValidateStoredSwitch | 8 | 16341:319, 16346:321, 16352T:341, 16354T:341, 16355:341, 16356:341, 16357:341, 16365:348 |
| ResolveTargetAuthorityAsync | 3 | 16378T:385, 16379T:385, 16380T:385 |
| OldAuthorityIsCurrentAsync | 7 | 16386:406, 16387:406, 16388:406, 16389:406, 16390:406, 16391:406, 16392:406 |
| RequireCurrentActiveSessionAsync | 3 | 16406:425, 16407:425, 16412:429 |
| CreateHomeAsync | 7 | 16426:466, 16427:470, 16428:470, 16443:512, 16446:514, 16448:515, 16449:517 |
| AcquireLeasesAsync | 3 | 16455:541, 16461:554, 16463:557 |
| ReleaseLeasesAsync | 3 | 16467:564, 16468:564, 16472:568 |
| AbortPreparingAsync | 7 | 16477:589, 16479:591, 16482:595, 16483:595, 16484:597, 16486:600, 16488:606 |
| AdvanceHomeAsync | 4 | 16495:626, 16498:631, 16500:635, 16501:635 |
| FinalizeTenantHeadsAsync | 2 | 16505:649, 16507:655 |
| PersistReceiptsAndFinalizeAsync | 3 | 16522:695, 16524:700, 16525:700 |
| RequireDurableTenantReceiptsAsync | 4 | 16531:725, 16536:727, 16538:730, 16539:731 |
| ValidateRevocationReceipt | 10 | 16541:746, 16543:746, 16544:746, 16545:746, 16546:746, 16547:746, 16548T:746, 16553:749, 16555:750, 16560:755 |
| ValidateSelectionReceipt | 9 | 16563:771, 16565:771, 16566:771, 16567:771, 16568:771, 16569T:771, 16573:773, 16575:774, 16580:779 |
| CompleteWithInstallationAuditAsync | 10 | 16588:798, 16591:803, 16594:811, 16603:821, 16608:824, 16613:836, 16616:852, 16617:852, 16618:856, 16619:856 |
| RotateSessionsAsync | 19 | 16625:870, 16630:870, 16631:871, 16635:878, 16636:878, 16641:882, 16647:896, 16650:905, 16652:905, 16653:905, 16657:912, 16658:912, 16659:913, 16661:915, 16663:929, 16664:929, 16665:929, 16666:930, 16668:954 |
| HasSameAuthorityCoordinates | 9 | 16675:978, 16676:978, 16677:978, 16678:978, 16679:978, 16680:978, 16681:978, 16682:978, 16683:978 |
| WebTenantSwitchAuthority | 1 | 16249*:77 |


## Tickets the G groups suggest

Ranked by T-719 silent-failure risk, highest first. Each is a per-area test ticket; the break starts at the slice baseline and ratchets.

1. **Tenant-binding defence in depth (the 20 T ids).** Receipt tenant clauses in `TenantMembershipAuthorityStore.ValidateIntegrity` (finalization, selection and revocation receipts), `WebTenantSelectionAuthority.ValidateTenantReceipt` and `ValidateStoredSelection`, `WebTenantSwitchAuthority.ValidateRevocationReceipt`, `ValidateSelectionReceipt` and `ValidateStoredSwitch`, the party verified-tenant check in `WebTenantSwitchAuthority.ResolveTargetAuthorityAsync`, the membership and intent tenant clauses at `TenantMembershipAuthorityStore.cs:1156-1166`, and the GUID tenant check in `ValidateEnvelopeIdentity`. A later check refuses each one today, so none is silent, but each layer should hold on its own.
2. **Tenant authority document integrity (`TenantMembershipAuthorityStore.ValidateIntegrity`, `LoadAsync`, `MutateAsync`).** Tamper evidence for the audit chain, intent digests and receipt versions, plus the CAS retry budget and the size ceiling. Tests follow `Tampered_Authority_Evidence_Is_Refused`: tamper one field and expect `identity.tenant_authority_invalid`.
3. **Effect-site revalidation in switch and selection.** `WebTenantSwitchAuthority.SwitchAsync` (the pre-commit recheck at line 219), `OldAuthorityIsCurrentAsync`, `RotateSessionsAsync` (a revoked or changed old session, `HasSameAuthorityCoordinates`, idle-expiry clamping), and `WebTenantSelectionAuthority.SelectAsync` and `MintSelectedSessionAsync` (revoked, consumed or expired challenges, stale authority after prepare). The per-request principal authority or a duplicate check refuses each one later; the tests should pin the refusal at the effect site.
4. **Session intent state machine in the tenant store.** `PrepareSessionSelection`, `PrepareSessionRevocation`, `AbortSessionSelection` and `AbortSessionRevocation` (never exercised: every mutant there is NoCoverage), `Abort`, `Finalize`, and the membership-busy fence in `IsAdmissionBlockedAsync`.
5. **Coordinator row bookkeeping in switch and selection.** `AbortPreparingAsync`, `AdvanceHomeAsync`, `PersistReceiptAndFinalizeAsync` and `PersistReceiptsAndFinalizeAsync`, `CompleteWithInstallationAuditAsync` (owner-version increments, audit envelope fields, the root-epoch and chain check), duplicate-key recovery in `CreateHomeAsync` and `GetOrCreateHomeAsync`, and lease ordering and release.
6. **Input validation.** Length and version bounds in `TenantMembershipAuthorityStore.ValidateEnvelopeIdentity`, the blank-id guards in the session prepare methods, `SessionOptions.Validate()` in both authority constructors, and the null guards in `FounderRosterPartyProvider` and `AddInstallationTenantCandidateClassification`.
7. **Founder attach availability paths.** `FounderTenantMembershipAttachService.RunAsync` returns early after a missing account, installation, party binding or grant issuance; without those returns each path ends in a later exception rather than `SkippedNoFounder` or `Unavailable`.

`ITenantMembershipGrantLookup.FindMembershipByGrantAsync` has no production caller, so its six mutants are E as dead code. Deleting it is a separate cleanup.
