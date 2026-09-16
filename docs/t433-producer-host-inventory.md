# T433 producer measured host inventory

## Read-only data-source review repair

The complete direct Release host run at `a76ca9443f4953b9865db34b7767569683adfce1` passed **4,303 total / 4,282 passed / 0 failed / 21 skipped**, without retries, from 2026-09-16T10:43:34.8868452Z to 10:48:28.0272978Z (console test duration 4m50s). Full solution restore/build and contracts/capability-host prerequisites completed first. This is a measured host result, not a full-gate receipt.

Candidate TRX: `artifacts/t433-action-contract/data-source-full-host-a76ca944.trx`, SHA-256 `a4a6775fc502bf15f894cd0cd86a411b4ba3199d046463ef0f5337cac13a3714`. Previous green exact-clone TRX: `artifacts/t433-action-contract/authority-reset-host-19649748.trx`, SHA-256 `82baf2b3d539198b39471bafe3fb36355e1119c7145a2025cb4b5b69c7cd3b6d`. Exact executed-row comparison proves **nine additions across three methods, zero removals**; all 21 skipped identities are byte-for-byte identical. Cumulatively this is 139 added rows across 71 methods versus central main. Failure, skip, known-flaky, retry and other-OS baseline policies remain unchanged.

All nine additions belong to `Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests`:

| Method | Exact descriptorId argument | Added rows |
| --- | --- | ---: |
| Data_source_admission_refuses_mutations_non_lists_and_unknown_descriptors | authorization.grant.review.v1 | 1 |
| Data_source_admission_refuses_mutations_non_lists_and_unknown_descriptors | authorization.holders.read.v1 | 1 |
| Data_source_admission_refuses_mutations_non_lists_and_unknown_descriptors | records.read.v1 | 1 |
| Data_source_admission_refuses_mutations_non_lists_and_unknown_descriptors | unknown | 1 |
| Data_source_compilation_refuses_mutations_non_lists_and_unknown_descriptors | authorization.grant.review.v1 | 1 |
| Data_source_compilation_refuses_mutations_non_lists_and_unknown_descriptors | authorization.holders.read.v1 | 1 |
| Data_source_compilation_refuses_mutations_non_lists_and_unknown_descriptors | records.read.v1 | 1 |
| Data_source_compilation_refuses_mutations_non_lists_and_unknown_descriptors | unknown | 1 |
| Data_source_admits_and_compiles_registered_read_only_list_metadata | none | 1 |

## Earlier producer integration measurement

The quality-enabled exact-clone run at `bbfcc15ba35879dd460b86e6a8e510cba95ad953` measured **4,294 total / 4,273 passed / 0 failed / 21 skipped**, without retries, from 2026-09-16T09:30:34Z to 09:35:20Z. The wrapper completed with only the previous host-count baseline mismatch and no receipt. Updating this measured Windows tuple still requires a new committed-head full gate.

Candidate TRX: `artifacts/t433-action-contract/pool-isolation-host-bbfcc15b.trx`, SHA-256 `4527b098e34808e03b561baca1da4c4bb4951c3a53d3fd2815e73ff0cf71103f`. Central comparison TRX: `review-resource-lifetime-exact-host.trx`, SHA-256 `a4abadc21cec5de79cbdc8e671379c3837edb3fb3427588e63471d8530a4bfe3`, measuring 4,164 / 4,143 / 0 / 21. Fully qualified method identity plus executed-row multiplicity yields **130 additions across 68 methods, zero removals**. All 21 skipped identities are unchanged. Long xUnit theory displays truncate two distinct argument cases to duplicate labels; row multiplicity, not unique display strings, is authoritative.

The additions comprise 103 producer/reconciliation cases, six explicit refusal-renderer proof rows, ten sequential replay-context cases, and eleven concurrent-first-use/store cases. The SQLite canary isolation changes no identity or assertion. [Integration verification](t433-producer-integration-verification.md) preserves red/green failures and root-cause repairs. Failure, skip, retry and other-OS baseline policies are unchanged.

| Fully qualified method | Central rows | Producer rows | Delta |
| --- | ---: | ---: | ---: |
| Harborline.Api.LocalNodeHost.Tests.ArchTests.AuthorizationRefusalRenderingFenceTests.Selected_catcher_proof_rejects_planted_exception_serialization | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.ArchTests.AuthorizationRefusalRenderingFenceTests.Selected_route_catchers_only_pass_the_exception_to_the_shared_renderer | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.AssetRegistry.AssetRegistryEntityReadAuthorizationTests.Selected_cookie_without_its_principal_never_falls_back_or_reads | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.AssetRegistry.AssetRegistryEntityReadAuthorizationTests.Selected_record_read_never_reads_the_ambient_tenants_matching_record | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.AssetRegistry.AssetRegistryEntityReadAuthorizationTests.Selected_record_read_uses_selected_tenant_and_returns_its_actual_audit_receipt | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Audit.KernelAuditMetadataRouteTests.Denied_metadata_read_never_queries_kernel_trail | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Audit.KernelAuditMetadataRouteTests.Invalid_range_and_unknown_cursor_are_explicit_refusals | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Audit.KernelAuditMetadataRouteTests.Metadata_pages_actual_kernel_ids_in_append_order_without_payload_values | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Audit.KernelAuditMetadataRouteTests.Selected_role_vocabulary_reads_real_rows_with_tenant_filter_and_unchanged_gate | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Authorization.GrantScopeNarrowingTests.A_duplicate_successor_id_rolls_back_both_legs | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Authorization.GrantScopeNarrowingTests.Frozen_scope_transition_replaces_one_grant_preserving_role_subject_and_history | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Authorization.GrantScopeNarrowingTests.Narrowing_last_administrator_cannot_leave_install_without_an_administrator | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Authorization.GrantScopeNarrowingTests.Wider_equal_or_sibling_scope_cannot_mutate_grant_or_epoch | 0 | 6 | +6 |
| Harborline.Api.LocalNodeHost.Tests.Authorization.SharedRouteGuardPointOfUseTests.A_selected_principals_tenant_cannot_be_replaced_by_route_authority | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Entities.RequireNewEntityCreationTests.Batch_create_honors_require_new_and_rolls_back_only_its_new_prefix | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Entities.RequireNewEntityCreationTests.Canceled_required_insert_leaves_no_claim_and_retry_succeeds | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Entities.RequireNewEntityCreationTests.Single_create_preserves_default_adoption_but_require_new_refuses_equal_body | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Canceled_create_wait_leaves_no_entity_mint_or_projection_and_retry_can_claim_key | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Concurrent_first_use_adopts_only_matching_context_without_second_mint | 0 | 4 | +4 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Desktop_changed_payload_replay_conflicts_before_projection_without_mutation | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Same_payload_without_correlation_replays_without_duplicate_entity_or_mint | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Selected_replay_context_mismatch_never_reaches_projection_or_mutates | 0 | 7 | +7 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Selected_submit_caught_denial_uses_renderer_without_exception_or_private_decision_on_wire | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Selected_submit_changed_payload_with_same_key_and_correlation_refuses_without_second_mint | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Selected_submit_refuses_malformed_or_changed_replay_correlation | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Selected_submit_requires_principal_and_antiforgery | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Forms.FormsRouteTests.Selected_submit_uses_its_tenant_and_real_form_engine_idempotent_receipt | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AccessHoldersReadCompositionTests.Selected_holder_projection_uses_same_gate_and_rows_as_desktop_read | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminNarrowMemberGrantTests.Correlated_grant_actions_replay_without_mutation_and_reject_changed_intent | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminNarrowMemberGrantTests.Governed_review_stamps_server_actor_and_time_without_changing_authority | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminNarrowMemberGrantTests.Grant_only_revocation_preserves_roster_and_membership_with_stable_replay_receipt | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminNarrowMemberGrantTests.Grant_review_and_revoke_denials_leave_store_and_epoch_unchanged | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminNarrowMemberGrantTests.Scope_narrowing_denial_writes_no_grant_or_epoch | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminNarrowMemberGrantTests.Scope_narrowing_preserves_role_and_removes_outside_authority_with_correlated_receipts | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminTeamAccessAuthorityTests.Grant_only_revocation_never_enters_the_roster_writer_and_preserves_other_grants | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminTeamAccessRoutesTests.Grant_only_route_calls_only_grant_service_and_exposes_receipt | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminTeamAccessRoutesTests.Grant_only_route_refuses_missing_session_or_antiforgery_before_authority | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminTeamAccessRoutesTests.Review_route_binds_selected_actor_and_forwards_distinct_server_receipt | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Identity.AdminTeamAccessRoutesTests.Scope_narrow_route_binds_selected_tenant_and_forwards_server_receipt | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_caught_denial_uses_renderer_without_exception_or_private_decision_on_wire | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_exposes_native_activation_refusal_as_422_with_inactive_draft | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_install_refusal_keeps_active_and_installed_state_unchanged | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_invalid_artifact_keeps_active_and_installed_state_unchanged | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_postcommit_diagnostic_preserves_success_and_carried_correlation | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_real_signed_probe_preserves_active_runtime_and_returns_native_pointer | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_refuses_before_body_read_without_authority_or_csrf | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.Selected_replacement_reports_draft_and_activation_separately | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.AccessAdministrationPreloadTests.T433_predeclared_key_drives_real_engine_instance_and_workflow_grant_with_idempotent_replay | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.PackInstallRouteTests.Selected_pack_inventory_does_not_read_ambient_tenant | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.PackNavigationRouteTests.Selected_navigation_reads_only_selected_tenant_not_ambient_team | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.Packs.T433AccessReplacementProducerTests.Atomicity_probe_is_reproducible_signed_and_pins_the_actual_late_content_pointer | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests.A_selected_grant_identity_is_bound_by_field_name_without_row_index_inference | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests.Activation_refuses_unregistered_transports_and_unresolved_source_fields | 0 | 4 | +4 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests.Admitted_binary_action_emits_file_input_and_refresh_contract | 0 | 3 | +3 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests.Admitted_record_read_emits_the_same_route_and_gate_the_host_executes | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests.Declarative_target_binding_does_not_weaken_the_grant_instance_fence | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests.Grant_actions_compile_only_host_owned_selected_session_enforcement | 0 | 3 | +3 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.HostViewRequestAdmissionTests.Unknown_file_or_result_semantics_are_refused | 0 | 2 | +2 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Correlation_header_is_an_explicit_safe_host_placement | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Form_payload_root_and_idempotency_header_are_host_owned_and_serialized_explicitly | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Invocation_values_have_a_closed_host_owned_shape | 0 | 3 | +3 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Missing_unknown_duplicate_or_inconsistent_placements_are_refused | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Refuses_pack_overrides_and_duplicate_properties | 0 | 6 | +6 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Refuses_unknown_contract_and_missing_or_extra_inputs | 0 | 5 | +5 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Refuses_unresolvable_or_wrongly_typed_sources | 0 | 7 | +7 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Resolves_transport_and_enforcement_only_from_the_host_descriptor | 0 | 1 | +1 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Unknown_invocation_values_are_refused | 0 | 4 | +4 |
| Harborline.Api.LocalNodeHost.Tests.ViewDefinitions.ViewRequestBindingAdmissionTests.Unsafe_host_header_descriptors_are_refused_before_activation | 0 | 5 | +5 |
