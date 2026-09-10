#!/usr/bin/env bash
# Local verification for harborline-api — the replacement for GitHub Actions.
#
# Actions is switched off here (see the ACTIONS_ENABLED block at the top of
# .github/workflows/packages.yml): the org is on the free plan, private-repo minutes ran out on
# 2026-08-24, and until these repositories are public the workflows only produce red checks that
# never ran. This script runs what those workflows ran, in the same order, and on success records a
# receipt that .githooks/pre-push requires.
#
# The step ids below MUST stay in sync with requiredStepIds in eng/verify-receipt.mjs. The receipt
# recorder refuses a receipt that is missing any of them, so deleting a step here fails loudly at
# the end rather than quietly narrowing what the hook accepts.
#
# Not covered, deliberately: the `publish` job. Publishing is a release action, not a verification,
# and it stays gated on a tag or an explicit dispatch.
set -uo pipefail

root=$(git rev-parse --show-toplevel)
cd "$root"
# shellcheck source=gate-lock.sh
source "$root/eng/gate-lock.sh"
gate_lock_acquire "eng/verify.sh"

# Wire the hooks path here, not only in the README. core.hooksPath is LOCAL config and git skips a
# missing hooks path WITHOUT an error, so a fresh clone enforces nothing and does not say so — and
# git cannot fix that: it deliberately never clones hooks or local config. What it CAN do is make
# sure that anyone who has demonstrated intent to verify is wired from then on. Idempotent.
if [ "$(git config core.hooksPath || true)" != ".githooks" ]; then
  git config core.hooksPath .githooks
  echo "wired core.hooksPath -> .githooks (pre-push will now require a receipt)"
fi

passed=()
step() {
  local id=$1; shift
  printf '\n\033[1m── %s\033[0m\n' "$id"
  if "$@"; then
    passed+=("$id")
  else
    printf '\n\033[31mFAILED: %s\033[0m\n' "$id" >&2
    printf '  command: %s\n' "$*" >&2
    printf '\n  No receipt written. Fix this and re-run: bash eng/verify.sh\n' >&2
    exit 1
  fi
}

# Prerequisites the CI runner installed with setup-* actions. Checked up front rather than failing
# three steps in, because "cargo: not found" halfway through a long run reads like a real failure.
for tool in dotnet node cargo; do
  command -v "$tool" >/dev/null || { echo "required tool not on PATH: $tool" >&2; exit 1; }
done

started=$SECONDS

# Fast structural gate first: it is a git-grep and costs a second, and there is no reason to spend
# the .NET suite before reporting a name that should not be in the tree.
step boundaries              bash eng/verify-boundaries.sh

# Ticket 260 slice 28: the retired-family spelling check, run from harborline-control's checker
# against this checkout only (eng/identity-r3-scan.sh). Blocking since ticket 267 took the count to zero;
# a non-zero count fails the gate and lists the offending lines.
step identity-r3             bash eng/identity-r3-scan.sh

# protocol-lane-conformance
step codegen-check           node tooling/harborline-contract-codegen/generate.mjs --check
step codegen-guard-suite     node tooling/harborline-contract-codegen/run-tests.mjs
step contracts-typescript    bash -c 'cd packages/contracts && pnpm install --frozen-lockfile && pnpm test'
step contracts-csharp        dotnet test packages/contracts/tests/Harborline.Contracts.Tests.csproj -c Release
step localfirst-csharp       dotnet test packages/foundation-localfirst/tests/Harborline.Foundation.LocalFirst.Tests.csproj -c Release

# Ticket 193: the shared cross-tier conformance corpus, executed. Its own step id rather than a ride
# inside exact-clone, because the corpus is what backs the package description's parity claim and a
# failure in it means "the two rule tiers disagree" — a different verdict from "the host suite moved",
# and it should be readable as such from the step name alone.
step rule-engine-conformance dotnet test packages/foundation-rule-engine/tests/Harborline.Foundation.RuleEngine.Tests.csproj -c Release
step contracts-rust          cargo test --manifest-path packages/contracts/rust/Cargo.toml

# operator-cli-headless
step operator-cli-headless   dotnet test apps/local-node-host/tests/tests.csproj -c Release \
                               --filter FullyQualifiedName~OperatorCliHeadlessEndToEndTests

# Ticket 359: the install path, not the correctness path. operator-cli-headless above proves the CLI
# drives a node composed IN PROCESS from this checkout; this step proves the same surface on a
# PUBLISHED artefact started from a clean directory with no checkout and no SDK beside it -- the
# thing an operator on a clean machine actually has. Expensive (a self-contained publish), so it runs
# after the cheap structural steps and before the clean-clone suite.
step install-artefact        bash eng/verify-install-artefact.sh

# build-and-test, but through the clean-clone gate rather than a bare `dotnet test`. That matters:
# the host suite has permitted failures recorded in eng/baselines/host-test-baseline.json, so a bare
# run is red by design and a gate built on it would be ignored within a week. run-exact-clone.mjs
# takes its verdict from baseline comparison, not exit code, and it builds from a clone of HEAD so
# anything missing from the commit fails instead of being supplied by this working tree.
case "$(uname -s)" in
  Darwin) host_baseline=eng/baselines/host-test-baseline.macos.json ;;
  Linux)  host_baseline=eng/baselines/host-test-baseline.ubuntu.json ;;
  *)      host_baseline=eng/baselines/host-test-baseline.json ;;
esac
step exact-clone             node eng/run-exact-clone.mjs --host-baseline "$host_baseline"

# Ticket 339: analysis is always run from the pinned checkout.  It records neutral/evaluate evidence
# even before tickets 337 and 340 produce Cobertura and SARIF artifacts.
step quality                 node eng/quality-step.mjs

# Ticket 370: a branch must see a newly introduced quality finding during verification, rather than
# after the merge tree is assembled in land.sh. quality_baseline_gate writes one disposable candidate
# because the evidence-only quality step above does not retain one.
step quality-baseline        bash -c 'source eng/quality-baseline-landing.sh; quality_baseline_gate "$PWD"'

# pack-consume
step packages                bash eng/verify-packages.sh

printf '\n\033[32mAll %d steps passed in %dm%02ds\033[0m\n' "${#passed[@]}" "$(((SECONDS-started)/60))" "$(((SECONDS-started)%60))"
node eng/verify-receipt.mjs --record "${passed[@]}" --host-baseline "$host_baseline"
