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

2026-09-11 (third re-pin) — after api #87 and #93 landed, the merge group for PR 92
reported 7 new / 8 resolved: those landings moved lines in apps/local-node-host
(Program.cs, RosterSyncBootstrapHostedService.cs and neighbours), and the identity
carries the line, so unchanged findings below a moved line read as new. Re-pinned by
`quality-step.mjs --write-baseline` from the merge-group run 34622462871's SARIFs
(tree main bab2770b + this branch): 2,351 rows. The identity's line sensitivity is a
known cost of the exact identity; the follow-up (control ticket) is a baseline computed
for the merge-base by the main push run, so a landing that moves lines does not red
the next PR.

2026-09-11 (340 s2) — the ESLint engine joins: packages/contracts runs
@typescript-eslint/no-floating-promises (HLQ.TS.1000) with the SARIF formatter into
artifacts/quality/eslint/contracts.sarif, normalised with the same three partials and
engine name `eslint`. Its findings on the current tree are not in this file yet; the
first gate run of the branch reports them and the re-pin from that run's artifact adds
them (the lane's sandbox could not install the formatter).
