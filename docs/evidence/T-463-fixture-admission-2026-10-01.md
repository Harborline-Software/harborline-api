# T-463 bounded configuration fixture slice — 2026-10-01

Base: API `bf60fed771208e64c0155ecfedca1bbed022e2b2`; refreshed Control
`398525dd`; refreshed Platform `46dfb044`. No production interpreter semantics,
external Kernel PR 329/330, pipeline files or branches were edited.

The existing Platform catalog has one action, `records.create`. The API runner
executes production Platform rules and host authorization, but does not persist
records or execute ledger posting. The suite/receipt/runner already exist; this
slice extends their input corpus rather than creating another framework.

## Measured lightweight evidence

PowerShell production admission probe, using an **existing cached net10 build**:
`C:/Projects/Harborline/harborline-platform/projections/dotnet/blocks/hlp.blocks.builder-definitions/bin/Debug/net10.0`.
The build is not attributed to the refreshed Platform head or the API's pinned
Platform commit. This is explicitly cached-production-build admission evidence.

Producer `Harborline.Blocks.BuilderDefinitions.dll` SHA-256:
`91ef05290375ee3c90513624befeea56566d19baa1cd1d6c51474845e54c6034`.

- Required invocation: exit 0; 11 passing production-parser checks, no skips.
- Asset suite digest: `2378f85a3ee436c43848ccf9db4d0f01818c5cdd8b56a4e56c7e0b04365168c7`.
- Ledger intake suite digest: `5faf3f23f2a0320a1148a4fa57234438049a7c88b1529cfc8ff8d1d7b5c6bcc7`.
- Both suites admitted; each rejected empty assertions, wrong expected boolean
  type, duplicate case ID and malformed initial record JSON.
- `ledger.post` rejected with `verification-action-unknown`; no admitted suite.
- Opt-in `-FutureAcceptance`: exit 1, expected red for T-463/T-544's missing
  posting catalog/runner binding. This is refusal evidence, not execution proof.
- Contract generation `--check`: exit 0.
- Existing codegen guard suite: 32 pass, 0 fail, 0 skipped.

The suite's expected values are independently authored literals and arithmetic
properties inherited from the existing T-463 oracle, not computed by the code
under test. Empty/type/identity/fact/action refusal expectations are explicit
Platform admission contract values. These probes are not Stryker evidence.

## Required evidence still pending

The two new `VerificationRunnerTests.File_authored_candidates` theory cases have
**not run** on this source revision. They consume the candidate files unchanged
through the existing pack-content parser, real candidate preparation and real
runner. The Windows full mutation run 36874901580 occupies the machine, so this
lane performed no competing .NET restore/build/test/mutation run. The full
repository gate and exact-candidate checks remain required before readiness.

Future asset persistence, ledger posting, rejected/no-mutation durable deltas,
initial-record/port consumption, receipt observation extensions and full
proposal/verify/review/release/activate/recovery acceptance are unsupported here.
Owners remain existing T-463, T-549, T-544, T-975, T-460/T-461. No new tickets or
architecture decisions were created. The draft requires parent review; no ready,
auto-merge, activation, release or full acceptance certification is authorized.
