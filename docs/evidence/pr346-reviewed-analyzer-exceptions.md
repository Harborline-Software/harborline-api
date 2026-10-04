# PR346 reviewed analyzer exceptions

Status: all three source-local exceptions are reviewed in the manifest. The restored recovery/provider/caller trial passed 30/30 with no skips. The Windows host gate passed at `fd2730d356d4569a72da2ad37eecc845e804aff7`; its receipt applies to that exact head, not subsequent documentation commits. Fresh required exact-head PR reviews and hosted checks remain the coordinator’s responsibility.

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

The historical raw archived quality decision at source head `68e92ee0` identifies the cursor comparison and web-home catch as PR-introduced. Fresh analysis at `458b5083` additionally identifies the recovery lease-release handler in `WebTenantSelectionAuthority.cs` as introduced; that handler is now covered by the reviewed `identity-selection-recovery-lease-release` manifest entry, backed by the executed trials and independent source review below. The coordinator's resume/abort/release catches, membership recovery catch, hosted daemon boundary, and audit outbox catches are baseline; this change does not add exceptions for them.

Their contracts differ and must not be replaced by a single generic swallowing rationale. Resume resolves durable state to Completed/PendingRecovery/Aborted. Lease release preserves the primary result and relies on lease expiry/reacquisition. Abort cleanup records whether cleanup succeeded. Membership recovery isolates poisoned rows. The daemon rethrows requested cancellation and records initial scan failure. Outbox delivery failure stays owed, records attempts, and uses audit identity for idempotent completion. Existing focused tests cover those state transitions; a baseline reconciliation would need individual site review, including direct cancellation and nested mark-failure gaps where applicable.

## Control handoff

Apply the evidence contract above to Control's authoritative change-delivery procedure. Keep `waivers.allowed: false`; a reviewed exact-rule/local source exception is not a blanket baseline waiver. Retain raw findings, fail new unsuppressed findings under current thresholds, reject unsupported or malformed exception evidence, distinguish executed trials from analysis, and initiate a separate rule review when justified exceptions recur. Required exact-head reviews and genuine required checks remain mandatory.

The repeated intentional recovery/lease boundaries are a concrete CA1031 applicability pattern for a separate rule-scope review. Track that review independently before adding further exceptions; this PR does not authorize an analyzer-wide change or blanket suppression of those baseline sites.

## Preparation checkpoint: identity-selection-recovery-lease-release

This historical checkpoint is superseded by the executed trial and final review sections below.

Fresh landing comparison at 458b5083 reports one new unsuppressed CA1031 at WebTenantSelectionAuthority.cs:291. This is the recovery-only finally cleanup handler; the live-selection handler is a separate baseline site. At that historical checkpoint, no reviewed suppression had yet been added; the reviewed entry and executed evidence below supersede that status.

Protected behavior: release is attempted with CancellationToken.None after lease acquisition, and a cleanup error must not replace the original recovery exception, requested cancellation, or durable completion. Risk: swallowing release failure can hide provider defects or leave a remote conflict until expiry. The 30-second lease is finite; every subsequent recovery invokes AcquireAsync before reloading/writing the home. FleaseLeaseCoordinator removes local ownership before broadcasting release, checks expiry in Holds and acquisition/conflict handling, and treats release broadcasts as best effort. This source analysis is not an executed lease-expiry proof.

Alternatives, analysis only: propagating every release failure can mask the original exception/cancellation and report completed work as failed; a finite exception-type catch lacks a complete failure contract for registered ILeaseCoordinator implementations. Replacing the lease protocol, introducing an owner-wide logging contract, or wrapping every implementation is a separate architecture decision, not an executed compliant trial. CA1031's defect-hiding concern applies, but this finite cleanup boundary has a different purpose from per-home failure isolation. Repetition requires separate rule applicability review and does not justify automatic acceptance or analyzer weakening.

Prepared test Recovery_release_failure_preserves_the_primary_exception_and_durable_retry covers both exact primary exception identities, requested cancellation, exactly one additional uncancelled release attempt, durable unfinished state, another AcquireAsync call, and audit completion once across retries. The separate prepared fact Recovery_release_failure_does_not_undo_completion_or_duplicate_its_audit pins successful durable completion despite cleanup failure and no duplicate on restart. It has NOT been built or run. Its fake lease coordinator injects release failure and records duration/acquisition; it does NOT prove production expiry enforcement. A controlled replacement of only the recovery catch with rethrow should fail the primary-exception oracle; this causal trial is NOT executed. Independent source review is pending.

## Windows gate failure at 458b5083

Boundary verification passed. Full Windows host gate: 5,107 tests, 5,085 passed, one failed, 21 skipped. WrongBearer_Rejected_FailClosed failed at HttpClient.SendAsync with SocketException 10048 targeting 127.0.0.1:65533, before the HTTP status assertion. The fixture uses an OS-assigned listener port (127.0.0.1:0), obtains the bound address after StartAsync, and disposes HttpClient, stops/disposes the server, and disposes the signer. No fixed-port reservation race or lifecycle omission has been established. The failure does not establish ephemeral-port exhaustion or a transient cause. No retry, timeout, authentication assertion, or gate baseline was relaxed; no successful exact-head receipt was produced.

The subsequent Node-only quality invocation is diagnostic, not a production-run attestation. Landing comparison failed the third CA1031 above. The quality decision also reports unmapped dist JavaScript coverage inputs and untrusted coverage; this has not been waived or represented as a passing quality gate.

## Executed lease-release rethrow control

Prepared test source content is recorded in local commit 489ac7b7de8fbfc5b731a1c57119d56011762687. Actual initial trial: 3 passed,0failed,0skipped; TRX SHA-256 62bf72a7e91b03f648537f8dc703f097660329dae3e042850dc770ce0e3f505d. Controlled source added only throw in the recovery cleanup catch: 3failed,0passed,0skipped. Both identity assertions observed release IOException instead of original primary exception/cancellation; successful completion case also threw. TRX SHA-256 9620edda93f03d39769c4131f1bf886199b33b0a8823484a9417caeed4cd01d5. Source original bytes restored in finally. This is a causal behavior trial, not Stryker or a successful fullgate receipt. Other alternatives remain analysis only. The final tagged-source restored-green result is recorded below.

## Independent lease-release source review

/root/review333 read-only reviewed both primary/cancellation cases and success-only fact. Reviewer identified that a None token alone could not prove cleanup ran; fixed with exact prior+1 ReleaseCalls assertions. Reviewer accepted site-specific cleanup rationale and required explicit fake expiry/exclusivity limitation. Final source peer review accepted the exact recovery-only bare catch, finite parser support and rejection tests, individual manifest rationale, and controlled-trial evidence. Reviewer found no concrete defect and confirmed corrected release-attempt oracles and fake limitations. The reviewer did not execute trials or grant required GitHub approval. Final tagged green/fullgate remained pending at review.

## Final tagged recovery/provider/caller trial

Actual restored and tagged source: 30 passed, 0 failed, 0 skipped across WebTenantSelectionAuthorityTests, WebSelectedSessionLogoutAuthorityTests and unchanged NodeCallerSessionTokenTests. TRX SHA-256 8f2ebca350f8b366c2f99b4611cd76d9b57bde4c5faa6705687bf4ee6e0cc2b1. This includes the 3 lease-release cases and actual SQLite provider rejection/paging and cancellation regressions. Initial compiler attempt stopped before tests with CS0016 because the explicit SARIF directory did not exist; directory created and build/trial completed. This setup error is not a causal behavior failure. Bounded read-only socket watcher observed the actual testhost and retained timestamped snapshots; no Socket 10048 occurred. Passing this targeted trial does not establish the historical transport failure root cause. The subsequent Windows host gate passed at `fd2730d356d4569a72da2ad37eecc845e804aff7`: 5,089 passed, zero failed, and 21 skipped; quality-baseline comparison reported zero new findings. Its quality decision was NEUTRAL under the unchanged policy, with two unmapped coverage inputs and untrusted-coverage would-block reasons retained. That pass does not establish the earlier Socket10048 cause, waive coverage limitations, or certify a later documentation head. Control owns the authoritative shared policy; this API evidence does not claim that its procedure update is live on main.

## Focused-mode inventory classification repair

Hosted merge tree `de1349e3d11e730fc9523c8f370ae6659e350b0b` combines main `c6e5717c4c7d1a67e84707980c465347e7454ee4` with PR head `bc84ef1bb652ccc91217effe568971a1f873b1ba`. Its preflight refused before tests on Linux, macOS, and Windows. Reproducing that tree's actual changed-path classification identified one unknown path: `apps/local-node-host/tests/ArchTests/durable-write-classification.tsv`. No focused-selector configuration file was missing; the existing selector is the bounded `compiled-write-fences-v1` suite.

The inventory is the exact input read by `DurableWriteClassificationArchTests.ReadClassification`. The repair classifies only that owned path as a build/test input. It preserves the existing suite, required test identities, and mandatory independently executed coverage OFF and ON modes. Another TSV inventory, the same filename in Identity tests, and a TSV under docs remain unsupported; there is no general extension or directory allowance. The early suite is a bounded compiled-write-fence proof, not a claim to verify all runtime behavior or the inventory's contents. The full host gate still owns those architecture and behavior tests.

A literal regression failed against the unchanged classifier with actual selection `unsupported` instead of required `focused`. After the exact-path mapping, all 74 Node classifier/evidence tests passed with zero skips; the actual 23-path PR delta selects both modes and has no unknown paths. These light checks do not execute C# coverage modes or substitute for the required exact-head full gate. The branch has normally merged current main so its local preflight now carries the mandatory focused hook; no history was rewritten. Complete exact-head review and the coordinated full gate remain required before the next push.


## Bounded recovery cycle review

Fresh review `4175796730` identified starvation while later eligible homes keep arriving: the prior cursor wrapped only on an empty tail. Each membership and web cycle now captures an independent upper `(CreatedAtUtc, CorrelationId)` tuple and pages only through that bound. A visited final tuple or an empty page clears the cycle; this includes rows completing elsewhere between the bound and first-page queries. The next sweep revisits older unfinished homes while admitting later arrivals into a new cycle. Page limits, durable states, leases, transaction boundaries, requested-cancellation propagation and failure logging remain unchanged.

The CA1309 exception remains one finite initializer. Both comparisons execute in SQLite under explicit `BINARY` collation, matching the page and maximum ordering. The exact source digest changed because the initializer now includes the upper bound. Independent source review and actual provider execution below refresh the evidence for this candidate.

Prepared regressions use literal durable-state/event/count oracles: real logout revocation with an owed audit under continuing full-page arrivals (limits one/two and tied/later timestamps); real membership finalization interrupted by response loss or unavailable lease under continuing full-page arrivals. The membership oracle requires a valid `TenantMembershipCoordinationCompleted` envelope, cleared account fence, usable membership and no duplicate audit. The corrected old-source causal trial executed all eight sustained-arrival cases: zero passed, eight failed, zero skipped. Every logout case failed at owed-home retry count one instead of two; every membership case failed at durable `Committing` instead of `Completed`. Its TRX SHA-256 is `b99f75bcc8915d8e7c99cc7f2b086ba81e730809b568cd0fe40b9bbd02121820`. The repaired working candidate passed all nine new cases with zero skips, including actual SQLite first-page nullable-cursor translation and the captured-first-page completion race. Its TRX SHA-256 is `84c34dca12797a80ae3606b5ac4df00ec0bd879b962668ae58634fcd422f739a`.

A separate one-line causal control retained the cycle bound on an empty page. The captured-first-page regression then failed because the next sweep visited no later row; the test had already observed the independent real completion and exactly one audit. Exact repaired source bytes were restored in `finally` after each causal trial. These are controlled regression faults, not Stryker mutation scores. Trials used `dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-restore -nodeReuse:false -maxcpucount:2`, exact named filters and TRX logging; the first old-source build also restored dependencies.

Initial logout fixture trials used an incorrect arrival timestamp assumption: this fixture's logout clock is 15:01, not 15:00. Their raw results remain retained as superseded trial evidence. The corrected tests pin literal 15:01, put tied identities later under BINARY ordering, and seed `limit - 1` initial rows so both page sizes are full. A subsequent evidence-directory setup attempt failed before source edits or test execution; the directory was created before the successful corrected trial. Neither superseded fixture failures nor setup failure is claimed as causal starvation proof.

The repaired working-candidate compiler SARIF is retained byte-for-byte with SHA-256 `4b91dbad51d872dad1d35ee656dc0c4fafbec8325a893b0d60b748911a730e6b`. It contains both CA1309 findings at the lower and upper SQL comparisons, each with actual `inSource` pragma evidence. The manifest retains this one finite initializer's exact digest `b31e81ff0741a8e959bae0a3e41d89c0d2abb7b400b3a8c9c0d43baa78449a01`; it does not discard raw warnings or exempt another source site.

Independent source review: `/root/review333` recomputed the scope digest and accepted separate lane bounds, explicit BINARY comparisons/order, cancellation propagation, failure isolation and literal test oracles. Review identified a stale-bound empty-first-page race and a partial initial logout page; both were corrected and the causal/provider trials above validate those corrections. Root separately reviewed the reviewer's membership and race fixtures. This is local source review and actual executed regression evidence, not required GitHub approval. No rule policy, baseline, threshold, transaction, lease or protected-queue guarantee was changed. The working candidate was based on `55cc8e6a8756bd77cc0c7e197d0cde09c85718d9`; a subsequent exact-head all-lane gate is required before push.

Restored affected suites: 57 passed, zero failed, zero skipped across InstallationIdentityCoordinatorServiceTests, WebSelectedSessionLogoutAuthorityTests and WebTenantSelectionAuthorityTests. This includes the existing actual provider rejection, tied-home page/order SQL assertions, requested-cancellation identity, failed-row logging, lease-release primary-exception and exactly-once recovery controls. Restored-suite TRX SHA-256: `2a7d6d528fab7a786538937431081c2e5e521279533ad0a0a9587e045c85ee74`. Empty-cycle causal-control TRX SHA-256: `36f6bc8f1f6283eef6ef70298571b52935130e94f33edf09170c019319079c19`. Raw reports and compiler SARIF remain in the external task evidence archive.
