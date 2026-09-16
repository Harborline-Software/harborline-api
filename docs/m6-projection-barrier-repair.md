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

## Schema iterator follow-up

The full exact-clone run at `36a6cfa14e92297a47ba1ac3ed6b3d837c9315f4`
stalled in the host suite. Source inspection found that `InMemorySchemaRegistry.ListAsync`
still held its read lease across `yield return`. A consumer does not inherit an
async iterator's ambient lease. A queued activation can therefore wait for the
paused iterator while a subsequent consumer read waits behind that activation.
The running suite was stopped after its prolonged all-wait state was observed;
no stack capture identified its particular blocked test. The following bounded
regression independently demonstrates the schema-iterator cycle.

`Paused_schema_enumerator_allows_queued_activation_and_consumer_read_and_retains_snapshot`
holds an independent reader, pauses schema enumeration, queues activation,
starts an independent consumer read, and releases the initial reader. Before
the schema repair this failed after its ten-second cancellation bound waiting
for activation. The retained `barrier-schema-red.trx` has SHA-256
`0CF7DA12F80893E6E46AAD8CC7CF56298CB8ACF7ABAAD460BCD2A169E8C65A91`.

The registry now materializes matching schemas under the read lease and releases
it before yielding. The regression also checks that its original enumeration
excludes the newly committed schema, a fresh enumeration includes it, and the
consumer read completes. All actors use separate execution contexts.

An audit of every production file containing `PackProjectionActivationBarrier.Read`
and `yield return` found the form and standing iterators already snapshot before
yield; role vocabulary's yields belong to a separate seed generator with no
lease. Schema enumeration was the only remaining lease spanning a yield.

Final expanded check:

```powershell
dotnet test apps/local-node-host/tests/tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PackProjectionTransactionTests|FullyQualifiedName~PackProjectionResourceLifetimeTests|FullyQualifiedName~AccessAdministrationPreloadTests|FullyQualifiedName~Standing|FullyQualifiedName~Schema" --logger "trx;LogFileName=barrier-schema-final.trx" --results-directory artifacts/m6-platform-form-title
```

Result: 156 passed, zero failed or skipped. TRX SHA-256:
`CD8FCBB3147B3D0B6DC149C668AF25D88A221D445AF813CC2F21FEE75982F279`.
