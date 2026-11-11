# T-521 epoch rotation — 2026-11-11

Scope: `T-521/content-store-epoch-key-store`, API draft PR #472. Control source:
`3030625341fe129304543ee3ecce82dc9c5d3fca`, slice acceptance 3, 4 and 8, ADR 0116 decisions 3–8,
DES-0054 §10 and accepted ADR 0127 decisions 1–5. ADR 0127 settles the previously open backup signal:
the host exposes backup handles, R1 opens none, and any open handle blocks key and byte destruction.
No snapshot backup, marker file or speculative backup detection was added.

API main `ca90c914` was merged normally as `0fffc324`. Its platform pin `22491171` is an ancestor of this
branch's `2d820840b2f49815db65270e6bdb1ffbae7c3be7` (platform #431), so the newer branch pin was retained.
Main advanced during validation to `c231e44787065285e43788995c2b08173c45b302` (the Cbor security update),
which was merged normally as `94a7abd8`; its dependency change is inherited, not a slice-owned edit.
The exact-pin local feed built all 36 producers successfully. The pre-change merged branch's Release build
and ContentStore/ArchTests/affected Entities catalog run passed 636/636, with zero skipped tests.

## Oracles

- Registration: slice literals `content-key-rotation`, `schedule`, `sys.sched.content-key-rotation`,
  `content-store`; both registrations resolved from the actual composition.
- Rotation, batching and destruction: ADR 0116's literal 90-day rotation and 30-day waiting period,
  including one-tick-before/exact-boundary assertions. Two independent committed rows and one-row batches
  establish full traversal; persisted files and row epoch IDs establish the result.
- Retirement: an actual SQLite reader prevents `TRUNCATE`, independently before retirement and after its
  SQL fence; `.pending` must not exist until the reader releases. A remaining epoch file is the refusal oracle.
- Backups: ADR 0127's any-open-handle rule, tested with nested handles, both key phases, immediate byte
  deletion and deferred sweeping. File existence is asserted before and after the final handle closes.
- Erasure: unencrypted SQLite with `secure_delete=OFF` demonstrably retains the deleted item's wrapped-key
  bytes in the database after the waiting period. A separately written HKDF/AES-GCM unwrap uses the
  producer format's literal purpose/AAD strings. It succeeds before destruction and fails afterward because
  the actual epoch key is gone; this does not claim raw-disk secure deletion.
- Actor/metadata: the slice's `sys.scheduler` identity and fail-closed unreadable pending timestamp.
- Crash recovery: literal payloads `hello` and `still-staged`, actual persisted bookkeeping and epoch files,
  and child exit code 73 plus a named reached-step signal. Each restart decrypts the committed item and
  independently unwraps the staged row, resumes the rotation, then checks both again.

## Hand mutations

Each mutation was applied alone, compiled and run against its named test, then the original source was restored
byte-for-byte. All twelve produced assertion/test failures, not compile errors. No threshold, baseline, test
selection policy or suppression was changed. Names below are methods on `NodeContentStoreTests`.

| ID | Mutation | Killing test |
| --- | --- | --- |
| R1 | Wrong run kind | `Rotation_run_uses_the_slice_literals_and_the_sweeps_registration_shape` |
| R2 | Ignore the rotation period | `Rotation_rewraps_multiple_batches_and_waits_thirty_days_across_restart` |
| R3 | Stop after one rewrap batch | `Rotation_rewraps_multiple_batches_and_waits_thirty_days_across_restart` |
| R4 | Ignore a failed batch checkpoint | `An_unfinished_TRUNCATE_checkpoint_cannot_start_the_destruction_wait` |
| R5 | Ignore the post-fence checkpoint | `The_checkpoint_after_fencing_old_pins_must_finish_before_the_wait_is_marked` |
| R6 | Ignore the rotation backup guard | `Backup_handles_gate_both_retirement_and_destruction_and_byte_sweeping` |
| R7 | Ignore the destruction waiting period | `Rotation_rewraps_multiple_batches_and_waits_thirty_days_across_restart` |
| R8 | Keep the destroyed epoch file | `Rotation_rewraps_multiple_batches_and_waits_thirty_days_across_restart` |
| R9 | Ignore the immediate byte-deletion backup guard | `Backup_handles_gate_both_retirement_and_destruction_and_byte_sweeping` |
| R10 | Ignore the conditional sweep-deletion backup guard | `Backup_handles_gate_both_retirement_and_destruction_and_byte_sweeping` |
| R11 | Accept a non-scheduler actor | `Rotation_refuses_a_non_scheduler_actor_before_changing_the_current_epoch` |
| R12 | Treat corrupt metadata as zero | `Unreadable_pending_metadata_refuses_instead_of_destroying_a_key` |

Raw logs and the machine-readable mutation receipt are in the thread-created directory
`/private/tmp/t521-epoch-20261111-PEzy1R` (`hand-R1.log` through `hand-R12.log`, `hand-mutations.json`).
These are hand-mutation results, not a Stryker score or certificate.

## Acceptance 8: real key-store process crashes

Executed on macOS 27.2, arm64, .NET SDK `11.0.100-rc.1.26425.128`. The installed SDK compiles the
worker fixture; no extra project, package or runtime is installed. A separate process exits at each actual
boundary, leaving the real SQLCipher database, key directory and content files for the parent to reopen:

- Epoch file: device flush, rename, directory flush.
- Current pointer: device flush, rename, directory flush.
- Rewrap: batch commit, successful checkpoint.
- Retirement: SQL fence, successful checkpoint, durable pending timestamp.
- Destruction: unlink, directory flush.

All 13 injection cases passed independently and were repeated successfully before final integrated validation.
These are process-crash tests, not power-cut tests: OS caches survive process exit, so they do not prove that
removing a flush would lose data on a power failure. No such flush-removal mutation is claimed.

## Final integrated validation

After both main merges, the Release build/restore passed with zero errors and 411 warnings (the initial baseline
also had 411); no warning suppression was added. The final run passed **657/657**, zero skipped,
including all **131 ContentStore cases** and all **13 child-process crashes**:

```sh
dotnet build apps/local-node-host/tests/tests.csproj -c Release --disable-build-servers -m:1
dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1 \
  --filter 'FullyQualifiedName~ContentStore|FullyQualifiedName~ArchTests|FullyQualifiedName~LocalNodePatternAModuleCatalogTests'
```

The final TRX is `final-tests/final-main.trx` in the thread's scratch directory; build/test logs are
`final-main-build.log` and `final-main-tests.log`. The earlier `final.trx` records the same 657/657 result before
the dependency update. No final test failure needed a clean-main comparison. The repository's
`eng/verify-boundaries.sh` passed, including its gate self-tests. The identity-R3 scanner passed over 5,771 tracked
files with zero unexempted hits; contract generation `--check` and the codegen guard suite passed.
This is not a full exact-clone/quality/Stryker gate run.

`eng/dependency-ledger.mjs` passed its package justifications, central pins, NuGet licence checks and lockfile policy,
but returned 1 because both existing pnpm audits returned 1 (`apps/capability-host` and `packages/contracts`).
Both failures were reproduced with `pnpm audit --audit-level high` in a fresh, untouched `git archive` of main
`c231e44787065285e43788995c2b08173c45b302`. Each existing NAS audit request exhausted its retries and returned
`[23] The operation was aborted due to timeout`. Logs: `main-capability-audit.log` and `main-contracts-audit.log`.
This is an audit-service failure, not a clean vulnerability report. No dependency/audit/registry configuration was
changed to work around it. The final boundary/preflight suite was rerun after the security merge and passed.

Linux and Windows execution remain outstanding. The worker is portable and has no host skip, but macOS evidence
does not satisfy acceptance 8's explicit Linux-and-Windows host requirement. Host enablement and schedule dispatch
remain under the existing content-store host hold; this slice adds the registration and executable handler at the
same seam as the staging sweep, not an independently enabled background timer.

## 2026-11-11 — parent-directory durability review repair

Codex review thread `discussion_r4538399917` identified that syncing the tenant directory alone did not persist
the entries for newly created `content-epochs` and tenant directories in their parents. The shared durable writer
now syncs the leaf and every ancestor through the existing filesystem root after rename and before returning.
It does this even when directories already exist: an interrupted attempt can leave them visible without having
completed their parent syncs. Any sync exception propagates; no successful write is reported on that path.
The change covers epoch creation, the current pointer and rotation metadata without changing their callers.

`NodeContentEpochDirectoryTests.cs` adds four deterministic regression cases. The oracle is acceptance 3 and
ADR 0116 decision 5: the literal `tenant-keys/content-epochs/<tenant>` hierarchy must be persisted before a
key write succeeds. Two cases record the actual sync callback's directory order while also performing the native
sync, for a newly created hierarchy and one already visible after interrupted creation. Two cases inject an
`IOException` at the `content-epochs` or `tenant-keys` parent sync and require that exact failure, not success.

The authorized native retry reproduced all four failures before the fix (`directory-red.log`); the fixed writer
passed all four (`directory-green.log`). Hand mutation D1 stops traversal after the leaf, restoring the original
defect; all four tests failed (`directory-hand-mutation.log`), and the source was restored before final validation.
This is deterministic call-order/error-propagation evidence, not a hardware
power-cut experiment or proof that removing a native fsync primitive loses data. Logs remain in the thread's
owned scratch directory `/private/tmp/t521-epoch-20261111-PEzy1R`.

The final Release build/restore passed with zero errors and 93 incremental-build warnings. The ContentStore,
ArchTests and affected Entities catalog filter passed 661/661, zero skipped, including all 13 real crash cases.
The final TRX is `directory-final-tests/directory-final.trx`; build/test logs are `directory-final-build.log` and
`directory-final-tests.log`. Boundary/preflight self-tests, identity-R3 scan, contract generation `--check`, the
32-case codegen guard suite and `git diff --check` passed. The sandbox boundary run could not obtain process
identity for its gate-lock self-tests; the unchanged command passed natively. No registry/audit configuration,
test threshold or warning policy was changed. The previously reproduced clean-main audit-service failures and
outstanding Linux/Windows host evidence remain as recorded above; no full gate or Stryker score is claimed.
