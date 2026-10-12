# PR 389 hosted Forms mutation evidence at 6d53882f

This directory archives the raw evidence of one hosted mutation run for API PR #389 (T-1016 Forms authoring). It changes no production or test bytes. The files are copied unchanged from the run's artifact.

## Run

| | |
|---|---|
| Run | [38299569702](https://github.com/Harborline-Software/harborline-api/actions/runs/38299569702), attempt 1, conclusion success |
| Workflow | `.github/workflows/candidate-mutation.yml`, `workflow_dispatch` on `main` at `61047f452a3bfc37107fbf202c370c25e1931679` (tree `bcedf6d9c305b2d148f97ec0179f802efeefa924`) |
| Inputs | `candidate_sha=6d53882fe45b07299e77e6f9d511177907dab9ef`, `test_project=apps/local-node-host/tests/tests.csproj`, `scope_preset=forms-authoring-files` |
| Candidate | `6d53882fe45b07299e77e6f9d511177907dab9ef`, tree `f2e814f7560c1a09aa3afac0f43305ed8e00abb2`, identical before and after the run |
| Platform checkout | `9abd9e016a45fc8756ef259763f8079d56352e4e`, tree `3ffda5e464a64436646cf7d28ce6569094ecb85c` |
| Tool | `dotnet-stryker` 5.0.0, .NET SDK `11.0.100-rc.1.26425.128` |
| Artifact | `candidate-mutation-evidence`, ID 11771045998, 7,431,231 bytes. GitHub digest `sha256:164d16a6ebd7272e41d78c7984cd5b4975ca56f761cca2aa57f487262d5a20ff` |
| Evidence zip in the artifact | `candidate-mutation-evidence.zip`, 22,489,770 bytes, SHA-256 `5d1bc33b6f40495d0b5614da12a195c5f431f6cb92f0a0684814e13ca6227eea`, equal to its `.zip.sha256` sidecar |

The GitHub digest is the value the run log and the artifacts API report for the uploaded artifact. The downloaded copy that these files come from is the evidence zip and its extracted directory. The outer artifact zip was not re-hashed locally.

## Files

| File | Bytes | SHA-256 |
|---|---|---|
| `mutation-report.json` (the artifact's `raw/reports/mutation-report.json`) | 14,955,615 | `f8916cee7f58c7177573b972693d9ad236c1414d86202362e208fa064d005e61` |
| `manifest.json` | 6,219,239 | `431541bb07c171e0aedea92a421c82966448ff170af4bad028bc01c96e9158f0` |
| `effective-config.json` | 543 | `d382c6f066c72225ae628080f40363dd53a01d05e241e34b8b9dd3385700bafb` |

The report hash equals `rawReportSha256` in `manifest.json`. The evidence zip holds 64 files. The manifest's `artifactHashes` lists the SHA-256 of the other 63, including the command stdout/stderr files that are not archived here. The manifest records `status: raw-complete-pending-control-review` and `qualificationClaimed: false`.

The effective configuration mutates `**/Health/FormDefinitionRoutes.cs` and `**/Health/FormsAuthoringC3Wire.cs` with `since` disabled, test-case filter `FullyQualifiedName~FormDefinitionRouteTests|FullyQualifiedName~RoleGateAdmissionTests.AdmissionMatrix_UsesAuthorityDerivedOwnerAndRefusesBeforeStore`, thresholds high 80 / low 60 / break 0, and reporters `json` and `progress`.

## Reading the report

The report embeds the mutated source and the 139 discovered tests. Match mutations by file, location and replacement. Mutant IDs identify this run only: every mutant in the two producer files has an ID exactly 2 higher than in the earlier reference run at `c2683c656d21b4f83a3b88c99086e44a73200fde`. Timeout is a separate status from Killed; this run has none. The interpretation of these results is in `docs/evidence/t1016-forms-authoring-mutation-2026-10-12.md` on the PR branch.
