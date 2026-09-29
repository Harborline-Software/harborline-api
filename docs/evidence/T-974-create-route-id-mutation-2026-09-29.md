# T-974 create-route record-id refusals, scoped mutation evidence

The seam is the 26 create-route guards T-974 added. Each refuses a caller-constructed record id with `400 request.record-id-not-accepted` before the server mints one. The guards are at API `5c3b92a2`, the head of `codex/t974-gaps` (PR #254), which stacks on PR #249. That code is not on this branch's base. The run was made in a detached worktree at `5c3b92a2`, with this branch's `eng/mutation-report.mjs` copied in and no other change.

The guards were already in a form Stryker can mutate: `if (body.Id is not null) return ...;` and its `||` variants, with no pattern variable. So no source rewrite was needed. The spans are the character spans of each `if` line plus its `return`, generated from `git diff -U0 887087b8 5c3b92a2 -- 'apps/local-node-host/Health/*.cs'`.

The run used repository-pinned Stryker.NET 5.0.0 through `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/Health/<file>.cs{<start>..<end>}' ...` (26 `--scoped` spans) with `--filter 'FullyQualifiedName~refuses_a_client_supplied_record_id'`. That filter selects 43 tests. The run used source project `Harborline.LocalNodeHost.csproj`, per-test coverage and the pinned SDK's `MSBuild.exe`, and took 643 s. In the spans, 99 mutants were Killed, with 0 Survived, 0 NoCoverage and 0 Timeout. Stryker's console summary also printed one Timeout, but no mutant in the JSON report has that status. Project-wide, 5,739 CompileError rollbacks lie outside the spans. No CompileError lies on a guard line; the per-file CompileError counts in the console output are rollbacks elsewhere in those files.

## Named kills: the bypass mutant per guard

| Guard | Bypass mutant (Killed) | Killing test |
| --- | --- | --- |
| AccountingPeriodRoutes.cs:78 | Equality `body?.Id is null` | `AccountingPeriodRouteTests.Open_refuses_a_client_supplied_record_id` |
| BankAccountRoutes.cs:178 | Equality `body?.Id is null` | `BankAccountCreateGateTests.Create_refuses_a_client_supplied_record_id` |
| BillRoutes.cs:191 | Equality `body.Id is null` | `BillRouteTests.Create_refuses_a_client_supplied_record_id` |
| CalendarCollectionRoutes.cs:87 | Equality `body?.Id is null` | `CalendarCollectionRouteTests.Create_refuses_a_client_supplied_record_id` |
| ChartOfAccountsManagementRoutes.cs:174 | Equality `body.Id is null` | `ChartOfAccountsManagementRouteTests.Create_refuses_a_client_supplied_record_id` |
| ChartOfAccountsRoutes.cs:89 | Logical `\|\|` to `&&` | `ChartOfAccountsRouteTests.Seed_refuses_a_client_supplied_record_id` |
| CommsRoutes.cs:312 | Logical `\|\|` to `&&` | `CommsRouteTests.Post_refuses_a_client_supplied_record_id` |
| CommsRoutes.cs:390 | Logical `\|\|` to `&&` | `CommsDmRouteScopeTests.Dm_append_refuses_a_client_supplied_record_id` |
| ConsentRecordRoutes.cs:97 | Equality `body?.Id is null` | `ConsentRecordRouteAndSweepTests.Create_refuses_a_client_supplied_record_id` |
| ContactRoutes.cs:222 | Logical `\|\|` to `&&` | `ContactDeleteRouteTests.Create_refuses_a_client_supplied_record_id` |
| DocumentRoutes.cs:174 | Logical `\|\|` to `&&` | `DocumentRouteTests.Upload_refuses_a_client_supplied_record_id` |
| DocumentTemplateRoutes.cs:176 | Logical `\|\|` to `&&` | `NodeDocumentTemplateRouteActingMemberPlacerTests.Issue_refuses_a_client_supplied_record_id` |
| FormsRoutes.cs:170 | Negate `!(body.TryGetProperty("instanceId", ...))` | `FormsRouteTests.Submit_refuses_a_client_supplied_record_id` |
| InvoiceRoutes.cs:233 | Equality `body.Id is null` | `InvoiceRouteTests.Create_refuses_a_client_supplied_record_id` |
| JournalEntryRoutes.cs:239 | Equality `body.Id is null` | `JournalEntryRouteTests.Create_refuses_a_client_supplied_record_id` |
| JournalEntryRoutes.cs:383 | Equality `body?.Id is null` | `JournalEntryRouteTests.Reverse_refuses_a_client_supplied_record_id` |
| LeaseRoutes.cs:99 | Logical `\|\|` to `&&` | `LeaseRouteTests.Create_refuses_a_client_supplied_record_id` |
| MaintenanceRoutes.cs:117 | Logical `\|\|` to `&&` | `MaintenanceRouteTests.Create_refuses_a_client_supplied_record_id` |
| PayrollRoutes.cs:109 | Logical `\|\|` to `&&` | `PayrollRouteTests.CreateEmployee_refuses_a_client_supplied_record_id` |
| PayrollRoutes.cs:176 | Logical `\|\|` to `&&` | `PayrollRouteTests.CreatePayRun_refuses_a_client_supplied_record_id` |
| PropertyRoutes.cs:107 | Logical `\|\|` to `&&` | `PropertyRouteTests.Create_refuses_a_client_supplied_record_id` |
| RecurringInvoiceRoutes.cs:128 | Logical `\|\|` to `&&` | `RecurringInvoiceRouteTests.Create_refuses_a_client_supplied_record_id` |
| RecurringInvoiceRoutes.cs:210 | Logical `\|\|` to `&&` | `RecurringInvoiceRouteTests.Generate_refuses_a_client_supplied_record_id` |
| SchedulingDefinitionRoutes.cs:210 | Logical `\|\|` to `&&` | `SchedulingDefinitionRouteTests.Appointment_booking_refuses_a_client_supplied_record_id` |
| SchedulingDefinitionRoutes.cs:267 | Logical `\|\|` to `&&` | `SchedulingDefinitionRouteTests.Event_create_refuses_a_client_supplied_record_id` |
| WebSession/SelectedFormSubmitRoutes.cs:58 | Negate `!(body.TryGetProperty("instanceId", ...))` | `FormsRouteTests.Selected_submit_refuses_a_client_supplied_record_id` |

For the two-key guards, the `||` to `&&` mutant is the bypass of either key alone. Every mutant Stryker placed in a span was Killed: 5 in each two-key guard and 2 in each single-key guard (3 in each `TryGetProperty` guard).

Line numbers are at `5c3b92a2`. The guard-form rewrite in this branch touches none of these lines, but it shifts later line numbers in files it also rewrote (ContactRoutes, InvoiceRoutes, JournalEntryRoutes, SchedulingDefinitionRoutes, ConsentRecordRoutes and BankAccountRoutes). Match by file, text and mutator when re-reading.

## Raw report

The raw report (about 13 MB) is not kept in the tracked tree, for the size and scanner reasons in AGENTS.md "Mutation testing". Its SHA-256 is `91B37BFE1DE35E8539BF3FB4599E853C77694948F4B3FAB79EB33DCF74AB2689`; archive it on an `archive/*` branch before citing it. This certifies the 26 record-id refusal guards only, not the classification test or the route-wide DesignException rows.
