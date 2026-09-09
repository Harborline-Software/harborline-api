#!/usr/bin/env bash
# gate-receipt-ref.sh <tree-sha> [receipt.json] [--no-push]   (ticket 350 slice 1)
#
# Publishes the verification receipt eng/verify-receipt.mjs wrote as refs/receipts/tree/<tree-sha>,
# a ref pointing straight at a BLOB holding the receipt JSON. A blob, not a commit: the receipt
# attests to a tree, has no history, and nothing should ever try to check it out.
#
# host and recordedAt are filled in if the receipt lacks them — eng/land.sh names both in the line it
# prints when it accepts, and the age check is the only thing standing between a landing and a stale
# gate. Idempotent: re-running for the same tree overwrites the ref and force-pushes it.
set -euo pipefail
tree=${1:?usage: gate-receipt-ref.sh <tree-sha> [receipt.json] [--no-push]}; shift
push=1; receipt=""
while [ $# -gt 0 ]; do case $1 in --no-push) push=0; shift;; *) receipt=$1; shift;; esac; done
root=$(git rev-parse --show-toplevel)
[ -n "$receipt" ] || receipt="$(git -C "$root" rev-parse --git-common-dir)/harborline-api-verify-receipt.json"
[ -f "$receipt" ] || { echo "gate-receipt-ref: no receipt at $receipt" >&2; exit 1; }
git -C "$root" rev-parse --verify -q "$tree^{tree}" >/dev/null || { echo "gate-receipt-ref: $tree is not a tree in this repository" >&2; exit 1; }
tree=$(git -C "$root" rev-parse "$tree^{tree}")

# Normalise in node: same JSON parser that reads it back, and the refusal below is the one thing
# that stops a receipt for one tree being published under another tree's name.
normalized=$(HARBORLINE_RECEIPT_TREE="$tree" node -e '
const {readFileSync} = require("node:fs")
const {hostname} = require("node:os")
const tree = process.env.HARBORLINE_RECEIPT_TREE
const receipt = JSON.parse(readFileSync(process.argv[1], "utf8"))
if (receipt.testedTree !== tree) {
  console.error(`gate-receipt-ref: the receipt attests to tree ${String(receipt.testedTree).slice(0,12)}, not ${tree.slice(0,12)}`)
  process.exit(1)
}
receipt.host = receipt.host || process.env.HARBORLINE_RECEIPT_HOST || hostname()
receipt.recordedAt = receipt.recordedAt || new Date().toISOString()
process.stdout.write(JSON.stringify(receipt, null, 2) + "\n")
' "$receipt")

blob=$(printf '%s' "$normalized" | git -C "$root" hash-object -w --stdin)
ref="refs/receipts/tree/$tree"
git -C "$root" update-ref "$ref" "$blob"
echo "gate-receipt-ref: $ref -> blob ${blob:0:12} (tree ${tree:0:12})"
if [ "$push" -eq 1 ]; then
  git -C "$root" push --no-verify -q --force origin "$blob:$ref"
  echo "gate-receipt-ref: pushed $ref to origin"
fi
