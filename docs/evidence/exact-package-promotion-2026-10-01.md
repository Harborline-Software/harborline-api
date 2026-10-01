# Exact package consumption and promotion â€” 2026-10-01

Scope: NuGet package staging and publication in `eng/verify-packages.sh` and `.github/workflows/packages.yml`.

The three shipped projects pack once. Every event, including a tag push, then executes the unchanged package consumer from a fresh temporary directory with a fresh global-packages and HTTP cache. NuGet source mapping assigns the exact shipped IDs to the staged feed. After restore, the archives in the isolated cache must hash identically to the staged archives; the consumer's existing 404 assertion must then pass. Only success writes `consumer-proof.json`.

The distribution manifest binds package hashes and the consumer-proof hash to repository, checkout SHA, version, workflow, run ID and run attempt. The publication job verifies it after downloading the artifact, before the existing authority checks and registry pushes. Both jobs retain the same staged files; publication does not repack.

## Evidence

- Eight guard tests pass: missing/wrong-version packages, restored-byte substitution, staged-byte tampering, restore/run failure, source/workflow/run/attempt mismatch, altered manifest and copied artifact bundle.
- `python eng/tests/package-proof-nuget-probe.py` passes against real NuGet. It first populates an ambient cache with synthetic archives bearing the same IDs/version but different bytes. The isolated consumer restores the staged archives instead. This probe measures cache selection; its minimal consumer is separate from the shipped assertion fixture.
- Actual `bash eng/verify-packages.sh` passed for version `0.1.0-preview.exactproof.20261001` against base `f087710b8d838c577b704a3b059fa6b11dfb3343`, with the unchanged shipped consumer assertion.
- Actual package SHA-256: Contracts `5d13a298e73f2f0fd90ed740d97c9113b03213d8263e5d6b4823011c4fa5f0b3`; Client `510cddf5110fbb304cee8fa7cec9f676541dd71dd297549c23fee402f74f7210`; Testing `8a0b7d56d6215f7a05ce0bdc99c9f978008c2a8a26c329f8693430da6ad4bfe6`.
- Oracle: the required equality between staged and restored bytes; literal package IDs/version fixtures; the unchanged shipped consumer assertion. No mutation score is claimed.

## Limits

This is same-run integrity evidence, not a signature or proof against a compromised workflow. API's npm contracts tarball is checked for expected name/version and bound by hash across artifact transfer, but is not executed by a tarball consumer. Existing npm tests run against source, so they do not supply that evidence. A tarball consumer would require additional coverage and is outside this NuGet byte-proof fix.

No registry push, release, tag, signing permission, credential or setting change was performed. T-705's release mechanism and T-670's published seed referent remain separate. Full host suites and hosted artifact transfer are not claimed as locally executed.

Existing `--skip-duplicate` behavior is preserved. This check binds the bytes supplied to the push command; it does not verify bytes already stored in a registry when a duplicate version is skipped.

Existing `--skip-duplicate` behavior is preserved. This check binds the bytes supplied to the push command; it does not verify bytes already stored in a registry when a duplicate version is skipped.
