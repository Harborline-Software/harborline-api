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
