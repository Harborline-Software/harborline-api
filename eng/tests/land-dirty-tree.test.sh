#!/usr/bin/env bash
# Ticket 335 fix 1: land-owned scratch must never dirty the merge worktree the exact-clone gate sees.
set -uo pipefail
set +e

source_root=$(cd "$(dirname "$0")/../.." && pwd)
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
real_node=$(command -v node)

write_baseline() {
  local path=$1 total=$2
  mkdir -p "$(dirname "$path")"
  cat > "$path" <<EOF
{"totals":{"total":$total,"passed":$((total - 2)),"failed":0,"notExecuted":2},"permittedFailures":[],"knownFlaky":[]}
EOF
}

remote="$scratch/remote.git"
seed="$scratch/seed"
runner="$scratch/runner"
git init --bare -q "$remote"
git init -q -b main "$seed"
git -C "$seed" config user.name 'Land Test'
git -C "$seed" config user.email 'land-test@example.invalid'
mkdir -p "$seed/eng"
cp "$source_root/eng/land.sh" "$source_root/eng/land-resolve.sh" "$source_root/eng/land-evidence.sh" \
  "$source_root/eng/gate-lock.sh" "$source_root/eng/repin-baseline.mjs" "$source_root/eng/splice-generic.js" "$seed/eng/"
cat > "$seed/eng/gate-lock.sh" <<'EOF'
gate_lock_acquire() { :; }
gate_lock_release() { :; }
EOF
cat > "$seed/eng/test-clean-tree-gate.sh" <<'EOF'
#!/usr/bin/env bash
test -z "$(git status --porcelain)"
EOF
chmod +x "$seed/eng/test-clean-tree-gate.sh"
write_baseline "$seed/eng/baselines/host-test-baseline.json" 100
git -C "$seed" add .
git -C "$seed" commit -q -m seed
git -C "$seed" remote add origin "$remote"
git -C "$seed" push -q -u origin main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main

git -C "$seed" switch -q -c feature
write_baseline "$seed/eng/baselines/host-test-baseline.json" 102
git -C "$seed" add .
git -C "$seed" commit -q -m feature
git -C "$seed" push -q -u origin feature
git -C "$seed" switch -q main
write_baseline "$seed/eng/baselines/host-test-baseline.json" 103
git -C "$seed" add .
git -C "$seed" commit -q -m main
git -C "$seed" push -q

git clone -q "$remote" "$runner"
git -C "$runner" config user.name 'Land Test'
git -C "$runner" config user.email 'land-test@example.invalid'
shim="$scratch/shim"
mkdir -p "$shim"
cat > "$shim/node" <<'EOF'
#!/usr/bin/env bash
case "$1" in
  eng/build-local-feed.mjs|eng/verify-receipt.mjs) exit 0 ;;
  *) exec "$REAL_NODE" "$@" ;;
esac
EOF
cat > "$shim/dotnet" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod +x "$shim/node" "$shim/dotnet"

out="$scratch/output.log"
REAL_NODE="$real_node" HARBORLINE_LAND_VERIFY_CMD='bash eng/test-clean-tree-gate.sh' \
  HARBORLINE_GATE_LOCK_PATH="$scratch/gate.lock" PATH="$shim:$PATH" \
  bash -c 'cd "$1" && bash eng/land.sh feature --dry-run' -- "$runner" > "$out" 2>&1
rc=$?
if [ "$rc" -ne 0 ]; then
  echo 'FAIL clean-tree gate: landing verifier found land-owned scratch in the merge worktree'
  tail -20 "$out"
  exit 1
fi
echo 'ok   land verifier sees a clean merge worktree'
