# Same-run Ubuntu Platform feed trial

This is an experimental packages workflow optimization, not a completed performance claim or a replacement for unique API consumer evidence. Related control context: T-104 (proof reuse does not materialize artifacts), T-314 (API pinned Platform feed), T-250 and T-273 (other feed identity tooling).

## Scope and boundary

Only packages.yml's protocol-lane-conformance, operator-cli-headless and pack-consume jobs consume a shared feed. A dedicated Ubuntu producer in that workflow run builds the selected Platform pin once. The pin supplies the producer inventory dynamically, so moving the pin does not require a hardcoded package count in the new tool.

The producer uploads one direct JSON bundle. Its exact artifact ID and SHA-256 digest travel through job outputs; the producer checks upload-artifact's digest against its independently computed bundle digest. Consumers download only that ID in the current run, fail transport digest mismatches, recompute the bundle digest against the producer output, and independently validate the feed before materializing it.

Identity includes Platform repository/commit/tree, selected producers and project graph, content-derived package version, actual SDK and Node versions, Linux architecture and runner image, global.json digest, build/verifier/composite source digests and Release pack configuration. This conservatively binds the entire tracked Platform tree rather than underdeclaring build inputs. Restored contents are exactly the pinned nupkgs plus packed-version.props. Nuspec identities/versions, assemblies, every dependency group's first-party ID/version closure, props and each file's bytes are checked. Unfamiliar first-party version range forms refuse reuse.

The transfer manifest stays outside .feed. No Platform or API bin/obj, global package cache, test results, receipt or credentials are shared. Each API consumer still runs its original commands. Exact-clone orchestration and verify.yml are untouched. Different OS, architecture, SDK or runner image refuse reuse; there is no cross-run cache or cross-OS reuse.

Tags and publication-enabled dispatches select the original fresh-pack path; publication code and permissions are unchanged. Draft/stacked guards are retained. A failed producer prevents its consumers from being accepted. An unavailable download with zero transferred files logs a warning, packs the checked-out pin and validates the new complete feed. Failed downloads that left any files, identity mismatches, digest mismatches and invalid contents refuse rather than hiding tampering behind a fallback.

## Validation recorded before hosted measurement

- 15 new light fixture/policy tests pass: identity perturbations, dynamic pin inventory, complete feed, tampering, missing/extra/duplicate files, package versions/assemblies, first-party dependency ranges, archive paths, destination isolation, event admission, publication guard and actual Bash fallback refusal.
- Existing platform-feed fixture suite: 7/7 pass, including duplicate producers and nested/sibling MSBuild evaluation (no compilation).
- Independent real-feed compatibility check: 34 packages, 83 dependency entries at version 0.0.0-alpha.0.h81d056d4c666, using the completed local main-pin feed. This is metadata validation, not a new pack, restore or clean consumer proof.
- Both edited YAML files parse; actionlint 1.7.12 passes packages.yml (shellcheck/pyflakes disabled). git diff --check passes.
- Fixture expectations use literal two-producer package metadata, literal hash perturbations and the existing event/publication contract; no production output supplies its own expected oracle. No mutation coverage or full API gate is claimed. API #322 retains the heavy Windows validation slot.

## Before measurement and required comparison

Same API main commit f087710b8d838c577b704a3b059fa6b11dfb3343: packages run 36802090007, verify run 36802090024, 2026-10-01. The three packages feed composites took 138s / 144s / 185s (467s SUM); their jobs took 218s / 257s / 235s (710s SUM), with all three starting together. Packages run creation to last consumer completion was 261s. Five required outer verify feed composites contributed another 793s, giving 1260s SUM across eight composites. Composite timings include setup, checkout and feed fixtures, not just pack CPU.

A producer dependency adds queue/startup, upload/download and repeated consumer setup. Saving SUM compute does not guarantee a shorter critical path. Measure a completed nonpublishing branch dispatch (publish=false) against the baseline, report producer plus all consumer durations and run creation-to-last-consumer completion, and disclose event/environment/cache differences. Verify's approximately 45-minute Windows critical path is outside this experiment. Do not recommend landing if wall time worsens without convincing shared-pool compute benefit.

Hosted before/after measurements remain pending at this initial draft checkpoint. No publication, merge, readiness change or runner-concurrency change is authorized by this experiment.
