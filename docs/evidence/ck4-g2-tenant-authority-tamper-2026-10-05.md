# ck-4 G group 2: tenant authority document tamper detection, 2026-10-05

DES-0029 `kernel-core-ck-4` (Tenancy), ticket T-1005 (rank 2 of 7 in `ck4-tenant-slice-triage-2026-09-30.md`). The triage listed 73 real-but-not-silent (G) survivors in `TenantMembershipAuthorityStore`: `MutateAsync` 8, `LoadAsync` 2 and `ValidateIntegrity` 63.

**No production code changed, and no mutant exposed a defect.** The change adds tests, makes `TenantMembershipAuthorityStoreTests` partial, and records the slice baseline.

## Result

- **Every surviving mutant on the ticket's listed lines is now killed**, in the full slice run at `560dfc26`: 53 Killed and 1 Timeout (`attempt--`, an infinite loop). Ticket mutants already killed on `main` at `bc78de3d` are counted in the mapping's last column. The T-1004 tests (#307) killed most of them.
- **Slice `scope-tenancy-identity-tenant`** was `pending`. It is now recorded at **60.23%** (1,139 tested: 778 killed, 2 timeout, 359 survived, 156 no coverage), with `break` 60 in `eng/baselines/mutation-baseline.json`.
  - Command: `node eng/mutation-report.mjs --full --only apps/local-node-host/tests/tests.csproj --slice scope-tenancy-identity-tenant`
  - It ran on winbox in 109 minutes with dotnet-stryker 5.0.0, against all 5,507 host tests.
- **Store file, scoped run.** Same 17-class test filter both times.
  - Command: `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped Data/Identity/TenantMembershipAuthorityStore.cs --filter <17 classes>`
  - Score: 61.04% before, 74.69% after the first test commit.

### Equivalent mutants (not on the ticket's G list; the triage already classed them E)

| Mutant | Line | Mutation | Why it is equivalent |
| --- | ---: | --- | --- |
| 14593 | 598 | `ConfigureAwait(false)` → `true` | Equivalent because neither the node host nor xunit installs a `SynchronizationContext`, so both values resume on the thread pool identically. |
| 14612 | 623 | `ConfigureAwait(false)` → `true` | Same reason. |
| 14618 | 636 | `ConfigureAwait(false)` → `true` | Same reason. |

## Tests

Every test is tagged `[Trait("Holds", "kernel-core-ck-4")]` and lives in `apps/local-node-host/tests/Identity/TenantMembershipAuthorityStoreTests.Tamper.cs`.

**The fixture.** One consistent stored document holds every decision kind in every state:
- membership, selection and revocation intents, each prepared, finalized and aborted;
- account-1's membership, updated by a second finalized intent.

**The cases.** Each case tampers one fact. Where the tamper would also break the audit chain, it re-seals the chain with the tenant audit hash, so the clause under test is the only check that can refuse the document. The oracle is the literal refusal code.

| Test | What it pins |
| --- | --- |
| `The_full_authority_fixture_loads_including_an_updated_membership_and_aborted_intents` | The control: the untampered fixture loads (literal oracles). |
| `A_duplicated_authority_row_is_refused_on_load` (4 cases) | Uniqueness of membership accounts and of intent, selection and revocation correlation ids. |
| `A_tampered_audit_chain_is_refused_even_when_its_references_agree` (4 cases) | Sequence gap, broken previous-hash link, forged envelope hash and head sequence past the chain, each with every other hash and receipt consistent. |
| `A_prepared_decision_with_a_forged_digest_is_refused_on_load` (4 cases) | Membership payload and intent digests, and selection and revocation intent digests. |
| `An_unfinalized_decision_with_foreign_finalization_or_abort_evidence_is_refused_on_load` (3 kinds × 6 tampers) | A prepared decision carries no receipt and no finalization or abort decision. An abort names its decision: missing, empty and blank are refused. |
| `A_finalization_receipt_that_does_not_bind_its_envelope_or_version_is_refused_on_load` (9 cases) | For each kind: a receipt sequence with no envelope (its head hash still matches the real envelope), document version 0, and a version past the document. |
| `A_write_that_never_wins_its_compare_exchange_stops_after_sixteen_attempts` | The compare-exchange budget is exactly 16, and the failure is reported as contention. |
| `A_committed_write_reads_and_writes_the_document_exactly_once` | A committed write ends the loop. |
| `A_replayed_command_that_changes_nothing_leaves_the_stored_document_byte_identical` | A no-op replay writes nothing. |
| `A_stored_document_of_exactly_four_mib_loads_and_one_byte_more_is_refused` | The read ceiling is inclusive, padded with JSON whitespace. |
| `A_write_of_exactly_four_mib_commits_and_one_byte_more_is_refused` | The write ceiling is inclusive. A binary search over a sealed envelope's padding (one byte per character) proves the committed write is exactly 4,194,304 bytes. |
| `A_stored_document_that_decodes_to_nothing_is_refused_as_invalid_authority` | A stored JSON `null` is refused with the authority code. |

## Hand-mutant proof

**Method.** Each rule group's mutant was applied by hand, the test project was built in Release, and `FullyQualifiedName~TenantMembershipAuthorityStoreTests` was run, gated on `Build succeeded`. The source was then restored and checked byte-identical.

**The two clause deletions.** The last two rows delete a whole clause. They answer a review claim that the chain cases were not isolated: each deletion alone is killed.

| Group | Mutant | Killed by |
| --- | --- | --- |
| Uniqueness (1156) | first `or` → `and` | `A_duplicated_authority_row_is_refused_on_load` |
| Chain (1178) | `or` → `and` | `A_tampered_audit_chain_is_refused_even_when_its_references_agree` |
| Head (1187) | `or` → `and` | `A_tampered_audit_chain_is_refused_even_when_its_references_agree` |
| Selection digest (1257) | refusal removed | `A_prepared_decision_with_a_forged_digest_is_refused_on_load` |
| Unfinalized selection (1264) | `== Prepared` → `!= Prepared` | the fixture control and every fixture-based case (25 tests) |
| Membership receipt lookup (1225) | `and` → `or` | `A_finalization_receipt_that_does_not_bind_its_envelope_or_version_is_refused_on_load` |
| CAS budget (596) | `<` → `<=` | `A_write_that_never_wins_its_compare_exchange_stops_after_sixteen_attempts` |
| Commit ends loop (625) | `return` removed | `A_committed_write_reads_and_writes_the_document_exactly_once` |
| No-op write (601) | `return` removed | `A_replayed_command_that_changes_nothing_leaves_the_stored_document_byte_identical` |
| Read ceiling (657) | `>` → `>=` | `A_stored_document_of_exactly_four_mib_loads_and_one_byte_more_is_refused` |
| Write ceiling (610) | `>` → `>=` | `A_write_of_exactly_four_mib_commits_and_one_byte_more_is_refused` |
| Decode-null code (665) | message → `""` | `A_stored_document_that_decodes_to_nothing_is_refused_as_invalid_authority` |
| Latest intent (1348) | `OrderBy` → `OrderByDescending` | the fixture control and every fixture-based case (25 tests) |
| Envelope-hash clause (1180) | clause deleted | `A_tampered_audit_chain_is_refused_even_when_its_references_agree`, `Tampered_Authority_Evidence_Is_Refused` |
| Sequence clause (1178) | clause deleted | `A_tampered_audit_chain_is_refused_even_when_its_references_agree` |

## Mapping: the ticket's mutants to current ids

The ticket's ids come from candidate `9076915d`. The line numbers are unchanged at `bc78de3d`; the ids moved by about 850. The baseline is the scoped run at `bc78de3d`, and "after" is the full slice run at `560dfc26`.

| Line | Listed at `9076915d` | At `bc78de3d`: baseline → after (full slice run at `560dfc26`) | Already killed at baseline |
|---:|---|---|---:|
| 596 | 13741, 13742 | 14590 Equality: Survived → Killed; 14591 PostIncrementExpression to PostDecrementExpression: Survived → Timeout | 1 |
| 601 | 13747 | 14596 Statement: Survived → Killed | 0 |
| 610 | 13754 | 14603 Equality: Survived → Killed | 2 |
| 612 | 13757 | 14606 Statement: NoCoverage → Killed | 0 |
| 618 | 13762 | 14611 Negate expression: Survived → Killed | 0 |
| 625 | 13765 | 14614 Statement: Survived → Killed | 0 |
| 629 | 13766 | 14615 Statement: NoCoverage → Killed | 0 |
| 657 | 13774 | 14623 Equality: Survived → Killed | 2 |
| 659 | 13777 | 14626 Statement: NoCoverage → Killed | 0 |
| 1156 | 14005*, 14006*, 14007* | 14855 Logical: Survived → Killed; 14856 Logical: Survived → Killed | 4 |
| 1178 | 14032 | 14881 Logical: Survived → Killed | 3 |
| 1187 | 14038* | 14887 Logical: Survived → Killed | 2 |
| 1190 | 14043 | 14892 Statement: NoCoverage → Killed | 0 |
| 1203 | 14045* | 14894 Logical: Survived → Killed | 2 |
| 1206 | 14050 | 14899 Statement: NoCoverage → Killed | 0 |
| 1211 | 14053*, 14055*, 14056* | 14902 Logical: Survived → Killed; 14904 Logical: Survived → Killed; 14905 Logical: Survived → Killed | 2 |
| 1218 | 14068 | 14917 Statement: NoCoverage → Killed | 0 |
| 1225 | 14071 | 14920 Logical: Survived → Killed | 1 |
| 1227 | 14073, 14075, 14076, 14077, 14078, 14079, 14082, 14083* | 14931 Logical: Survived → Killed; 14932 Logical: Survived → Killed | 10 |
| 1228 | 14086* | 14935 Equality: Survived → Killed | 1 |
| 1243 | 14100 | — | 1 |
| 1257 | 14104 | 14953 Statement: NoCoverage → Killed | 0 |
| 1262 | 14107, 14109, 14110 | 14956 Logical: Survived → Killed; 14958 Logical: Survived → Killed; 14959 Logical: Survived → Killed | 2 |
| 1264 | 14114 | 14963 Equality: Survived → Killed | 1 |
| 1267 | 14118, 14119, 14120 | 14967 String: NoCoverage → Killed; 14968 String: NoCoverage → Killed; 14969 String: NoCoverage → Killed | 0 |
| 1269 | 14122 | 14971 Statement: NoCoverage → Killed | 0 |
| 1276 | 14126 | 14975 Logical: Survived → Killed | 2 |
| 1279 | 14131, 14133, 14134, 14135, 14138, 14139 | 14987 Logical: Survived → Killed; 14988 Logical: Survived → Killed | 8 |
| 1280 | 14142 | 14991 Equality: Survived → Killed | 1 |
| 1289 | 14154 | — | 1 |
| 1304 | 14158 | 15007 Statement: NoCoverage → Killed | 0 |
| 1309 | 14161, 14163, 14164 | 15010 Logical: Survived → Killed; 15012 Logical: Survived → Killed; 15013 Logical: Survived → Killed | 2 |
| 1311 | 14168 | 15017 Equality: Survived → Killed | 1 |
| 1314 | 14172, 14173, 14174 | 15021 String: NoCoverage → Killed; 15022 String: NoCoverage → Killed; 15023 String: NoCoverage → Killed | 0 |
| 1316 | 14176 | 15025 Statement: NoCoverage → Killed | 0 |
| 1323 | 14179, 14180 | 15028 Logical: Survived → Killed; 15029 Logical: Survived → Killed | 1 |
| 1326 | 14185, 14187, 14188, 14189, 14190, 14193, 14194 | 15042 Logical: Survived → Killed; 15043 Logical: Survived → Killed | 9 |
| 1327 | 14197 | 15046 Equality: Survived → Killed | 1 |
| 1328 | 14198 | 15047 Equality: Survived → Killed | 1 |
| 1337 | 14210 | — | 1 |
| 1344 | 14212* | 15061 Linq method (OrderBy() to OrderByDescending()): Survived → Killed | 0 |

Prior art: Stryker.NET mutation testing (per-test coverage, scoped `mutate` spans), and the tamper-evident hash-chain verification pattern from certificate transparency (RFC 6962), where each entry commits to its predecessor and the signed tree head. Each test breaks exactly one link and expects the chain to be refused.

A merged change does not certify `kernel-core-ck-4`.
