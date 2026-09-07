#!/usr/bin/env bash
# dangling-token-scan.sh [repo_root] — ticket 260 slice 1.
#
# Invariant (ticket 253 review round 4): every era-family-spelled DI-registration or interface token
# that appears anywhere in the tree — doc, comment, csproj, resx, message string — must resolve to a
# declaration in a .cs file. Four review rounds closed hand-listed rows one at a time and a fifth
# always appeared, because no pass enumerated the class. This does.
#
# Two token shapes are enumerated, because those are the two that name something a reader is told to
# call or implement:
#   Add<family><Word>  -> requires a method declaration
#   I<family><Word>    -> requires an `interface <token>` declaration
#
# Era family words are assembled from character codes, not written out, for the same reason
# verify-boundaries.sh does it: a guard that spells the word it is retiring plants a fresh copy of
# that word in the tree and shows up in every scan for it. Families are the checker's content
# families (harborline-control/tools/scan-identity-standard.mjs).
#
# POSITIVE CONTROL, per docs/adr/0010 in harborline-control. To confirm this guard still bites, add a
# call to a registration that does not exist (e.g. an `Add<family>NoSuchThing()` mention in any doc
# comment) and run this script: it must exit 1 and print that token.
#
# Allow-list: eng/dangling-token-allowlist.tsv. Exact rows, no wildcard — a convention placeholder
# earns a row and states why. A row is `<shape>\t<suffix>\t<reason>`: the family word is stripped
# from the token so the allow-list does not spell it either (same argument as above).
set -uo pipefail
repo_root=${1:-$(git rev-parse --show-toplevel)}
allowlist="$repo_root/eng/dangling-token-allowlist.tsv"

f1=$(printf '\x53\x75\x6e\x66\x69\x73\x68')
f5=$(printf '\x53\x68\x69\x70\x79\x61\x72\x64')
fam="($f1|$f5)"

# allowed <shape> <token> — strips the shape prefix and either family word, then looks the remainder
# up in the allow-list.
allowed() {
  [ -f "$allowlist" ] || return 1
  local suffix=${2#"$1"}
  suffix=${suffix#"$f1"}
  suffix=${suffix#"$f5"}
  grep -qE "^$1	$suffix	.+" "$allowlist"
}

dangling=""

# Registration extensions. A declaration is a method: a return type (or `static`) before the token
# and an argument list after it. Call sites do not satisfy this — a call site is exactly what the
# broken rows were.
for t in $(git -C "$repo_root" grep -hIoE "\bAdd$fam[A-Za-z0-9_]*" -- . | sort -u); do
  git -C "$repo_root" grep -qIE "(static|void|Task|IServiceCollection|Builder)[A-Za-z0-9_<>,. ]+ $t[[:space:]]*[<(]" -- '*.cs' && continue
  allowed Add "$t" && continue
  dangling+="DANGLING $t"$'\n'
done

# Interfaces.
for t in $(git -C "$repo_root" grep -hIoE "\bI$fam[A-Za-z0-9_]*" -- . | sort -u); do
  git -C "$repo_root" grep -qIE "interface[[:space:]]+$t\b" -- '*.cs' && continue
  allowed I "$t" && continue
  dangling+="DANGLING-TYPE $t"$'\n'
done

# Components. Ticket 260 slice 20: slice 1 enumerated the registration and interface shapes only, so a
# doc that names a COMPONENT or type that exists in neither spelling — the largest dead-token class in
# the 253 logs — stayed invisible. A bare `<family><Word>` token resolves against a type declaration in
# a .cs file OR against a Blazor component file named `<token>.razor` (a .razor component declares its
# type implicitly, from the file name). The `+` after the character class is load-bearing: the bare
# family word on its own names nothing and is not a token.
#
# OFF BY DEFAULT, exactly like the R3 checker's --fail-on-r3. At the slice-20 pin the shape finds 71
# dangling component tokens, so wiring it straight into the boundaries gate would make every landing
# red for a backlog no slice has taken yet (see the slice 20 report: the ui-adapters-blazor `Shell/`
# components, which no component slice covered, and the F1-spelled component citations in the
# GAP_ANALYSIS / RESOLUTION_STATUS docs). Set DANGLING_TOKEN_COMPONENT_SHAPE=1 to run it — locally
# now, and in eng/verify-boundaries.sh once that count is zero. `eng/tests/dangling-token-scan.test.sh`
# enumerates the shape with the switch ON, so the guard is exercised either way.
if [ "${DANGLING_TOKEN_COMPONENT_SHAPE:-0}" = "1" ]; then
  for t in $(git -C "$repo_root" grep -hIoE "\b$fam[A-Za-z0-9_]+" -- . | sort -u); do
    git -C "$repo_root" grep -qIE "(class|record|struct|interface|enum)[[:space:]]+$t\b" -- '*.cs' && continue
    [ -n "$(git -C "$repo_root" ls-files -- "*/$t.razor" "$t.razor")" ] && continue
    allowed C "$t" && continue
    dangling+="DANGLING-COMPONENT $t"$'\n'
  done
fi

if [ -n "$dangling" ]; then
  echo "Doc/comment text names a registration or interface that does not exist in this tree." >&2
  echo "Replace it with the symbol that does exist, delete the sentence, or add an allow-list row" >&2
  echo "with a reason in eng/dangling-token-allowlist.tsv:" >&2
  printf '%s' "$dangling" >&2
  exit 1
fi
exit 0
