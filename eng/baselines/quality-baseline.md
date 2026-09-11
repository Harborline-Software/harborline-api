# Quality baseline identity re-pin

2026-09-11 — The baseline was re-pinned from 2,363 rows with 744 broad analyzer
fingerprints to 2,352 distinct identities. The normalizer now carries its stable
primary location and MSBuild project-output identity; `quality-step.mjs` persists
their combined digest as the baseline fingerprint. Eleven rows described the same
rule, source location, and project identity and were honestly de-duplicated.

The migration was produced by `node eng/repin-quality-baseline.mjs` after the
Release analysis build could not start in this fresh worktree because its `obj`
asset files are absent. Future `node eng/quality-step.mjs --write-baseline <file>`
uses the same identity writer directly.
