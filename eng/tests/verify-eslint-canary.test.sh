#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
bash "$root/eng/verify-eslint-canary.sh"
test ! -e "$root/packages/contracts/src/quality-floating-promise-canary.ts"
echo 'eslint canary: planted violation detected and restored'
