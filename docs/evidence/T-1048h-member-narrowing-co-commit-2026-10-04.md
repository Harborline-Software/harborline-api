# T-1048h: member narrowing audit survives real process loss

API #352 joins the signed `CapabilityRevoked` and `CapabilityDelegated` records to the existing admission-grant narrowing fence. `NarrowAdmissionGrantAsync` stages both records on the same context that revokes the wider grant, advances the subject epoch, and confers the successor. The conferral's one save commits all three audit records with the change. Existing post-commit appends deliver those records; a fresh startup outbox drain delivers any records process loss leaves owed. Main's captured-authority staging, persisted ceremony predecessors and #351 autonomous erasure recovery remain intact.

`AdminTeamAccessAuthorityTests.KilledMemberNarrowingProcess_StartupDrainDeliversAllThreeCommittedAuditsExactlyOnce` starts a real child process over three SQLite files using the existing authority fixture. The child pauses in direct `AppendAuthorizedAsync`, after the narrowing commit and before audit delivery. While it is still alive, the parent independently opens the grant file and asserts the revoked original, one active successor, exactly one subject-epoch advance, and three distinct pending records with literal event values. The parent then kills the entire owned child process tree and awaits exit. A fresh context, durable trail, outbox and startup drain daemon open the same grant file without retrying the narrowing request. Two fresh startups yield the same three audit IDs, with no duplicate records and no pending entry.

The delivery-fault test selects the two narrowing legs using the literal `AuthorizationAdmissionGrantConferred` value, rather than deriving its oracle from the production event constant. The commit-refusal test additionally asserts the subject epoch, including absence of an epoch row, is unchanged when SQLite refuses the successor insert. Approval actor, occurrence instant, original target, successor ID, reason and the shared correlation remain pinned. No retry contract, gate, failure allowance, skip or flaky-retry policy is relaxed.

## Causal and mutation evidence

The controlled regression removes only the same-commit stage block, leaving the existing post-commit delivery calls in place. The delivery-fault and real process-loss checks fail: only `AuthorizationAdmissionGrantConferred` remains durable instead of the literal three-record set. The commit-refusal check still passes. Restoring the source yields three passed tests, zero failures and zero skips. The broader authority/narrowing integration run passed 59 tests with zero failures or skips. Raw TRX files, source hashes and the failed early child-startup trial are retained in the execution evidence. The child manifest signal uses synchronous I/O because it runs inside a blocking module initializer; the 120-second startup deadline is retained.

Native repository-pinned Stryker.NET 5.0.0 ran with the pinned SDK's `MSBuild.exe`, the exact host test project, `--full --scoped '**/Data/Authorization/NodeEfAuthorizationConfigurationStore.cs'`, and the delivery-fault/commit-refusal method filter. The explicit optional-stage block preserves the former null-conditional behavior and makes the staging boundary independently mutable.

The raw report identifies these causal controls:

| Mutation | Result | Killing test |
|---|---|---|
| 2301, line 204: replace `stage.Invoke(db)` with `;`, leaving only after-commit delivery | Killed | `Crash_after_the_member_narrowing_commit_delivers_both_legs_and_the_conferral_once_on_restart` |
| 2299, line 202: change `stage is not null` to `stage is null` | Killed | Same test |

The report test ID is `93b381d9-3c19-2cde-7483-9c5fe5314d10`. Mutant IDs are local to this report. Block removal 2300 is Ignored by Stryker's redundant-block filter; no kill is claimed for it. Across the selected file, 32 mutants are Killed, 66 Survived, 225 have NoCoverage, 39 are CompileError and 38 are Ignored. This two-test run proves the required co-commit control; it is not a certificate for every behavior of the configuration store. The real process-loss test is verified separately from Stryker's in-process delivery-fault test.

The [raw report](https://github.com/Harborline-Software/harborline-api/blob/5627ed3abce2facddf94de600e58e3f7db9cb24d/mutation-report.json) is archived byte-for-byte on `archive/pr352-stryker-7b8fc2d8af07`, outside the tracked source tree because it embeds host test sources. SHA-256: `7b8fc2d8af07cddb8cf85bb1bdab5dd762b5ad4e38255a17369f12d57e8b9a45`.

## Separate follow-up

Permission narrowing retains its pre-existing correlation/retry behavior. T-362 acceptance promises production narrowing, gate refusal and epoch behavior, without an operation-specific replay receipt. DES-0029 K10's broader same-key response promise and the `/api/session` exclusion in `NodeMutationIdempotency` deserve separate contract reconciliation. They do not authorize inventing a new permission-narrowing replay policy in this audit repair.

The exact-head all-lane receipt and hosted protected-queue qualification are recorded separately; targeted tests and scoped mutation evidence alone do not establish protected landing.
