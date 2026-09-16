# M6 projection barrier repair

The exact-clone run at `95c2efe212036a1f8e5da18db5fca17b57e1b0d8`
reported 4,315 host tests: 4,293 passed, one failed, 21 skipped. The failure was
`PackProjectionResourceLifetimeTests.Paused_form_enumerator_does_not_block_activation_and_retains_its_snapshot(publishedOnly: True)`.
Its retained report is `.claude/gate-evidence/exact-clone-fail.json`. The stack
ends at the cancellation check inside `PackProjectionActivationBarrier.Enter`,
while acquiring activation, before the test could mutate any form.

Both form enumeration paths already materialize their snapshot and release the
read lease before the first yield. The activation barrier is process-global,
however, and the test's two-second limit included unrelated parallel test hosts.
The report does not identify which unrelated lease delayed that acquisition.
The two classes that deliberately pause this global barrier now run in one
nonparallel xUnit collection. Their concurrent actors, deadlines, snapshot
assertions, and cancellation assertions remain in place.

Inspection also found an admission defect: new independent readers could enter
ahead of a waiting activation indefinitely. The regression holds an initial
reader, starts an activation on a separate execution context, observes that
contender waiting, and then starts another reader. The old barrier immediately
admits the latter reader; the new FIFO queue preserves the activation's place.
Nested reads remain reentrant, and adjacent independent readers remain
compatible. Cancellation removes the queued contender and wakes successors;
a final cancellation check prevents admission after a cancellation races with
the last holder leaving.

## Bounded verification

Before changing the admission algorithm:

```powershell
dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Queued_activation_precedes_later_readers|FullyQualifiedName~Cancelled_queued_activation_releases_later_readers" --logger "trx;LogFileName=barrier-fairness-red.trx" --results-directory artifacts/m6-platform-form-title
```

Both new tests failed. The queue-order test failed because the later reader
completed while activation was waiting. The cancellation test's cleanup also
observed the old barrier granting a cancelled activation after the holder left;
cleanup now drains that actor without masking the primary assertion.
TRX SHA-256: `63E5CABFFA7D6E0109BCAA60EA8C669DD59E1655D0320076EE4B44EB97B7032F`.

After the repair:

```powershell
dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PackProjectionTransactionTests|FullyQualifiedName~PackProjectionResourceLifetimeTests|FullyQualifiedName~T433ReplacementAtomicityTests" --logger "trx;LogFileName=barrier-fairness-green.trx" --results-directory artifacts/m6-platform-form-title
```

All 17 tests passed, with zero failures or skips. This covers queue ordering,
queued cancellation, nested reads, both paused form enumerators, paused standing
enumeration, transaction commit/rollback, and replacement atomicity. TRX:
`artifacts/m6-platform-form-title/barrier-fairness-green.trx`, SHA-256
`285283E6B8020029AADFC939748A38DBE08733BCCFF817E2B9AD4684A6E85081`.

No failure baseline, retry allowance, or existing timeout was changed. The full
exact-clone gate remains required before this branch is released.
