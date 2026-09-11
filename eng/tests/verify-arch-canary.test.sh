#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
bash "$root/eng/verify-arch-canary.sh"
test ! -e "$root/packages/foundation-arch-canary"
echo 'arch canary: planted dependency edge detected and restored'
