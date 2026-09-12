# Quality baseline identity re-pin

2026-09-12 (re-pin to 4e179067) — Routine drift re-pin, not an identity change.
The committed baseline was pinned at `ac088ab3` (2,350 rows). Six landings later
the PR gate reported `38 new, 41 resolved` against it and main's own verify went
red on the same comparison, which blocks every PR: a red main publishes no
`quality-findings` artifact for the next PR to use as its merge-base, so each PR
falls back to this committed file and inherits the same red.

The churn is line-shift refingerprinting, not defects. The paths named as new
(`PackSeedProjector.cs`, `PackInstallRoutes.cs`, `PackInstaller.cs`) are files the
reporting PR never opened, and 38-against-41 with a net of -3 rows is the
signature of identities moving, not of findings appearing.

Re-pinned from the artifact main's own verify published at `4e179067`
(`quality-findings-4e1790677df06f94386ee9ff2909a641b1d34bb5`, run 34707698875):
2,350 -> 2,347 rows, both engines `ok`. That artifact exists because ticket 405
made the upload `always()`, so a red main still publishes one.

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
