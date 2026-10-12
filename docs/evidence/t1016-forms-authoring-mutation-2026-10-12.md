# PR 389 Forms authoring mutation evidence (T-1016)

The hosted run tested source head `6d53882fe45b07299e77e6f9d511177907dab9ef` (tree `f2e814f7560c1a09aa3afac0f43305ed8e00abb2`) with the trusted workflow on main `61047f452a3bfc37107fbf202c370c25e1931679`. This note changes no production or test bytes.

Dispatch: `.github/workflows/candidate-mutation.yml`, `workflow_dispatch` on `main`, with these inputs:

```text
candidate_sha=6d53882fe45b07299e77e6f9d511177907dab9ef
test_project=apps/local-node-host/tests/tests.csproj
scope_preset=forms-authoring-files
```

The preset's effective configuration mutates `**/Health/FormDefinitionRoutes.cs` and `**/Health/FormsAuthoringC3Wire.cs` with `since` disabled. It uses the test-case filter `FullyQualifiedName~FormDefinitionRouteTests|FullyQualifiedName~RoleGateAdmissionTests.AdmissionMatrix_UsesAuthorityDerivedOwnerAndRefusesBeforeStore`, thresholds high 80 / low 60 / break 0, and reporters `json` and `progress`. The tool is `dotnet-stryker` 5.0.0 on .NET SDK `11.0.100-rc.1.26425.128`.

Run [38299569702](https://github.com/Harborline-Software/harborline-api/actions/runs/38299569702) (attempt 1) succeeded. The candidate, controller and platform checkouts (platform `9abd9e016a45fc8756ef259763f8079d56352e4e`) were clean and unchanged before and after the run, and all 30 recorded commands exited 0. Stryker discovered 139 tests and completed its initial test run.

Raw report, run manifest and effective configuration are archived at commit `4347a2508f8c57bb95e8b9b6c306ce6197f73856` on `archive/pr389-forms-s4-6d53882f-20261012`, under `docs/evidence/raw/pr389-6d53882f/`, with a README.

- Report SHA-256 `f8916cee7f58c7177573b972693d9ad236c1414d86202362e208fa064d005e61`; size 14,955,615 bytes. This equals `rawReportSha256` in the manifest.
- Manifest SHA-256 `431541bb07c171e0aedea92a421c82966448ff170af4bad028bc01c96e9158f0`.
- Effective configuration SHA-256 `d382c6f066c72225ae628080f40363dd53a01d05e241e34b8b9dd3385700bafb`.
- The GitHub artifact `candidate-mutation-evidence` (ID 11771045998, 7,431,231 bytes) has digest `sha256:164d16a6ebd7272e41d78c7984cd5b4975ca56f761cca2aa57f487262d5a20ff`. The evidence zip inside it has SHA-256 `5d1bc33b6f40495d0b5614da12a195c5f431f6cb92f0a0684814e13ca6227eea`.

The report embeds the mutated source and the test manifest. Match mutations by file, location and replacement; IDs below identify this run only.

## Counts

| File | Mutants | Killed | Survived | NoCoverage | CompileError | Ignored | Timeout |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `FormDefinitionRoutes.cs` | 931 | 428 | 175 | 77 | 191 | 60 | 0 |
| `FormsAuthoringC3Wire.cs` | 118 | 93 | 7 | 4 | 8 | 6 | 0 |
| Both producer files | 1049 | 521 | 182 | 81 | 199 | 66 | 0 |

There are zero Timeouts. Timeout is a separate status and is not counted as Killed. The reported score is 66.45 (521 Killed of 784 Killed, Survived or NoCoverage mutants). It is not a gate here, because the run's break threshold is 0. The 199 CompileError rows are project-wide compile rollbacks, not tested mutants. Ignored mutants were not tested either.

The whole report has 3,338 mutants across 759 files. Outside the two producer files every mutant is Ignored or CompileError, so the Killed, Survived and NoCoverage totals above are the whole report's. This is not a survivor-free result or a replacement for required CI gates.

## Comparison with the earlier reference run

The earlier reference is a local container-lane report at `c2683c656d21b4f83a3b88c99086e44a73200fde` (SHA-256 `53c55b862e7875edb0438249cbb3b77b4ee0d78d5ed39a30c6e0992dd6ae98da`). It is not archived on GitHub. The producer files are byte-identical at c2683c65 and 6d53882f, and the source embedded in both reports is identical. Between the two heads, the branch gained a merge of main and one test-only change.

An independent review joined the 1049 producer mutants of both reports by file, mutator, replacement, start and end span, original span text and surrounding source lines. The result was 1049 unique matches, with 0 absent, 0 ambiguous, 0 status changes and 0 changes in covering tests. The counts above therefore equal the reference counts exactly.

Every producer mutant ID in this run is exactly 2 higher than in the reference, because the merge of main adds mutants earlier in Stryker's numbering. Joins by ID alone across these reports are wrong.

For 118 Killed non-target mutants the recorded set of killing tests differs from the reference while the status is unchanged. None of them is a target below or one of the dispositioned rows. Any future claim that relies on a specific killing test for one of those mutants must read it from this report.

## Targets

These are the 31 targets of the earlier reviewed target table plus two non-target kills credited earlier. Each is Killed in this run by exactly the tests that killed it in the reference report. The last column is the lead's disposition record before this run. Its credits are bound to the earlier reference reports; this run reproduces those kills on the final head.

| This run | Reference ID | Location | Mutation | Status | Recorded killing test (`FormDefinitionRouteTests`) | Record before this run |
| --- | --- | --- | --- | --- | --- | --- |
| 38485 | 38483 | FormDefinitionRoutes.cs:1673 | Conditional (true) mutation -> `(true?c.Inputs :null)` | Killed | `C3_Admissible_Empty_Model_Inputs_Are_Omitted_By_Real_Get` | credited |
| 38489 | 38487 | FormDefinitionRoutes.cs:1673 | Equality mutation -> `>= 0` | Killed | `C3_Admissible_Empty_Model_Inputs_Are_Omitted_By_Real_Get` | credited |
| 38490 | 38488 | FormDefinitionRoutes.cs:1675 | Conditional (true) mutation -> `(true?true :null)` | Killed | `C3_Async_Acknowledgment_And_Pii_Projection_Have_Literal_Values` | credited |
| 38491 | 38489 | FormDefinitionRoutes.cs:1675 | Conditional (false) mutation -> `(false?true :null)` | Killed | `C3_Async_Acknowledgment_And_Pii_Projection_Have_Literal_Values` | weak before hardening; credited on this report (see below) |
| 38492 | 38490 | FormDefinitionRoutes.cs:1675 | Boolean mutation -> `false` | Killed | `C3_Async_Acknowledgment_And_Pii_Projection_Have_Literal_Values` | credited |
| 38494 | 38492 | FormDefinitionRoutes.cs:1678 | String mutation -> `"Stryker was here!"` | Killed | `C3_Async_Check_Omitted_Values_Refuse_At_Publish` | credited |
| 38500 | 38498 | FormDefinitionRoutes.cs:1681 | String mutation -> `"Stryker was here!"` | Killed | `C3_Async_Check_Omitted_Values_Refuse_At_Publish` | credited |
| 38507 | 38505 | FormDefinitionRoutes.cs:1684 | Boolean mutation -> `true` | Killed | `C3_Omitted_Acknowledgment_Refuses_A_Sensitive_Aspect_Feed` | credited |
| 38514 | 38512 | FormDefinitionRoutes.cs:1734 | Conditional (true) mutation -> `(true?"Sensitive" :"None")` | Killed | `C3_Async_Acknowledgment_And_Pii_Projection_Have_Literal_Values` | credited |
| 38518 | 38516 | FormDefinitionRoutes.cs:1734 | String mutation -> `""` | Killed | `C3_Async_Acknowledgment_And_Pii_Projection_Have_Literal_Values` | credited |
| 38551 | 38549 | FormDefinitionRoutes.cs:1796 | String mutation -> `""` | Killed | `C3_Standing_Parse_Locations_Remain_Author` | credited |
| 38553 | 38551 | FormDefinitionRoutes.cs:1803 | String mutation -> `""` | Killed | `C3_Standing_Parse_Locations_Remain_Author` | credited |
| 38817 | 38815 | FormsAuthoringC3Wire.cs:49 | String mutation -> `""` | Killed | `C3_Async_Check_Omitted_Values_Refuse_At_Publish` (3 cases) | credited |
| 38827 | 38825 | FormsAuthoringC3Wire.cs:57 | String mutation -> `""` | Killed | `C3_Second_Section_Write_Role_Uses_Section_Index_Despite_Field_Name_Collision`, `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` (4 cases) | credited |
| 38833 | 38831 | FormsAuthoringC3Wire.cs:62 | Conditional (true) mutation -> `(true?"read" :"write")` | Killed | `C3_Second_Section_Write_Role_Uses_Section_Index_Despite_Field_Name_Collision`, `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` (3 cases) | credited |
| 38836 | 38834 | FormsAuthoringC3Wire.cs:62 | String mutation -> `""` | Killed | `C3_Second_Section_Write_Role_Uses_Section_Index_Despite_Field_Name_Collision`, `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` (3 cases) | credited |
| 38838 | 38836 | FormsAuthoringC3Wire.cs:63 | String mutation -> `""` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` | credited |
| 38839 | 38837 | FormsAuthoringC3Wire.cs:63 | String mutation -> `$""` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` | credited |
| 38841 | 38839 | FormsAuthoringC3Wire.cs:64 | String mutation -> `""` | Killed | `C3_Second_Section_Write_Role_Uses_Section_Index_Despite_Field_Name_Collision` | credited |
| 38849 | 38847 | FormsAuthoringC3Wire.cs:70 | Conditional (true) mutation -> `(true?field.ReadRoles :field.WriteRoles)` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` (2 cases) | credited |
| 38855 | 38853 | FormsAuthoringC3Wire.cs:72 | Conditional (true) mutation -> `(true?field.Aspects?.Access?.ReadRoles :field.Aspects?.Access?.WriteRoles)` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` | credited |
| 38856 | 38854 | FormsAuthoringC3Wire.cs:72 | Conditional (false) mutation -> `(false?field.Aspects?.Access?.ReadRoles :field.Aspects?.Access?.WriteRoles)` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` | credited |
| 38861 | 38859 | FormsAuthoringC3Wire.cs:73 | Equality mutation -> `>= 0` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` | credited |
| 38864 | 38862 | FormsAuthoringC3Wire.cs:73 | Equality mutation -> `>= 0` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` | credited |
| 38870 | 38868 | FormsAuthoringC3Wire.cs:75 | Equality mutation -> `>= 0` | Killed | `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations` | credited |
| 38884 | 38882 | FormsAuthoringC3Wire.cs:82 | PostIncrementExpression to PostDecrementExpression mutation -> `i--` | Killed | `C3_Second_Section_Write_Role_Uses_Section_Index_Despite_Field_Name_Collision` | killed-weak, not credited |
| 38888 | 38886 | FormsAuthoringC3Wire.cs:87 | String mutation -> `""` | Killed | `C3_Standing_Parse_Locations_Remain_Author` (2 cases) | credited |
| 38890 | 38888 | FormsAuthoringC3Wire.cs:88 | Negate expression -> `!(gate.StartsWith("sections.", StringComparison.Ordinal))` | Killed | `C3_Standing_Parse_Locations_Remain_Author` (2 cases) | credited |
| 38891 | 38889 | FormsAuthoringC3Wire.cs:88 | String mutation -> `""` | Killed | `C3_Standing_Parse_Locations_Remain_Author` | credited |
| 38892 | 38890 | FormsAuthoringC3Wire.cs:88 | String mutation -> `""` | Killed | `C3_Standing_Parse_Locations_Remain_Author` | credited |
| 38893 | 38891 | FormsAuthoringC3Wire.cs:89 | String mutation -> `""` | Killed | `C3_Standing_Parse_Locations_Remain_Author` | credited |
| 38496 | 38494 | FormDefinitionRoutes.cs:1679 | String mutation -> `"Stryker was here!"` | Killed | `C3_Async_Check_Omitted_Values_Refuse_At_Publish` | credited |
| 38548 | 38546 | FormDefinitionRoutes.cs:1793 | Null coalescing mutation (remove left) -> `[]` | Killed | `C3_Second_Section_Write_Role_Uses_Section_Index_Despite_Field_Name_Collision` | credited |

"Credited" in the last column means credited on the earlier reference reports. The exception is 38491, which is credited on this report only. Theory cases of one test method are counted in brackets.

## Acknowledgment presence hardening (this run's 38491)

The reference reports killed `(false?true :null)` at `FormDefinitionRoutes.cs:1675` (reference ID 38489) through a `KeyNotFoundException` from `GetProperty`, before any assertion compared a value. That kill was recorded as a weak oracle and not credited. Commit 6d53882f changes only `C3_Async_Acknowledgment_And_Pii_Projection_Have_Literal_Values`, +3/-1 in the same case. The test now asserts `checks[1].TryGetProperty("allowsSensitiveInputs", out var ackTrue)` and then `JsonValueKind.True`.

In this run the mutant is ID 38491, Killed by that test, and its line, replacement and killing test match the reference. ID 38489 in this run is a different mutant: the `>= 0` equality at line 1673, killed by `C3_Admissible_Empty_Model_Inputs_Are_Omitted_By_Real_Get`.

Source reading of the kill: under the mutant, `AsyncCheckDto.From` projects `AllowsSensitiveInputs = null`, and `WhenWritingNull` omits the member on GET. The PUT path does not use that projection, so the 200 status and the earlier assertions still hold. The first statement that fails is `Assert.True(checks[1].TryGetProperty("allowsSensitiveInputs", out var ackTrue))` at `FormDefinitionRouteTests.cs:358`, which is an assertion failure, not an exception. The companion mutation `true -> false` at line 1675 (this run's 38492) fails at the next line's `Assert.Equal(JsonValueKind.True, ...)`.

Limit: this reading comes from the source, not from raw test output. Stryker's JSON records no failure message for a Killed mutant, and the run artifact holds no per-mutant test output. The raw evidence alone cannot distinguish an assertion failure from an exception here. The independent review of this run found the oracle confirmed on that source reading. On that review the lead credited this kill as killed by test with a confirmed oracle. The credit is bounded to this run's raw report `f8916cee7f58c7177573b972693d9ad236c1414d86202362e208fa064d005e61` only, and it rests on a source reading rather than raw test output. A single-mutant local reproduction would supply the literal failure text if it is ever required.

## Crash-mediated kill (this run's 38884)

`i++ -> i--` at `FormsAuthoringC3Wire.cs:82` (reference ID 38882) is Killed here, as before, by `C3_Second_Section_Write_Role_Uses_Section_Index_Despite_Field_Name_Collision`. The kill is crash-mediated: the mutant either returns the original pointer or indexes `Sections[-1]` and throws, producing a 500 where the test expects the literal 422. No test-only assertion can observe it without that crash, so it stays killed-weak and not credited. Production code is unchanged.

## Survived and uncovered mutants

The lead's disposition record has 296 rows: every producer mutant that was Survived or NoCoverage in any earlier report, plus all targets. Each row joins to exactly one mutant in this run, and no row's status differs from the reference. In this run, 33 rows are Killed. Of these, 32 are credited kills: 31 credited on the earlier reference reports, and 38491 credited on this report. The 33rd is 38884, which stays killed-weak and is not credited. The other 263 rows are exactly this run's 182 Survived and 81 NoCoverage mutants. They all remain open:

| Open disposition | Rows | Survived | NoCoverage |
| --- | --- | --- | --- |
| Test gap | 204 | 140 | 64 |
| Proposed non-material by a source-only audit; not accepted | 35 | 27 | 8 |
| Proposed equivalent or unreachable by a source-only audit; unverified | 24 | 15 | 9 |

No mutant is accepted as equivalent. An independent review found that one class of the source-only audit's "equivalent" proposals was wrong, so the remaining 24 proposals are explicitly unverified.

That wrong class is the remove-left mutations `access.ReadRoles ?? [] -> []` at `FormDefinitionRoutes.cs:1785` and `access.WriteRoles ?? [] -> []` at line 1786 (this run's 38542 and 38544, both Survived). They would drop aspect-access roles on GET and on the restore path. They are open test gaps and the named follow-up test candidates: PUT a field or form aspect with `access.readRoles`/`writeRoles`, GET it, and assert the literal arrays. That test is not part of this PR.

## Focused validation of 6d53882f

Before this run, the local container lane ran the focused stages on the same head. It is a single serial lane, and these were raw-run results with an independent terminal review.

- With the same filter as the mutation run: 139 of 139 passed, and all 14 required stable test IDs were present.
- `C3_Write_And_Mixed_Gates_Preserve_Their_Actual_Locations`: 5 of 5 passed.
- `C3_Author_Metadata_Escapes_Pointers_And_Preserves_History`: the baseline passed 6 of 6.
- Five planned whole-file source direction controls on the two producer files then produced failed/passed counts of 4/2, 6/0, 6/0, 4/2 and 4/2. These are exactly the planned counts. Every failure was an assertion, and each original file was restored.

The direction controls are supplemental evidence and carry no mutation or kill credit. Hosted required checks on 6d53882f passed.

## Limits and disclosures

- The workflow ran from main `61047f45`. The candidate's last merge of main is `6ba5c1ed369684d1fce829a2008fd43d7d3127bf`. The workflow, the executor scripts, `global.json`, the tool manifest, `nuget.config` and the Stryker configurations are byte-identical between the two. The later main commit changes none of the four #389 source and test paths. The landed tree should still be checked for equal blobs on those paths.
- This is the first successful hosted run of the `forms-authoring-files` preset. The earlier run 37738011623 failed at the tool-version probe before any baseline or mutant ran.
- The artifact digest above is GitHub's value for the uploaded artifact zip. The archived files come from the evidence zip inside it, whose hash matches its sidecar. The outer artifact zip was not re-hashed locally. The hosted artifact expires on 2027-01-10; the archive branch holds the raw report independently.
- The earlier reference reports (at 040528d1, 28717577 and c2683c65) are local container-lane reports and are not archived on GitHub.
- No survivor clearance, equivalence acceptance, aggregate mutation qualification, threshold, App-consumer conformance, aggregate C3 or T-1016 closure is claimed. No gate, baseline, deadline, workflow or owner policy was changed.
