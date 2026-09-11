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

2026-09-11 (second re-pin, same day) — `eng/repin-quality-baseline.mjs` derived the
project identity from the nearest tracked `.csproj`; for 38 rows (36 under
apps/local-node-host, 3 in Program.cs, 1 under packages/foundation/Assets, all one
project identity at gate time) that differed from the identity the Roslyn SARIF
carries, so the PR gate reported them as 38 new and 38 resolved with the row count
unchanged at 2,352. This file is now the tool's own product: the 88 normalised SARIFs
from the macOS verify run for PR 92 (run 34617871284) were fed to
`node eng/quality-step.mjs --write-baseline eng/baselines/quality-baseline.json`.
No finding was added or removed; only those 38 identities moved to the gate's.
