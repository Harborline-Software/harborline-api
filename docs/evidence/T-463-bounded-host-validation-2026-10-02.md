# T-463 bounded native verifier and client HTTP validation

Tested API source: `2464f9ad93ce05c6f42623b3ce618f48bfb9ed21`, clean before and after execution.
Platform source: clean isolated worktree at the recorded pin
`9abd9e016a45fc8756ef259763f8079d56352e4e`. SDK `11.0.100-rc.1.26425.128`.

Quiet five-second CPU samples and immediate pre-launch checks found no external active build,
Stryker, VSTest or testhost. The parent-authorized sole heavy slot was used and released after the
process completed. No operational node, external Kernel worktree, whole gate or Stryker run touched.

## Dependency and build scope

Used the canonical `eng/build-local-feed.mjs --dry-run` plan against the exact pinned Platform source,
executing its 36 pack commands with only `-maxcpucount:6` changed to `-maxcpucount:1` for the slot.
All packages were freshly built into this API worktree's ignored `.feed`, version
`0.0.0-alpha.0.h43ec71c33440`. Checked package count, every producer's NuSpec identity/version and lib
assembly, and first-party dependency closure; canonical `--check-manifest` passed. Wrote the same
packed-version props used by the canonical builder. No cached API/test binaries used.

The local orchestration initially misread PowerShell's producer-property count after packing. Corrected
that local assertion and resumed verification of the already completed packages; did not repack them.
No repository source change was made for this orchestration correction.

## Executed command and result

```powershell
dotnet test apps/local-node-host/tests/tests.csproj -c Release --nologo `
  -nodeReuse:false -maxcpucount:1 `
  --filter 'FullyQualifiedName~VerificationRunnerTests|FullyQualifiedName~The_api_example_runs_proposal_save_and_read_against_the_isolated_http_host' `
  --logger 'trx;LogFileName=t463.trx' --results-directory ../t463-results
```

Native restore/build/test exit **0**: **16 passed, 0 failed, 0 skipped**, test duration 2 seconds.
This is a bounded test filter; the complete repository suite was not run. Analyzer warnings were
emitted during compilation; no warning policy was changed or failure waived.

Passed the two inherited PR332 file-authored asset/ledger theory cases, all three new asset cases,
and the actual client Proposal workflow test against the existing temporary Kestrel/SQLCipher host.
The latter spawned PowerShell, submitted the whole Form document, saved a version and read back the
proposal; asserted effective preservation and no release/effective/projection rows. It used the
fixture's permitting outer package gate, not an author-only production authorization certificate.

The widened-policy case observed the independently expected **Failed** candidate receipt while the
three total examples remained **Passed**. This is expected-red business-candidate evidence in a
passing regression. No broad pre-fix rebuild or mutation score is claimed. Existing invoice defect,
unsupported-case, environment-admission, route, unprepared-candidate and corpus tests also passed.

Raw evidence retained in the task workspace:

- `t463-results/t463.trx`, SHA256 `992d7ea6b99d4d169556cadd9ef3799e2f65961f792048cf44643f7977b3a4e6`.
- `t463-host-tests.log`, SHA256 `24956b31429384f4f2464e2be6049c478ec066771367b7179ec2d0a260979b52`.
- `t463-validation.exit`: `0`; `t463-feed-manifest.json`: canonical 36-producer manifest.

## Remaining acceptance boundaries

Proposal client live HTTP evidence is now measured, superseding earlier UNRUN notes for this source.
VerifyInstalled client-mode live invocation remains unrun (the real verify route itself passed its
existing host tests). Author-only routing regression remains transport-intercepted client evidence.
Saved proposal bytes still lack a verified-receipt binding through the governed release lifecycle;
Record references/refinement version admission, durable asset/journal state, port/fact consumption,
recovery and full release acceptance remain outside this increment. No readiness or merge action.

This evidence-only commit preserves the tested code/example/test files; parent review remains the
decision point for draft PR336.
