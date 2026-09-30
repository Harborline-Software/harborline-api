# ck-4 G group 1: tenant-binding defence in depth, 2026-09-30

DES-0029 `kernel-core-ck-4` (Tenancy), under T-984. The tenant slice triage (`docs/evidence/ck4-tenant-slice-triage-2026-09-30.md`) ranked 20 tenant-binding mutants first among the real-but-not-silent (G) survivors. Each one weakens a tenant clause that a later check on the production path still refuses loudly, so no test isolated the first line of defence. This note records the tests that isolate each defence and the hand-applied mutation run that proves each one kills its mutants.

No production code changed, and no test revealed a defect. The host suite passes in Release at this branch (4,844 passed, 22 skipped, 0 failed; 4,866 total).

## Tests

Every test is tagged `[Trait("Holds", "kernel-core-ck-4")]`. They isolate one defence each: they call the validator directly, tamper one field of an otherwise consistent document, or feed a stubbed receipt or Party binding, so the named clause alone must refuse.

| Test | Class | Defence it isolates |
| --- | --- | --- |
| `A_prepared_intent_proposing_a_membership_in_another_tenant_is_refused_on_load` | `TenantMembershipAuthorityStoreTests` | intent tenant clause in `ValidateIntegrity`; a prepared intent has no membership row or receipt for a later check to catch |
| `Session_revocation_intents_sharing_a_correlation_id_are_refused_on_load` | `TenantMembershipAuthorityStoreTests` | revocation correlation uniqueness in the same `ValidateIntegrity` condition as the tenant clauses |
| `A_finalization_receipt_naming_another_tenant_is_refused_on_load` (3 cases) | `TenantMembershipAuthorityStoreTests` | receipt tenant clause for membership, session-selection and session-revocation receipts; the tenant id is in none of the receipt digests |
| `A_membership_mutation_with_a_non_guid_tenant_id_is_refused_before_it_is_written` | `TenantMembershipAuthorityStoreTests` | GUID tenant check in `ValidateEnvelopeIdentity`; the store is bound to the same non-GUID id, so its tenant-binding check passes |
| `A_selection_receipt_naming_another_tenant_is_refused_before_the_home_records_it` | `WebTenantSelectionAuthorityTests` | receipt tenant clause in `ValidateTenantReceipt`; without it the receipt is persisted and `SelectAsync` returns null only later |
| `A_stored_selection_whose_tenant_list_names_another_tenant_is_refused` | `WebTenantSelectionAuthorityTests` | tenant-list clause in `ValidateStoredSelection`, called directly on a real completed row |
| `Switch_refuses_a_target_party_binding_verified_under_another_tenant` | `WebTenantSwitchAuthorityTests` | Party verified-tenant clause in `ResolveTargetAuthorityAsync`; the reader echoes the principal |
| `Switch_refuses_a_tenant_receipt_naming_another_tenant` (2 cases) | `WebTenantSwitchAuthorityTests` | receipt tenant clause in `ValidateRevocationReceipt` and `ValidateSelectionReceipt` |
| `A_stored_switch_whose_old_and_target_tenant_are_the_same_is_refused` | `WebTenantSwitchAuthorityTests` | same-tenant clause in `ValidateStoredSwitch`, called directly on a row whose digests, fingerprint, correlation id and tenant list all agree; the distinct-tenant row built the same way is admitted |

## Mutation proof

Each mutant was applied by hand at the exact span and with the exact replacement in the archived Stryker report (`archive/ck4-tenant-slice-2026-09-30`, `5da90b94`). The mutated expression was parenthesised so C# precedence matched Stryker's syntax-tree replacement. The three touched test classes (32 tests) were then run in Release, and the production file was restored after each run. Every mutant turned exactly its named test red and no other test.

Mutants 16378 and 16380 do not compile as written under the host's nullable-warnings-as-errors setting (CS8602, `party` dereferenced after the null guard is weakened). Stryker compiles them with its own settings, so they survived in the report. They were rerun with `#pragma warning disable CS8602` added to the mutated file only.

| Mutant | Location | Mutation | Killing test | Result |
| --- | --- | --- | --- | --- |
| 14002 | TenantMembershipAuthorityStore.cs:1156 | membership-tenant `\|\|` intent-tenant to `&&` | `A_prepared_intent_proposing_a_membership_in_another_tenant_is_refused_on_load` | Killed |
| 14004 | TenantMembershipAuthorityStore.cs:1156 | revocation-uniqueness `\|\|` membership-tenant to `&&` | `Session_revocation_intents_sharing_a_correlation_id_are_refused_on_load` | Killed |
| 14022 | TenantMembershipAuthorityStore.cs:1166 | `Intents.Any` to `Intents.All` | `A_prepared_intent_proposing_a_membership_in_another_tenant_is_refused_on_load` | Killed |
| 14080 | TenantMembershipAuthorityStore.cs:1227 | receipt tenant `\|\|` membership id to `&&` | `A_finalization_receipt_naming_another_tenant_is_refused_on_load(intents)` | Killed |
| 14081 | TenantMembershipAuthorityStore.cs:1227 | owner version `\|\|` receipt tenant to `&&` | `A_finalization_receipt_naming_another_tenant_is_refused_on_load(intents)` | Killed |
| 14136 | TenantMembershipAuthorityStore.cs:1279 | selection receipt tenant `\|\|` membership id to `&&` | `A_finalization_receipt_naming_another_tenant_is_refused_on_load(sessionSelections)` | Killed |
| 14137 | TenantMembershipAuthorityStore.cs:1279 | owner version `\|\|` selection receipt tenant to `&&` | `A_finalization_receipt_naming_another_tenant_is_refused_on_load(sessionSelections)` | Killed |
| 14191 | TenantMembershipAuthorityStore.cs:1326 | revocation receipt tenant `\|\|` membership id to `&&` | `A_finalization_receipt_naming_another_tenant_is_refused_on_load(sessionRevocations)` | Killed |
| 14192 | TenantMembershipAuthorityStore.cs:1326 | owner version `\|\|` revocation receipt tenant to `&&` | `A_finalization_receipt_naming_another_tenant_is_refused_on_load(sessionRevocations)` | Killed |
| 14284 | TenantMembershipAuthorityStore.cs:1474 | GUID tenant check `\|\|` grant version to `&&` | `A_membership_mutation_with_a_non_guid_tenant_id_is_refused_before_it_is_written` | Killed |
| 14285 | TenantMembershipAuthorityStore.cs:1474 | target status `\|\|` GUID tenant check to `&&` | `A_membership_mutation_with_a_non_guid_tenant_id_is_refused_before_it_is_written` | Killed |
| 16023 | WebTenantSelectionAuthority.cs:308 | receipt tenant `\|\|` membership id to `&&` | `A_selection_receipt_naming_another_tenant_is_refused_before_the_home_records_it` | Killed |
| 16047 | WebTenantSelectionAuthority.cs:338 | tenant count `\|\|` tenant id to `&&` | `A_stored_selection_whose_tenant_list_names_another_tenant_is_refused` | Killed |
| 16352 | WebTenantSwitchAuthority.cs:341 | same-tenant `\|\|` tenant list to `&&` | `A_stored_switch_whose_old_and_target_tenant_are_the_same_is_refused` | Killed |
| 16354 | WebTenantSwitchAuthority.cs:341 | security version `\|\|` same-tenant to `&&` | `A_stored_switch_whose_old_and_target_tenant_are_the_same_is_refused` | Killed |
| 16378 | WebTenantSwitchAuthority.cs:385 | party guard conditional to `false` | `Switch_refuses_a_target_party_binding_verified_under_another_tenant` | Killed (CS8602 suppressed) |
| 16379 | WebTenantSwitchAuthority.cs:385 | verified tenant `\|\|` principal to `&&` | `Switch_refuses_a_target_party_binding_verified_under_another_tenant` | Killed |
| 16380 | WebTenantSwitchAuthority.cs:385 | party null `\|\|` verified tenant to `&&` | `Switch_refuses_a_target_party_binding_verified_under_another_tenant` | Killed (CS8602 suppressed) |
| 16548 | WebTenantSwitchAuthority.cs:746 | revocation receipt tenant `\|\|` membership id to `&&` | `Switch_refuses_a_tenant_receipt_naming_another_tenant(oldTenantReceipt: True)` | Killed |
| 16569 | WebTenantSwitchAuthority.cs:771 | selection receipt tenant `\|\|` membership id to `&&` | `Switch_refuses_a_tenant_receipt_naming_another_tenant(oldTenantReceipt: False)` | Killed |

All 20 were killed, and none was judged equivalent.

The membership-row tenant clause at line 1164 cannot be isolated from the intent tenant clause at line 1166. A membership row must hash to its latest finalized intent's payload digest, which is length-prefixed and covers the tenant id, so a foreign row always implies a foreign intent. Mutant 14004 is still killable, because it also disables the revocation correlation-uniqueness clause that shares the condition. Its killing test pins that clause.
