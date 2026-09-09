#!/usr/bin/env bash
set -uo pipefail

here=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=fixture-git-retry.sh
source "$here/fixture-git-retry.sh"

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
real_git=$(command -v git)
repo="$scratch/repo"
git_r init -q "$repo"

shim="$scratch/shim"
mkdir -p "$shim"
cat > "$shim/git" <<'EOF'
#!/usr/bin/env bash
if [[ " $* " == *" config "* ]]; then
  count_file="$FIXTURE_GIT_SHIM_COUNT"
  count=$(cat "$count_file" 2>/dev/null || echo 0)
  count=$((count + 1))
  printf '%s\n' "$count" > "$count_file"
  if [ "${FIXTURE_GIT_SHIM_ALWAYS_FAIL:-0}" = 1 ] || [ "$count" = 1 ]; then
    echo 'error: could not write config file .git/config: Permission denied' >&2
    exit 128
  fi
fi
exec "$REAL_GIT" "$@"
EOF
chmod +x "$shim/git"

count_file="$scratch/count"
out=$(REAL_GIT="$real_git" FIXTURE_GIT_SHIM_COUNT="$count_file" PATH="$shim:$PATH" git_r -C "$repo" config user.name 'Fixture Test' 2>&1); rc=$?
[ "$rc" -eq 0 ] || { echo "FAIL one denied config attempt did not recover: $out"; exit 1; }
retry_lines=$(printf '%s\n' "$out" | grep -c '^fixture: retried git ')
[ "$retry_lines" -eq 1 ] || { echo "FAIL retry lines=$retry_lines :: $out"; exit 1; }
[ "$(cat "$count_file")" -eq 2 ] || { echo "FAIL config calls=$(cat "$count_file")"; exit 1; }

printf '0\n' > "$count_file"
out=$(REAL_GIT="$real_git" FIXTURE_GIT_SHIM_COUNT="$count_file" FIXTURE_GIT_SHIM_ALWAYS_FAIL=1 PATH="$shim:$PATH" git_r -C "$repo" config user.email fixture@example.invalid 2>&1); rc=$?
[ "$rc" -eq 128 ] || { echo "FAIL permanent denied config rc=$rc :: $out"; exit 1; }
grep -Fq 'error: could not write config file .git/config: Permission denied' <<<"$out" || { echo "FAIL permanent denial lost original message :: $out"; exit 1; }
[ "$(cat "$count_file")" -eq 6 ] || { echo "FAIL permanent config calls=$(cat "$count_file")"; exit 1; }

echo 'ok   fixture git retry recovers one denied config write and preserves exhausted failure'
