# ck-2 S5/S6 scoped mutation evidence (K9 undeclared references, K2 unknown content kinds)

This is scoped Stryker.NET evidence for the refusals api PR #258 added: the deriver in `packages/foundation-packs/Export/PackContentReferenceDeriver.cs` refuses undeclared references, `BuildPlan` in `Install/PackInstaller.cs` re-derives edges, `Serialization/PackFileCodec.cs` `TryDecode` names the unknown kind, and `Verify/PackVerifier.cs` emits `pack.verify.content.kind_unknown`. It is not a certificate for the whole pack installer.

## Setup

- `apps/local-node-host/tests/tests.csproj` now references `packages/foundation-packs/Harborline.Foundation.Packs.csproj` directly, so Stryker can mutate it. One config mutates one direct reference, the same pattern PR #256 uses for foundation-authorization.
- `eng/mutation-report.mjs` `--scoped` mode was copied from PR #256 unchanged. Drop this copy when #256 merges.
- Each run is `node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/<file>' --project Harborline.Foundation.Packs.csproj --filter <expr>`, with Stryker.NET 5.0.0, the pinned SDK's MSBuild.exe, and per-test coverage.
- Deriver and installer filter: `PackContentReferenceRouteTests`, `PackContentReferenceDeriverTests`, `PackContentKindAdmissionTests`, `RestrictingDefinitionKindAdmissionTests`, plus the three composed-host `Route_install*` and `Install_reports*` tests.
- Codec and verifier filter: the same list without the composed-host tests. With those tests included, codec mutants that break the host's own boot decode ended as Timeout after the 3-minute readiness wait, and one left an orphan host process that locked the test bin. The in-process tests cover the same spans.

## Instrumentation fixes (no behaviour change)

The first runs showed Stryker Safe Mode removing every mutation in `DetectUndeclaredReferences`, and CompileError on the whole `kindPointer` branch. The causes were an unassigned-local `try` pattern and an `is { } kindPointer` pattern variable. Both are now plain definitely-assigned locals, and both spans instrument.

## Refusal-bypass mutants and their killers

| File:line | Mutant | Status | Killing test |
|---|---|---|---|
| Deriver:95 | `toPackKey is not null` | Killed | PackContentReferenceRouteTests.Export_refuses_a_reference_into_an_undeclared_package |
| Deriver:98 | drop `unresolved.Add` | Killed | Export_refuses_a_reference_into_an_undeclared_package; Install_rederives_edges_and_refuses_an_undeclared_reference |
| Deriver:89 | negate own-content check | Killed | Export_refuses_a_reference_into_an_undeclared_package |
| Deriver:136 | `key + ""` (bare-prefix ownership) | Killed | PackContentReferenceDeriverTests.Bare_prefix_is_not_ownership |
| Deriver:69 | own key not known | Killed | PackContentReferenceDeriverTests.Own_namespace_reference_is_intra_app |
| Deriver:91 | drop sibling `continue` | Killed (manual red B5; NoCoverage in the first run) | PackContentReferenceDeriverTests.Sibling_reference_is_intra_app_without_a_namespace |
| Installer:1036 | `undeclaredReferences.Count >= 0` / negate | Killed | PackContentReferenceRouteTests.Install_rederives_edges_and_refuses_an_undeclared_reference |
| Installer:1000 | CHECK drops the undeclared refusal | Killed | Install_rederives_edges_and_refuses_an_undeclared_reference (CHECK leg) |
| Installer:1365/1373/1375 | re-derivation parse or add removed | Killed | Install_rederives_edges_and_refuses_an_undeclared_reference |
| Installer:780 | negate / `kindPointer is null` | Killed | RestrictingDefinitionKindAdmissionTests.Install_names_an_unknown_content_kind_before_store_or_projector |
| Installer:782 | unknown-kind block removed (falls to not_verified) | Killed | Install_names_an_unknown_content_kind_before_store_or_projector |
| Codec:65/117/131 | pointer not returned / emptied | Killed | PackContentKindAdmissionTests.Unknown_Content_Kind_Names_Its_Pointer |
| Codec:122 | `&&` to `\|\|` (unknown kind string passes) | Killed | PackContentKindAdmissionTests.Unknown_Kind_Shapes_Name_Their_Pointer |
| Codec:126 | `_ => true` (non-string, non-number kind passes) | Killed | Unknown_Kind_Shapes_Name_Their_Pointer |
| Codec:129 | `!known` inverted | Killed | Known_Content_Kind_Still_Decodes; Unknown_Content_Kind_Names_Its_Pointer |
| Codec:122 | `ignoreCase: false` | Timeout (manual red B3) | PackContentKindAdmissionTests.Kind_Gate_Is_Case_Insensitive |
| Verifier:49 | conditional true/false, `is not null` | Killed | Install_names_an_unknown_content_kind_before_store_or_projector (also asserts malformed maps to `pack.verify.malformed` and `not_verified`) |

## Survivors, classified

- Deriver:64 (`ThrowIfNull(contents)` removed): equivalent. `contents.Select` throws the same `ArgumentNullException` from LINQ.
- Deriver:72 (`&&` to `||` in known-key filtering): equivalent. It only adds a duplicate key, or a blank key that the export validator already refuses (`pack.validation.dependency.unpinned`).
- Deriver:161/163 (CompareEdge tie-breaks): unreachable. The extractor emits at most one reference per item, and content keys are unique, so two edges never tie on `FromContentKey`.
- Installer:780 (`&&` to `||`): equivalent under the verifier invariant. `FailurePointer` is set only together with `ContentKindUnknown` (PackVerifier:49).
- Installer:771/774, 993, 996/997/1003: pre-existing code outside the S5/S6 span (verify gate shape, CHECK/INSTALL fork, other collected refusals). Other host tests outside this filter cover them.
- Codec:73, and 77/83 NoCoverage (catch-block removal): equivalent. Stryker's block removal returns `default`, which is the same `null`.
- Codec:152 (`return true` when a property is missing): equivalent. A default `JsonElement` has ValueKind Undefined, which the gate already treats as unknown or non-array.
- Codec:20/22/31/37: the serializer `Options` initializer, outside the span. Duplicate-property refusal (31) is held by AuthorizationWriteStageTests (`TryDecode(duplicated)` is null), which is outside this filter.
- Verifier:42 (`ThrowIfNull(trustStore)`): pre-existing guard, outside the span.

## New kill tests (each showed red against its manual mutant before counting)

`PackContentReferenceDeriverTests` (7 facts), `PackContentKindAdmissionTests.Unknown_Kind_Shapes_Name_Their_Pointer` (3 rows) and `.Kind_Gate_Is_Case_Insensitive`, `RestrictingDefinitionKindAdmissionTests.Install_names_an_unknown_content_kind_before_store_or_projector`, and the CHECK leg in `PackContentReferenceRouteTests.Install_rederives_edges_and_refuses_an_undeclared_reference`. Manual reds: B1 (codec `||`) and B2 (codec `_ => true`) failed `Unknown_Kind_Shapes_Name_Their_Pointer`; B3 (case-sensitive) failed `Kind_Gate_Is_Case_Insensitive`; B4 (CHECK drop) failed `Install_rederives...`; B5 (sibling `continue`) failed `Sibling_reference...`; B6 (verifier always kind_unknown) failed `Install_names_an_unknown_content_kind...`. Every mutant was restored.

## Scores (scoped files, final runs)

| File | Killed | Timeout | Survived | NoCoverage | Score |
|---|---:|---:|---:|---:|---:|
| PackContentReferenceDeriver.cs | 26 | 0 | 2 | 2 | 86.67 |
| PackFileCodec.cs | 17 | 5 | 4 | 2 | 78.57 |
| PackVerifier.cs | 12 | 0 | 2 | 3 | 70.59 (103 CompileError, pre-existing `VerifyFormBindings` Safe Mode) |
| PackInstaller.cs | 104 | 0 | 112 | 273 | 21.27 (the whole file; only the spans above are claimed) |

## Raw reports

The four JSON reports (about 10 MB each) embed every host test source, so they are not tracked. SHA-256:

- deriver `9858D39EA33583AFA31D3089F7322F8BA62C47459C9579C94FE400E44ED45E30`
- installer `FA26967C50EE69B5650E10ACD103A70CEE1AF7C32832744DFD30E37388A0312C`
- codec `36B1ED237555C46AE4AFBAB5FA1E733E30A8AA97559E4C02D14EE6DB2A0CBF15`
- verifier `9063950F40B002AE182941385418AE7A45454BB7950744501181BC7C5D6F23E2`
