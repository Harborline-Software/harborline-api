#!/usr/bin/env bash
# Ticket 340's positive control: the typed ESLint engine must report a real
# floating promise in SARIF, rather than merely load a flat config successfully.
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
contracts="$repo_root/packages/contracts"
canary="$contracts/src/quality-floating-promise-canary.ts"
# Not mktemp: under Git Bash on Windows /tmp is a path node cannot open (ticket 340).
scratch="$repo_root/artifacts/quality/eslint-canary.$$"; mkdir -p "$scratch"
sarif="$scratch/contracts-canary.sarif"
cleanup() {
  node -e 'require("node:fs").rmSync(process.argv[1], {force: true})' "$canary"
  rm -rf "$scratch"
}
trap cleanup EXIT

node -e 'require("node:fs").rmSync(process.argv[1], {force: true})' "$sarif"
printf 'async function plantedFloatingPromise(): Promise<void> {
  Promise.resolve();
}
void plantedFloatingPromise;
' > "$canary"

# The boundaries step runs before the exact clone installs the package; install once, frozen, when eslint is absent.
if [[ ! -x "$contracts/node_modules/.bin/eslint" ]]; then
  ( cd "$contracts" && pnpm install --frozen-lockfile --ignore-scripts >/dev/null ) || { echo "eslint canary FAIL — pnpm install --frozen-lockfile failed in packages/contracts" >&2; exit 1; }
fi

set +e
( cd "$contracts" && pnpm exec eslint "src/quality-floating-promise-canary.ts" --format @microsoft/eslint-formatter-sarif --output-file "$sarif" )
status=$?
set -e
if [[ $status -eq 0 ]] || [[ ! -f "$sarif" ]]; then
  echo "eslint canary FAIL — planted floating promise did not produce SARIF evidence" >&2
  exit 1
fi

node "$repo_root/eng/normalize-eslint-sarif.mjs" --repo-root "$repo_root" contracts "$sarif"
node -e '
const {readFileSync} = require("node:fs");
const sarif = JSON.parse(readFileSync(process.argv[1], "utf8"));
const results = sarif.runs.flatMap(run => run.results ?? []);
const canary = results.find(result => result.ruleId === "@typescript-eslint/no-floating-promises");
const valid = sarif.version === "2.1.0" && sarif.runs.every(run => run.tool?.driver?.name === "eslint")
  && canary?.partialFingerprints?.["harborline/primary-location/v1"]
  && canary?.partialFingerprints?.["harborline/primary-location/v2"]
  && canary?.partialFingerprints?.["harborline/project/v1"];
process.exit(valid ? 0 : 1);
' "$sarif" || { echo "eslint canary FAIL — HLQ.TS.1000 is absent from normalized SARIF" >&2; exit 1; }
echo "eslint canary GREEN — @typescript-eslint/no-floating-promises detected"
