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
