# PR 331 hierarchy scoped mutation evidence

The native run tested source head `0cb6f58fcc801a04e84c6bab731af12400a30e1c` against main `4e980008449928654a9097f9ca2b44c6ac5831b4` in 228 seconds. This note changes no production or test bytes.

Command:

```text
node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/Data/Entities/NodeHierarchyCompositeCoordinator.cs' --filter 'FullyQualifiedName~Hierarchy|FullyQualifiedName~AuthorizationWriteStageTests|FullyQualifiedName~CompiledSchemaEntityValidationTests|FullyQualifiedName~RecordWriteRulesStageTests'
```

Target SHA-256: `3EAAFA2EB162D28DBC529D044554E809C0D8E61AFBB895AA94A8D2CDA92E4343`. Base config SHA-256: `720A013FBDE90D08C89F1EFE6E57221AFC2A07543B1493F8FD37C09D403FEA5F`. The generated scoped config preserves the existing scoped-run policy, disables since, selects the whole file and the stated test filter, and does not change a tracked gate or baseline. Discovery found 122 tests.

Raw report and generated config are archived at commit `a5f9d68d687add3fb15a84e0d0d9319c3cc45259` on `archive/pr331-scoped-0cb6f58f-20261003`, under `docs/evidence/raw/pr331-0cb6f58f/`. Report SHA-256: `859ECF402CDFF06C31AF3CECC85C34EA7F2286928EF42DD2EDE079F5D5CB7041`; size 12,583,509 bytes. The report embeds the test manifest and source. Match mutations by file, location and replacement; IDs below identify this run only.

The coordinator has 86 Killed, 70 Survived, 5 NoCoverage, 20 CompileError and 28 Ignored mutations. There are 156 tested mutations, zero Timeouts, and a reported score of 53.42. Project-wide compile rollbacks are distinct from these file counts. This is not a survivor-free result or a replacement for required CI gates.

All tests below are in `HierarchyCompositeWriteEffectsTests`, except the future merge start test in `AuthorizationWriteStageTests`.

| Obligation | Mutation in coordinator | Status | Killing test recorded in JSON |
| --- | --- | --- | --- |
| Traverse temporal paths | 5942, line 402, remove initial pending interval | Killed | `FutureMultihopDescendant_RefusesOnlyWhenAllPathIntervalsOverlap` |
| Traverse multiple edges | 5969, line 415, remove pending push | Killed | `FutureMultihopDescendant_RefusesOnlyWhenAllPathIntervalsOverlap` |
| Respect future path start | 5950, line 409, force inherited interval start | Killed | `ReparentWithNonoverlappingFutureReverseEdge_DoesNotRefuse` |
| Exclude empty interval overlap | 5965, line 412, remove continue | Killed | `ReparentWithNonoverlappingFutureReverseEdge_DoesNotRefuse` |
| Recheck inside atomic unit | 5976, line 428, remove cycle recheck | Killed | `OpposingReparentAtLaterInstant_RefusesEarlierMoveAtCommit` |
| Refuse changed displaced state | 5981, line 432, remove refusal | Killed | `ChangedDisplacedEdges_RefuseCommitAndWriteNothing` and `CompetingReparent_RefusesStaleCommitAndPreservesFirstReplacement` |
| Exclude merged internal child | 5901, line 296, remove continue | Killed | `Merge_OfARecordWithItsOwnChild_ReparentsOnlyOutsideChildren` |
| Preserve future merge start | 5895, line 293, force act instant | Killed | `HierarchyMerge_AdmitsChildCommittedAfterTheActInstantWhileWaitingForTheScope` |
| Preserve longest finite replacement end | 5929, line 375, Max to Min | Killed | `Reparent_TwoFiniteDisplacedEdges_ReplacementEndsAtTheLater` |

Oracles are literal entity IDs and admission instants, expected edge endpoints and intervals, and independently asserted entity/audit counts. They do not read the coordinator's output to construct their expected state. The concurrent tests pause the first admission, commit an opposing or displaced-state change, then assert refusal before any stale mutation and preservation of the winner.

Limits: finite-end invalidation removals at lines 300 and 438 are CompileError, not Killed. The clipping comparisons at line 411 have NoCoverage mutations; the visited-interval continue at line 405 is also NoCoverage. The other two uncovered mutations are the missing admitted-decision throw at line 525. Survived mutations include cancellation/ConfigureAwait booleans, exception text, authorization Require statements, and the ancestor throw at line 396 (temporal traversal still supplies refusal). No blanket equivalence or harmlessness claim is made for all 70 survivors. The equality changes from `>` to `>=` in maximum-start selection preserve the selected value when both instants are equal. These disclosures remain review evidence, not waivers or changes to owner policy.

The previously completed focused suite at the tested head passed 147 tests, zero skipped. The raw scoped report above is the current mutation evidence; earlier hosted mutation timeouts produced no substitute report.

## Temporal traversal follow-up at 9a5fbc32

Independent review of the first report found two material test gaps despite correct production logic: forcing an edge's earlier start instead of preserving the inherited maximum (5949), and failing to clip a finite inherited end to an earlier edge end (5958). The production file is unchanged. Two new independent regressions at source/test head `9a5fbc32aac426747510df76a5a9ec819cf83071` pin these intervals:

- `FutureMultihopDescendant_DoesNotWidenAnInheritedStartBackward` supplies path legs `[day2,day4)`, `[day1,day3)`, `[day1,day1.5)`, whose common interval is empty. Forcing the second leg's day1 start falsely refuses the move.
- `FutureMultihopDescendant_ClipsAFiniteInheritedEndBeforeTheNextLeg` supplies candidate interval `[day0,day3)` and path legs `[day1,day2)`, `[day2,infinity)`. The first leg must clip the candidate's finite end before examining the touching second leg.

Each targeted fault produced one red test with a false-cycle ArgumentException. Restoring the unchanged production file produced 124 focused green tests, zero skipped, using the same filter as the command above. The new tests assert exact literal replacement and path intervals, the original path endpoints, and one audit row with the input justification; their expected state is not constructed from production output.

The same native whole-file command then completed in 286 seconds. The report embeds exactly 124 tests. Raw report, generated config and extracted manifest are archived at commit `3eafbe2d94fbd98c899b7cffc0bf76b5a1f37878` on `archive/pr331-scoped-9a5fbc32-20261003`, under `docs/evidence/raw/pr331-9a5fbc32/`. Report size is 12,591,256 bytes; SHA-256 is `87757248FE9D2E669CFC3AA628B2B2E6537EFD78B4F7D8C68B38EE352F58F889`. Manifest SHA-256 is `AACB48CFDB5DF490C13FB311090B72EA05BF14CBD69D25E0DA5A2AB9FC3C83CF`.

This rerun supersedes the first report's temporal follow-up status. The coordinator has **76 Killed, 12 Timeout, 70 Survived, 3 NoCoverage, 20 CompileError, 28 Ignored**: 158 tested mutations, reported score 54.66. Timeout is reported separately and is not claimed as an assertion kill. All nine cited semantic mutations in the earlier table remain Killed in this rerun, with named killing tests in its JSON.

| Follow-up | Exact location and replacement | Status | Recorded killing test |
| --- | --- | --- | --- |
| Preserve inherited start | 5949, line 409, `(true?edge.Validity.ValidFrom :interval.From)` | Killed | `FutureMultihopDescendant_DoesNotWidenAnInheritedStartBackward` |
| Clip finite inherited end | 5958, line 411, `edgeEnd > to.Value` | Killed | `FutureMultihopDescendant_ClipsAFiniteInheritedEndBeforeTheNextLeg` |

The comparison `edgeEnd <= to.Value` at line 411 is now covered and Survived; replacing a bound by the same value when equal preserves its interval. The earlier inherited-start equality survivor at line 409 has the same equal-value property. The three remaining uncovered mutations are line 405's visited-interval continue and the two missing-decision throw mutations at line 525. Finite-end invalidation removals at lines 300 and 438 remain CompileError.

The twelve Timeout IDs in this run are 5810 (line 36), 5811 (37), 5823/5825 (123), 5830/5832 (135), 5839 (148), 5865 (186), 5966 (413), 6004 (480), 6013 (524), and 6016 (527). These were Killed in the earlier raw report, but that historical status is not substituted for their current Timeout result. No deadline, gate, baseline or production behavior was changed, and no blanket clearance is claimed for the residual survivors, compile errors, uncovered mutations or timeouts.

## Scheduled split correction at b3ef2327

Source/test head `b3ef232711a0530dc596386bdb3c164e859601e2` fixes requested future child edges omitted by active-only split snapshots. Both Bind and atomic reread select requested unended edges. Replacement and old-edge close start at the later of the scheduled start and act instant, retaining finite ends. Unrequested future children remain untouched. The admission instant remains the act instant.

Four new cases were red against the previous production implementation: open/finite scheduled reassignment and added/changed future snapshots. Restored production passed 141 focused tests with zero skipped. Independent literal oracles cover endpoints, scheduled intervals, invalidations, entity and audit state, and refusal before minting.

The same whole-file native scoped command above then completed in 274 seconds, embedding 128 tests. This report supersedes the previous source's mutation evidence: **83 Killed, 11 Timeout, 71 Survived, 3 NoCoverage, 20 CompileError, 28 Ignored**, 165 tested, score 55.95. Timeouts are not assertion kills. Raw report, generated configuration and test manifest are archived at `716d0f44` on `archive/pr331-scoped-b3ef2327-20261003`, under `docs/evidence/raw/pr331-b3ef2327/`. Raw size 12,612,107 bytes; SHA-256 `7C857B7C74E8A85FB4CDBCB5258A6BEB9A26DF82FCACA79D7FE86C7ECD4DD0CE`. Manifest SHA-256 `6538948ADE791F859CE6A5CDE96EEBAEC7F94B7D352E7B3520308936C873F68D`. Tested production SHA-256 `8C50489C0CBA36B212884040F460A195FDACA202ABB9048814F5133445593193`.

| Obligation | Current mutation | Status | Named killing test |
| --- | --- | --- | --- |
| Refuse changed future snapshot | 5840, line 149, remove refusal | Killed | `Split_ChangedFutureChildSnapshot_RefusesBeforeMinting` |
| Preserve scheduled start | 5850, line 163, force act instant | Killed | `Split_ReassignsRequestedFutureChild_PreservesScheduledInterval` |
| Retain finite scheduled end | 5859, line 169, remove invalidation | Killed | `Split_ReassignsRequestedFutureChild_PreservesScheduledInterval` |
| Preserve requested-child selection | 6016, line 518, negate inclusion | Killed | `Split_ChangedFutureChildSnapshot_RefusesBeforeMinting` |
| Preserve inherited start | 5953, line 412, force edge start | Killed | `FutureMultihopDescendant_DoesNotWidenAnInheritedStartBackward` |
| Clip finite inherited end | 5962, line 414, reverse comparison | Killed | `FutureMultihopDescendant_ClipsAFiniteInheritedEndBeforeTheNextLeg` |
| Atomic cycle recheck | 5980, line 431, remove recheck | Killed | `OpposingReparents_TheSecondToCommitIsRefusedInsideItsUnit` |
| Displaced-state refusal | 5985, line 435, remove refusal | Killed | `ChangedDisplacedEdges_RefuseCommitAndWriteNothing` |

The three NoCoverage mutations remain the visited-interval continue at line 408 and missing-decision throw at line 530. Residual survivors, timeouts and compile errors are disclosed by the archived raw report; no blanket equivalence, survivor clearance, policy waiver or required-gate replacement is claimed. Earlier report IDs and outcomes remain historical evidence only.

## Complete split prospective-graph follow-up at 37fa5a35

Source/test head `37fa5a35da30534c5cf4a77dee07e1f34893a94d` refuses split cycles against the complete prospective temporal graph, including cycles introduced jointly by multiple reassignments. The shared Split/Reparent traversal excludes displaced edges, overlays every proposed replacement, and intersects path intervals. Split checks at Validate and repeats inside its atomic unit after snapshot comparison and before minting. Replacement IDs and reassignment parents cannot name the original that will be deleted. Scheduled finite starts and ends remain intact.

Six cases failed against the previous production implementation, while two nonoverlap controls passed. The restored broader hierarchy/authorization/fence suite passed 149 tests, zero skipped. Independent regressions assert exact literal endpoints/intervals, preservation of the winning intervening path and its audit, refusal without composite writes, and successful touching/nonoverlapping intervals. Transaction-entry counts additionally pin admission-time refusal before opening the unit: removing that check produced five red cases. Changing the deleted-target check from Any to All produced one red multi-target case. Restored production passed 149 tests after both stronger oracles.

The initial prospective-graph report at `34291401e4df3e166c90a27ff2d9dca9aacfa56c` had 107 Killed, 79 Survived, zero Timeout, 3 NoCoverage, 20 CompileError and 30 Ignored, 186 tested, 136 embedded tests, 280 seconds. Archive `e170f09a540a6fa48e223f9eaf6f85d7822586ae` on `archive/pr331-scoped-34291401-20261003`, raw SHA-256 `073E8498AF331817D44AFF629E11155755B51C7C77E9012BEA16BB12AF313D86`. Its admission-check removal survivor prompted the transaction-entry oracle.

The intermediate admission-oracle report at `f5e7c2bbfd417f57fe78c95dd2a3819fa45c9dc8` had 103 Killed, 6 Timeout, 77 Survived, 3 NoCoverage, 20 CompileError and 30 Ignored, 186 tested, 136 tests, 293 seconds. Archive `cfbc06a98a1423e49e70fc7e8531b91c50639b61` on `archive/pr331-scoped-f5e7c2bb-20261003`, raw SHA-256 `7846128A1E31006672A69137D43645213F7760EC092A30F1A223DDC292C7C83A`. Its Any-to-All survivor prompted the multi-target deletion oracle. These two reports are historical, not substitutes for current outcomes.

The final same-command whole-file scoped run completed in 271 seconds at `37fa5a35`: **110 Killed, 76 Survived, zero Timeout, 3 NoCoverage, 20 CompileError, 30 Ignored**, 186 tested, reported score 58.20. It embeds 136 tests. Raw report, configuration and manifest are archived at `ecded48243aff08a4c1b98140fce9d6bc3a96607` on `archive/pr331-scoped-37fa5a35-20261003`, under `docs/evidence/raw/pr331-37fa5a35/`. Report size 12,686,255 bytes; SHA-256 `539252C260CFD38AE1CCF36F500709E2BE43C33F216E58BBA592310DA28395BD`. Configuration SHA-256 `B283F7E65EC840A721863339CE662F34504B609FAD8958DF40A1A332122DC46F`; manifest SHA-256 `289A87D663254BDCDA110B27F45F087FF53555B0372947E0F51DCFCF0920C813`. Tested production file SHA-256 `CD2E2E2D8D9A07A5E32F1C83D3F59099F01385E2AE4646A2D0AF6780610935DB`.

| Obligation | Final mutation | Status | Recorded killing test |
| --- | --- | --- | --- |
| Refuse at admission before opening a unit | 5821, line 122, remove check | Killed | `Split_ScheduledDescendant_RefusesOverlappingPathBeforeAnyWrite` |
| Check every replacement ID | 5839, line 144, Any to All | Killed | `Split_DeletedOrSelfTarget_RefusesBeforeAnyWrite` |
| Preserve deleted-replacement refusal | 5841, line 145, remove throw | Killed | `Split_DeletedOrSelfTarget_RefusesBeforeAnyWrite` |
| Preserve deleted-parent refusal | 5844, line 147, remove throw | Killed | `Split_DeletedOrSelfTarget_RefusesBeforeAnyWrite` |
| Repeat graph check inside atomic unit | 5861, line 166, remove check | Killed | `Split_ScheduledDescendant_RefusesOverlappingPathBeforeAnyWrite` |
| Include all proposed replacements | 6016, line 503, remove overlay | Killed | `Split_JointReassignments_CheckProspectiveTemporalGraph` |
| Preserve inherited start | 6019, line 506, force edge start | Killed | `FutureMultihopDescendant_DoesNotWidenAnInheritedStartBackward` |
| Clip finite inherited end | 6028, line 508, reverse comparison | Killed | `FutureMultihopDescendant_ClipsAFiniteInheritedEndBeforeTheNextLeg` |

This final report supersedes prior current-source mutation evidence. The visited-interval continue at line 497 and missing-decision throw at line 558 remain NoCoverage. Residual survivors include exception text, cancellation/task wrapper booleans, and equal-value clipping comparisons; no blanket equivalence or survivor clearance is claimed. CompileError remains separate from assertion kills. No gate, deadline, baseline, policy waiver or owner contract was changed.

## Active-child historical interval follow-up at 2cc9406a

Test head `2cc9406aa5c78606d6f31763f322ace32f09d54c` adds `Split_ActiveChild_IgnoresAnEndedHistoricalReversePath`: the active child starts three days before admission; the reverse path exists only during the two-to-one-days-before interval. Splitting at admission succeeds with literal replacement interval [at, infinity), the old interval closes exactly at admission, and the ended reverse edge remains unchanged. Exact results, one atomic entry, invalidation, and audit are independently asserted. Forcing the proposed edge start to its historical start produced one red test; restored production passed 150 focused tests, zero skipped. Production SHA-256 remains `CD2E2E2D8D9A07A5E32F1C83D3F59099F01385E2AE4646A2D0AF6780610935DB`.

The same native whole-file scoped command at that exact head completed in 280 seconds with **111 Killed, 75 Survived, zero Timeout, 3 NoCoverage, 20 CompileError, 30 Ignored**, 186 tested, 137 embedded tests, score 58.73. Archive `97817a81d5287a9991c05eb495e52545ca41140a`, branch `archive/pr331-scoped-2cc9406a-20261003`, directory `docs/evidence/raw/pr331-2cc9406a/`. Report size 12,693,027 bytes, SHA-256 `AFBEA9A773F26733DED93C01C4F1819636A8363DFE81E4875B9A337A7D741D47`; configuration SHA-256 `B283F7E65EC840A721863339CE662F34504B609FAD8958DF40A1A332122DC46F`; test-manifest SHA-256 `3DA1A6E1D69F17BE8AFFAD71350B1AABCE0A59AD8FDF0F455395D3B577618EB9`.

Mutation 5846, line 150, forcing the proposed start to edge.Validity.ValidFrom, is **Killed** by the new active-history test. All eight prior named obligations remain Killed in this raw report: 5821 (admission check), 5839 (Any to All), 5841 (deleted replacement), 5844 (deleted parent), 5861 (atomic cycle recheck), 6016 (complete proposed overlay), 6019 (inherited start), and 6028 (finite inherited end), with the same recorded killing tests listed above. This refresh supersedes the preceding current-source report while preserving every historical archive. The three uncovered sites and residual survivors remain disclosed; compile errors are separate from assertion kills. No blanket survivor clearance, policy exception, baseline change, or gate replacement is claimed.
