# PR346 reviewed analyzer exceptions

Status: committed provider/recovery trial passed 6/6 with no skips; two causal recovery controls failed as expected and restored source passed 6/6. Final Windows host gate and required exact-head PR reviews remain pending.

The owner approved the policy in [coordinator comment 5973232820](https://github.com/Harborline-Software/harborline-api/pull/346#issuecomment-5973232820). Control owns the authoritative change-delivery policy. The API implementation supplies a fail-closed ingestion contract; it does not change the shared analyzer pin, thresholds, baseline, branch protection, or required checks.

## Evidence contract

An exception names one exact analyzer rule, source path, and reviewed local scope. A tagged disable and matching restore enclose either one local initializer or one catch handler, with a maximum of 32 source lines. The normalized-LF SHA-256 pins the reviewed scope. Unsupported forms require separate reviewed parser support. A file, category, multiple-rule, declaration, or missing-restore exception is not supported.

The source-owned manifest must explain the protected behavior, the behavior change caused by compliance, the rule's protected risk, why detection is inapplicable or misfires here, and remaining controls. It records rejected alternatives as analysis or executed trials. Executed trials additionally identify the command, exact source commit, observed result, outcome, and report reference. Analysis-only rejection is never presented as execution. A justification need not prove that no conceivable compliant implementation exists.

Test and review references identify existing source/evidence anchors. Structural validation does not prove that a test ran or authenticate an owner's approval; execution reports and PR review supply those assurances. Self-authored manifest prose is not an approval substitute. An additional justified exception requires a separate rule applicability/scope review; analyzer weakening is never automatic.

Roslyn's source suppression must be present for the exact diagnostic location. Ingestion assigns `accepted` only after validation. An accepted status without that evidence fails closed. Other findings remain active under the existing quality policy. The compiler report is retained byte for byte as `.sarif.raw`, including findings omitted from normalized input because they have no physical location. Raw reports are not analyzer inputs; both normalized and raw reports participate in the exact-head/run production digest. Changed artifacts invalidate production evidence. Failed normalization also retains raw evidence.

CQG retains accepted findings in both its decision and baseline candidate. API candidate records carry the reviewed exception identity and exact scope digest from the validated source manifest. The added-finding comparison independently checks that proof against the current manifest, exact rule/path/line, and accepted analyzer status. An exception cannot consume another active finding's fallback baseline identity. A previously accepted exception in a future baseline cannot cover another active finding or reactivation of the old warning. No committed baseline is changed, and an unverified suppressed flag, malformed proof, or newly added unsuppressed finding still fails.

## identity-recovery-sqlite-cursor — CA1309

Protected behavior: bounded recovery paging must use the same SQLite `BINARY` ordering in its cursor predicate and both first/wrapped-page ordering paths. A stalled older row must not indefinitely hide a later owed audit.

The two-argument comparison is part of an EF query translated by the SQLite provider. The source explicitly collates both the predicate and ordering; it does not make a general equivalence claim between SQLite byte ordering and .NET UTF-16 ordinal ordering for all Unicode.

Protected risk and applicability: CA1309 protects against culture-dependent managed string comparisons. This statement must execute in SQL under the declared provider collation. A managed overload is not an interchangeable SQL expression.

Alternatives:

- `StringComparison.Ordinal` overload: executed against the actual SQLite provider in the committed trial below. Translation raises `InvalidOperationException`; the test asserts both `could not be translated` and `Ordinal` in its message. This is an observed provider rejection, not a claim that every possible compliant rewrite is impossible.
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

Remaining controls: the tied-home test asserts two Error log records containing the actual failure and stalled identity, leaves stalled homes Preparing, and proves exactly one later completion audit. `Requested_recovery_cancellation_propagates_the_original_exception_without_recording_failure` asserts original-exception identity, no error record, durable unfinished state, and a later idempotent recovery. Both passed in the committed trial and after restoring the causal controls.

## Executed provider trial ee35604e

Source commit: `ee35604e7a804477994f7bf3bea24c894ca758ed`. The source tree was committed before this trial; the manifest added later does not falsely identify itself as the tested source commit.

Command (evidence directory is external to the tracked repository):

```text
dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-restore -nodeReuse:false -maxcpucount:2 --filter FullyQualifiedName~WebSelectedSessionLogoutAuthorityTests --logger trx;LogFileName=committed-provider-recovery-green.trx --results-directory <evidence>/committed-provider-recovery-green -p:HARBORLINE_GATE_QUALITY=1 -p:HarborlineRoslynSarifDirectory=<evidence>/roslyn
```

Observed result: **6 passed, 0 failed, 0 skipped**. The ordinal-overload test passed by observing the provider's expected translation rejection. The actual recovery SQL uses `COLLATE "BINARY"` in bounded page ordering and the tied-time cursor predicate; the test pins this declared collation, not a general Unicode equivalence. The initial SQL oracle omitted identifier quotes and failed; the diagnostic established the provider spelling, and the corrected literal still fails when collation is removed.

Retained report: `committed-provider-recovery-green.trx`; SHA-256 `190dc73c333ecb9bf10831b3ddfdd205a0579a02a0bc428af33beaaa177e83ea`.

Recovery causal controls: remove explicit `BINARY` ordering and remove only the web recovery requested-cancellation handler. The paging test failed for absent `COLLATE "BINARY"`; the cancellation test failed because no original exception propagated. **2 failed, 0 passed** in `recovery-contracts-red.trx`, SHA-256 `a5ddc90b4199a0be7116a1865eec83bdd0c1c3baec2e571d99c04071f53f4109`. The two tests exercise separate controls: the paging test does not request cancellation, and the cancellation test does not assert SQL ordering. Exact source bytes were restored, rebuilt, and all **6 passed** again.

The raw compiler report includes both local findings with `kind: inSource`, `suppressionType: Pragma Directive`, and no accepted status. Baseline catches remain unsuppressed. This verifies the actual representation consumed by the normalizer.

Actual-report normalization retains **532 of 532 anchored findings**, accepts exactly these two manifest sites, preserves original compiler bytes, and leaves coordinator CA1031 catches at lines 550, 610, 913, and 1404 active. Raw report SHA-256: `d984a81c717f5fde255c90562251b40d3493e017c342c67917651f9702d9928e`.

## Independent source review ee35604e

`/root/review333` accepted both finite site rationales at `ee35604e7a804477994f7bf3bea24c894ca758ed`: the SQL initializer preserves provider paging semantics, and the single web-home handler preserves cancellation, logs failure, allows later progress, and keeps durable retry state. The reviewer confirmed the analysis-only alternatives were labelled accurately and found no concrete source/policy issue. The reviewer did not independently execute the trials. This source review does not replace required GitHub PR approval or exact-head CI.

## Existing recovery and lease boundaries

The historical raw archived quality decision at source head `68e92ee0` identifies the cursor comparison and web-home catch as PR-introduced. Fresh analysis at `458b5083` additionally identifies the recovery lease-release handler in `WebTenantSelectionAuthority.cs` as introduced; that handler remains unsuppressed pending individual validation/review. The coordinator's resume/abort/release catches, membership recovery catch, hosted daemon boundary, and audit outbox catches are baseline; this change does not add exceptions for them.

Their contracts differ and must not be replaced by a single generic swallowing rationale. Resume resolves durable state to Completed/PendingRecovery/Aborted. Lease release preserves the primary result and relies on lease expiry/reacquisition. Abort cleanup records whether cleanup succeeded. Membership recovery isolates poisoned rows. The daemon rethrows requested cancellation and records initial scan failure. Outbox delivery failure stays owed, records attempts, and uses audit identity for idempotent completion. Existing focused tests cover those state transitions; a baseline reconciliation would need individual site review, including direct cancellation and nested mark-failure gaps where applicable.

## Control handoff

Apply the evidence contract above to Control's authoritative change-delivery procedure. Keep `waivers.allowed: false`; a reviewed exact-rule/local source exception is not a blanket baseline waiver. Retain raw findings, fail new unsuppressed findings under current thresholds, reject unsupported or malformed exception evidence, distinguish executed trials from analysis, and initiate a separate rule review when justified exceptions recur. Required exact-head reviews and genuine required checks remain mandatory.

The repeated intentional recovery/lease boundaries are a concrete CA1031 applicability pattern for a separate rule-scope review. Track that review independently before adding further exceptions; this PR does not authorize an analyzer-wide change or blanket suppression of those baseline sites.

## Preparation checkpoint: identity-selection-recovery-lease-release

This historical checkpoint is superseded by the executed trial and final review sections below.

Fresh landing comparison at 458b5083 reports one new unsuppressed CA1031 at WebTenantSelectionAuthority.cs:291. This is the recovery-only finally cleanup handler; the live-selection handler is a separate baseline site. No reviewed suppression has been added here.

Protected behavior: release is attempted with CancellationToken.None after lease acquisition, and a cleanup error must not replace the original recovery exception, requested cancellation, or durable completion. Risk: swallowing release failure can hide provider defects or leave a remote conflict until expiry. The 30-second lease is finite; every subsequent recovery invokes AcquireAsync before reloading/writing the home. FleaseLeaseCoordinator removes local ownership before broadcasting release, checks expiry in Holds and acquisition/conflict handling, and treats release broadcasts as best effort. This source analysis is not an executed lease-expiry proof.

Alternatives, analysis only: propagating every release failure can mask the original exception/cancellation and report completed work as failed; a finite exception-type catch lacks a complete failure contract for registered ILeaseCoordinator implementations. Replacing the lease protocol, introducing an owner-wide logging contract, or wrapping every implementation is a separate architecture decision, not an executed compliant trial. CA1031's defect-hiding concern applies, but this finite cleanup boundary has a different purpose from per-home failure isolation. Repetition requires separate rule applicability review and does not justify automatic acceptance or analyzer weakening.

Prepared test Recovery_release_failure_preserves_the_primary_exception_and_durable_retry covers both exact primary exception identities, requested cancellation, exactly one additional uncancelled release attempt, durable unfinished state, another AcquireAsync call, and audit completion once across retries. The separate prepared fact Recovery_release_failure_does_not_undo_completion_or_duplicate_its_audit pins successful durable completion despite cleanup failure and no duplicate on restart. It has NOT been built or run. Its fake lease coordinator injects release failure and records duration/acquisition; it does NOT prove production expiry enforcement. A controlled replacement of only the recovery catch with rethrow should fail the primary-exception oracle; this causal trial is NOT executed. Independent source review is pending.

## Windows gate failure at 458b5083

Boundary verification passed. Full Windows host gate: 5,107 tests, 5,085 passed, one failed, 21 skipped. WrongBearer_Rejected_FailClosed failed at HttpClient.SendAsync with SocketException 10048 targeting 127.0.0.1:65533, before the HTTP status assertion. The fixture uses an OS-assigned listener port (127.0.0.1:0), obtains the bound address after StartAsync, and disposes HttpClient, stops/disposes the server, and disposes the signer. No fixed-port reservation race or lifecycle omission has been established. The failure does not establish ephemeral-port exhaustion or a transient cause. No retry, timeout, authentication assertion, or gate baseline was relaxed; no successful exact-head receipt was produced.

The subsequent Node-only quality invocation is diagnostic, not a production-run attestation. Landing comparison failed the third CA1031 above. The quality decision also reports unmapped dist JavaScript coverage inputs and untrusted coverage; this has not been waived or represented as a passing quality gate.

## Executed lease-release rethrow control

Prepared test source content is recorded in local commit 489ac7b7de8fbfc5b731a1c57119d56011762687. Actual initial trial: 3 passed,0failed,0skipped; TRX SHA-256 62bf72a7e91b03f648537f8dc703f097660329dae3e042850dc770ce0e3f505d. Controlled source added only throw in the recovery cleanup catch: 3failed,0passed,0skipped. Both identity assertions observed release IOException instead of original primary exception/cancellation; successful completion case also threw. TRX SHA-256 9620edda93f03d39769c4131f1bf886199b33b0a8823484a9417caeed4cd01d5. Source original bytes restored in finally. This is a causal behavior trial, not Stryker or a successful fullgate receipt. Other alternatives remain analysis only. Final taggedscope restored-green trial pending.

## Independent lease-release source review

/root/review333 read-only reviewed both primary/cancellation cases and success-only fact. Reviewer identified that a None token alone could not prove cleanup ran; fixed with exact prior+1 ReleaseCalls assertions. Reviewer accepted site-specific cleanup rationale and required explicit fake expiry/exclusivity limitation. Final source peer review accepted the exact recovery-only bare catch, finite parser support and rejection tests, individual manifest rationale, and controlled-trial evidence. Reviewer found no concrete defect and confirmed corrected release-attempt oracles and fake limitations. The reviewer did not execute trials or grant required GitHub approval. Final tagged green/fullgate remained pending at review.

## Final tagged recovery/provider/caller trial

Actual restored and tagged source:30passed,0failed,0skipped across WebTenantSelectionAuthorityTests, WebSelectedSessionLogoutAuthorityTests and unchanged NodeCallerSessionTokenTests. TRX SHA-256 8f2ebca350f8b366c2f99b4611cd76d9b57bde4c5faa6705687bf4ee6e0cc2b1. This includes the3lease-release cases and actual SQLite provider rejection/paging and cancellation regressions. Initial compiler attempt stoppedbeforetests with CS0016 because the explicit SARIF directory did not exist; directorycreated and build/trial completed. This setup error is not a causal behavior failure. Bounded read-only socket watcher observed the actual testhost and retained timestamped snapshots; no Socket10048 occurred. Passing this targeted trial does not establish the historical transport failure rootcause. Full exact-head Windows gate pending.
