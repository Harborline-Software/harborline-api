# Same-run Ubuntu Platform feed trial

This is an experimental packages workflow optimization, not a completed performance claim or a replacement for unique API consumer evidence. Related control context: T-104 (proof reuse does not materialize artifacts), T-314 (API pinned Platform feed), T-250 and T-273 (other feed identity tooling).

## Scope and boundary

Only packages.yml's protocol-lane-conformance, operator-cli-headless and pack-consume jobs consume a shared feed. A dedicated Ubuntu producer in that workflow run builds the selected Platform pin once. The pin supplies the producer inventory dynamically, so moving the pin does not require a hardcoded package count in the new tool.

The producer uploads one direct JSON bundle. Its exact artifact ID and SHA-256 digest travel through job outputs; the producer checks upload-artifact's digest against its independently computed bundle digest. Consumers download only that ID in the current run, fail transport digest mismatches, recompute the bundle digest against the producer output, and independently validate the feed before materializing it.

Identity includes Platform repository/commit/tree, selected producers and project graph, content-derived package version, actual SDK and Node versions, Linux architecture and runner image, global.json digest, build/verifier/composite source digests and Release pack configuration. This conservatively binds the entire tracked Platform tree rather than underdeclaring build inputs. Restored contents are exactly the pinned nupkgs plus packed-version.props. Nuspec identities/versions, assemblies, every dependency group's first-party ID/version closure, props and each file's bytes are checked. Unfamiliar first-party version range forms refuse reuse.

The transfer manifest stays outside .feed. No Platform or API bin/obj, global package cache, test results, receipt or credentials are shared. Each API consumer still runs its original commands. Exact-clone orchestration and verify.yml are untouched. Different OS, architecture, SDK or runner image refuse reuse; there is no cross-run cache or cross-OS reuse. After verifying every transferred byte and its closure, a known environment-only key miss exits 3 and rebuilds/validates from the consumer's pinned source. Source/producer/graph/script mismatches and corruption remain fatal. Windows reuse is prohibited at identity construction.

Tags and publication-enabled dispatches select the original fresh-pack path; publication code and permissions are unchanged. Draft/stacked guards are retained. A failed producer prevents its consumers from being accepted. An unavailable download with zero transferred files logs a warning, packs the checked-out pin and validates the new complete feed. Failed downloads that left any files, identity mismatches, digest mismatches and invalid contents refuse rather than hiding tampering behind a fallback.

## Validation recorded before hosted measurement

- 17 new light fixture/policy tests pass: identity perturbations, dynamic pin inventory, complete feed, tampering, missing/extra/duplicate files, package versions/assemblies, first-party dependency ranges, archive paths, destination isolation, event admission, publication guard and actual Bash fallback refusal. Verified environment misses rebuild; corrupt data never takes that path, including when the environment differs.
- Existing platform-feed fixture suite: 7/7 pass, including duplicate producers and nested/sibling MSBuild evaluation (no compilation).
- Independent real-feed compatibility check: 34 packages, 83 dependency entries at version 0.0.0-alpha.0.h81d056d4c666, using the completed local main-pin feed. This is metadata validation, not a new pack, restore or clean consumer proof.
- Both edited YAML files parse; actionlint 1.7.12 passes packages.yml (shellcheck/pyflakes disabled). git diff --check passes.
- Fixture expectations use literal two-producer package metadata, literal hash perturbations and the existing event/publication contract; no production output supplies its own expected oracle. No mutation coverage or full API gate is claimed. API #322 retains the heavy Windows validation slot.

## Before measurement and required comparison

Same API main commit f087710b8d838c577b704a3b059fa6b11dfb3343: packages run 36802090007, verify run 36802090024, 2026-10-01. The three packages feed composites took 138s / 144s / 185s (467s SUM); their jobs took 218s / 257s / 235s (710s SUM), with all three starting together. Packages run creation to last consumer completion was 261s. Five required outer verify feed composites contributed another 793s, giving 1260s SUM across eight composites. Composite timings include setup, checkout and feed fixtures, not just pack CPU.

A producer dependency adds queue/startup, upload/download and repeated consumer setup. Saving SUM compute does not guarantee a shorter critical path. Measure a completed nonpublishing branch dispatch (publish=false) against the baseline, report producer plus all consumer durations and run creation-to-last-consumer completion, and disclose event/environment/cache differences. Verify's approximately 45-minute Windows critical path is outside this experiment. Do not recommend landing if wall time worsens without convincing shared-pool compute benefit.

No publication, merge, readiness change or runner-concurrency change is authorized by this experiment.

Initial hosted trial 36813191922 at 2bfedb22 exercised a real image-key refusal: the producer used Ubuntu image 20260920.314.1, while pack-consume received 20260927.320.1. The producer's 3,762,408-byte direct artifact (ID 11140896231; SHA-256 1223d746946c497b944c66f33ce50cb54cf4a3c06e917cc27114d209ab01b2f9) passed binding; two consumers restored it, and pack-consume refused the image mismatch. This failed run is not a completed performance comparison. The follow-up keeps exact image identity and adds the explicit verified-environment-miss fresh-build path rather than accepting mismatched bytes.

## Completed trial: do not land this version

[Baseline run 36802090007](https://github.com/Harborline-Software/harborline-api/actions/runs/36802090007) was a successful merge_group on main f087710. [Trial run 36814078999](https://github.com/Harborline-Software/harborline-api/actions/runs/36814078999) successfully completed on code commit c334f1bd3fc522ee39a49b6c1cccf6299a8204bb, via workflow_dispatch on refs/heads/pipeline/api-same-run-feed-trial-20261001 with publish=false. Its publication job was skipped: the ref was not a tag and inputs.publish was false. Protocol TypeScript/C#/Rust, operator CLI headless and clean package consumer verification retained their original commands and passed. No API results or receipts were restored.

| Metric, seconds | Baseline | Trial |
|---|---:|---:|
| Producer job | absent | 207 |
| Operator job | 218 | 238 |
| Pack-consume job | 257 | 117 |
| Protocol job | 235 | 273 |
| SUM producer plus consumer job elapsed | 710 | 835 |
| Run creation to last consumer completion | 261 | 487 |
| First relevant job start to last consumer completion | 257 | 483 |
| SUM relevant feed composites, including producer and fallback | 467 | 552 |

The producer finished at 04:16:41Z after starting at 04:13:14Z. Consumers started at 04:16:43/44Z. Last consumer completed at 04:21:17Z, from a run created at 04:13:10Z. This explicitly includes producer serialization, its startup and transfer overhead. SUM job elapsed increased 125s (17.6%); creation-to-last-consumer wall time increased 226s (86.6%). These are measured elapsed runner occupancy and wall time, not CPU utilization or billed rounded minutes.

Verified artifact provenance: ID 11141035730, name platform-feed-bundle.json, 3,762,416 bytes, SHA-256 702f77bf6bd457d1fddb87e5cdb83347a484435222f2fed43c6cc6e270aa0764. Producer output BUNDLE_DIGEST and upload ARTIFACT_DIGEST agreed. Producer and pack-consume ran Ubuntu image 20260920.314.1; pack-consume logged a complete 34-package restore. Operator and protocol ran 20260927.320.1 and logged imageVersion-only key misses, refused artifact reuse, freshly packed the checked-out pin and validated the new feed. Their composite durations were 152s and 200s, versus 8s for the matching pack consumer and 192s for the producer composite.

Both runs used the same pinned Platform commit and package source version. API production/test sources are unchanged by the trial; its code changes are pipeline/helper fixtures. Events, Git commit metadata, runner instances/images, preinstalled dependencies and transient network/cache conditions differ. This is one completed trial and one historical baseline, not a controlled repeated statistical estimate. It nevertheless demonstrates no shared-pool benefit in the observed current image rollout: both SUM job elapsed and critical path worsened.

Recommendation: retain API #324 as an experimental draft and do not land this version. Exact image partitioning fragmented the same-run feed while the producer serialized the consumers. Do not drop that check merely to get a hit; any future narrower identity needs an audit of the actual SDK/reference-pack/build inputs or a deliberately controlled producer environment. Cross-run reuse and cross-OS reuse remain out of scope. No further expensive run is warranted for this implementation after the observed regression.
