# PR346 reviewed analyzer exceptions

Status: source changes and regression tests prepared; provider execution and exact-head reviews pending. This document does not certify a green gate or an executed provider trial.

The owner approved the policy in [coordinator comment 5973232820](https://github.com/Harborline-Software/harborline-api/pull/346#issuecomment-5973232820). Control owns the authoritative change-delivery policy. The API implementation supplies a fail-closed ingestion contract; it does not change the shared analyzer pin, thresholds, baseline, branch protection, or required checks.

## Evidence contract

An exception names one exact analyzer rule, source path, and reviewed local scope. A tagged disable and matching restore enclose either one local initializer or one catch handler, with a maximum of 32 source lines. The normalized-LF SHA-256 pins the reviewed scope. Unsupported forms require separate reviewed parser support. A file, category, multiple-rule, declaration, or missing-restore exception is not supported.

The source-owned manifest must explain the protected behavior, the behavior change caused by compliance, the rule's protected risk, why detection is inapplicable or misfires here, and remaining controls. It records rejected alternatives as analysis or executed trials. Executed trials additionally identify the command, exact source commit, observed result, outcome, and report reference. Analysis-only rejection is never presented as execution. A justification need not prove that no conceivable compliant implementation exists.

Test and review references identify existing source/evidence anchors. Structural validation does not prove that a test ran or authenticate an owner's approval; execution reports and PR review supply those assurances. Self-authored manifest prose is not an approval substitute. An additional justified exception requires a separate rule applicability/scope review; analyzer weakening is never automatic.

Roslyn's source suppression must be present for the exact diagnostic location. Ingestion assigns `accepted` only after validation. An accepted status without that evidence fails closed. Other findings remain active under the existing quality policy. The compiler report is retained byte for byte as `.sarif.raw`, including findings omitted from normalized input because they have no physical location. Raw reports are not analyzer inputs; both normalized and raw reports participate in the exact-head/run production digest. Changed artifacts invalidate production evidence. Failed normalization also retains raw evidence.

## identity-recovery-sqlite-cursor — CA1309

Protected behavior: bounded recovery paging must use the same SQLite `BINARY` ordering in its cursor predicate and both first/wrapped-page ordering paths. A stalled older row must not indefinitely hide a later owed audit.

The two-argument comparison is part of an EF query translated by the SQLite provider. The source explicitly collates both the predicate and ordering; it does not make a general equivalence claim between SQLite byte ordering and .NET UTF-16 ordinal ordering for all Unicode.

Protected risk and applicability: CA1309 protects against culture-dependent managed string comparisons. This statement must execute in SQL under the declared provider collation. A managed overload is not an interchangeable SQL expression.

Alternatives:

- `StringComparison.Ordinal` overload: a provider regression test is prepared to exercise the actual query translation. **Execution is pending**, so its rejection is not yet an executed trial in this evidence record.
- Materialize candidates and compare in memory: analysis-only rejection. It changes bounded SQL paging into application-side scanning or can discard eligible rows after a database limit.
- Change the cursor identity or add a new ordering field: analysis-only rejection. It changes the durable coordinator identity/schema contract rather than fixing the analyzer's applicability to this expression.

Remaining controls: `SQLite_rejects_the_ordinal_comparison_overload_in_a_recovery_cursor_query` exercises provider translation. `Recovery_pages_past_stalled_tied_homes_and_revisits_them_after_delivering_a_later_audit` exercises tied cursor progress, a throwing stalled home, later audit completion, and revisiting unfinished homes. Provider consistency and any broader ordering change need their own evidence.

## identity-web-home-recovery — CA1031

Protected behavior: one damaged web home must be recorded as a recovery failure without stopping later homes. Durable unfinished state remains available for retry. Requested cancellation propagates the original exception.

Protected risk and applicability: CA1031 protects against accidentally swallowing unexpected failures. Here the catch is an explicit per-home recovery boundary after the requested-cancellation handler. It logs the actual exception and correlation identity, then continues the drain; it does not report a failed home as completed. The exception is scoped only to that handler.

Alternatives:

- Restrict the handler to an assumed set of exception classes: analysis-only rejection. Registered home implementations and their SQLite/audit dependencies can fail in different ways; no existing contract establishes a complete recoverable exception list.
- Propagate every home failure: analysis-only rejection. A poisoned old home would repeatedly prevent later owed audits from being recovered.
- Suppress requested cancellation: rejected by the existing cancellation contract and the prepared direct regression. The earlier cancellation handler remains outside this exception scope.

Remaining controls: the tied-home test asserts two Error log records containing the actual failure and stalled identity, leaves stalled homes Preparing, and proves exactly one later completion audit. `Requested_recovery_cancellation_propagates_the_original_exception_without_recording_failure` asserts original-exception identity, no error record, durable unfinished state, and a later idempotent recovery. These additions still require execution.

## Existing recovery and lease boundaries

The raw archived quality decision at source head `68e92ee0` identifies the cursor comparison and web-home catch as PR-introduced. The coordinator's resume/abort/release catches, membership recovery catch, hosted daemon boundary, and audit outbox catches are baseline; this change does not add exceptions for them.

Their contracts differ and must not be replaced by a single generic swallowing rationale. Resume resolves durable state to Completed/PendingRecovery/Aborted. Lease release preserves the primary result and relies on lease expiry/reacquisition. Abort cleanup records whether cleanup succeeded. Membership recovery isolates poisoned rows. The daemon rethrows requested cancellation and records initial scan failure. Outbox delivery failure stays owed, records attempts, and uses audit identity for idempotent completion. Existing focused tests cover those state transitions; a baseline reconciliation would need individual site review, including direct cancellation and nested mark-failure gaps where applicable.

## Control handoff

Apply the evidence contract above to Control's authoritative change-delivery procedure. Keep `waivers.allowed: false`; a reviewed exact-rule/local source exception is not a blanket baseline waiver. Retain raw findings, fail new unsuppressed findings under current thresholds, reject unsupported or malformed exception evidence, distinguish executed trials from analysis, and initiate a separate rule review when justified exceptions recur. Required exact-head reviews and genuine required checks remain mandatory.
