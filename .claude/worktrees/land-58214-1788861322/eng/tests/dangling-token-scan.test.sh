#!/usr/bin/env bash
# Enumerates eng/dangling-token-scan.sh against throwaway git repositories:
#   {registration, interface} x {both era families} x {declared, declared-elsewhere-only, cited-only,
#   call-site-only, allow-listed} x {.cs comment, .md, .csproj, .resx, message string}.
# A citation is ACCEPTED only when a declaration exists in a .cs file or an allow-list row covers it;
# a call site is NOT a declaration (that is the defect four review rounds of ticket 253 chased).
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
fence="$here/../dangling-token-scan.sh"
f1=$(printf '\x53\x75\x6e\x66\x69\x73\x68')
f5=$(printf '\x53\x68\x69\x70\x79\x61\x72\x64')
fails=0; n=0

run_case() { # name expect(0|1) file content [allowlist-rows]
  local name=$1 want=$2 file=$3 content=$4 allow=${5:-}
  local repo; repo=$(mktemp -d)
  ( cd "$repo" && git init -q && mkdir -p eng "$(dirname "$file")" \
    && printf '%s\n' "$content" > "$file" \
    && { [ -z "$allow" ] || printf '%s\n' "$allow" > eng/dangling-token-allowlist.tsv; } \
    && git add -A && git -c user.email=t@t -c user.name=t commit -qm x )
  DANGLING_TOKEN_COMPONENT_SHAPE=${component_shape:-1} bash "$fence" "$repo" >/dev/null 2>&1; local rc=$?
  n=$((n+1))
  if [ "$rc" != "$want" ]; then echo "FAIL $name: rc=$rc want=$want"; fails=$((fails+1)); else echo "ok   $name"; fi
  rm -rf "$repo"
}

for fam in "$f1" "$f5"; do
  short=${fam:0:3}
  # --- registrations -------------------------------------------------------
  run_case "$short add: cited in a doc comment, never declared" 1 Doc.cs \
    "/// Call <c>Add${fam}Ghost()</c> to register it."
  run_case "$short add: cited and declared" 0 Doc.cs \
    "$(printf '/// Call <c>Add%sGhost()</c>.\npublic static IServiceCollection Add%sGhost(this IServiceCollection s) => s;' "$fam" "$fam")"
  run_case "$short add: only a CALL SITE, no declaration" 1 Doc.cs \
    "$(printf '/// Call <c>Add%sGhost()</c>.\nvoid Compose(IServiceCollection s) { s.Add%sGhost(); }' "$fam" "$fam")"
  run_case "$short add: cited in markdown, never declared" 1 README.md \
    "Register it with \`Add${fam}Ghost\`."
  run_case "$short add: cited in a csproj comment, never declared" 1 App.csproj \
    "<Project><!-- see Add${fam}Ghost --></Project>"
  run_case "$short add: cited in a resx comment, never declared" 1 R.resx \
    "<root><!-- see Add${fam}Ghost --></root>"
  run_case "$short add: cited in a thrown message string, never declared" 1 Doc.cs \
    "throw new InvalidOperationException(\"call Add${fam}Ghost() first\");"
  run_case "$short add: undeclared but allow-listed with a reason" 0 Doc.cs \
    "/// Call <c>Add${fam}Ghost()</c>." "$(printf 'Add\tGhost\tconvention placeholder')"
  run_case "$short add: allow-list row with no reason does not cover it" 1 Doc.cs \
    "/// Call <c>Add${fam}Ghost()</c>." "$(printf 'Add\tGhost\t')"
  run_case "$short add: allow-list row for a different token does not cover it" 1 Doc.cs \
    "/// Call <c>Add${fam}Ghost()</c>." "$(printf 'Add\tOther\treason')"
  # --- interfaces ----------------------------------------------------------
  run_case "$short type: cited in a doc comment, never declared" 1 Doc.cs \
    "/// Implement <c>I${fam}Ghost</c>."
  run_case "$short type: cited and declared" 0 Doc.cs \
    "$(printf '/// Implement <c>I%sGhost</c>.\npublic interface I%sGhost { }' "$fam" "$fam")"
  run_case "$short type: only USED as a parameter type, never declared" 1 Doc.cs \
    "void Take(I${fam}Ghost g) { }"
  run_case "$short type: cited in markdown, never declared" 1 GAP.md \
    "- [ ] Define \`I${fam}Ghost\` interface"
  run_case "$short type: undeclared but allow-listed with a reason" 0 Doc.cs \
    "/// Implement <c>I${fam}Ghost</c>." "$(printf 'I\tGhost\tspec-side name')"
  # --- shape discrimination ------------------------------------------------
  run_case "$short bare family word alone is not a token" 0 Doc.cs \
    "// the ${fam} era"
  run_case "$short a declared type that is not an interface or registration" 0 Doc.cs \
    "public sealed class ${fam}Widget { }"
  # --- components (ticket 260 slice 20) ------------------------------------
  run_case "$short component: cited in a gap-analysis doc, declared nowhere" 1 GAP_ANALYSIS.md \
    "- [ ] Port \`${fam}Widget\` to the new shell"
  run_case "$short component: cited in a doc and declared as a class" 0 Doc.cs \
    "$(printf '/// See <c>%sWidget</c>.\npublic sealed class %sWidget { }' "$fam" "$fam")"
  run_case "$short component: cited in a doc and declared as a record" 0 Doc.cs \
    "$(printf '/// See <c>%sWidget</c>.\npublic sealed record %sWidget(int A);' "$fam" "$fam")"
  run_case "$short component: resolved by a .razor file of the same name" 0 "Components/${fam}Widget.razor" \
    "<div>@Body</div>"
  run_case "$short component: a .razor file of a DIFFERENT name does not resolve it" 1 Doc.cs \
    "/// See <c>${fam}Widget</c>."
  run_case "$short component: only USED as a parameter type, never declared" 1 Doc.cs \
    "void Take(${fam}Widget w) { }"
  run_case "$short component: cited in a css file, declared nowhere" 1 site.css \
    ".x { /* ${fam}Widget */ }"
  run_case "$short component: undeclared but allow-listed with a reason" 0 Doc.cs \
    "/// See <c>${fam}Widget</c>." "$(printf 'C\tWidget\tspec-side component name')"
  run_case "$short component: an allow-list row of another shape does not cover it" 1 Doc.cs \
    "/// See <c>${fam}Widget</c>." "$(printf 'I\tWidget\twrong shape')"
  component_shape=0 run_case "$short component shape is OFF unless the switch is set" 0 GAP_ANALYSIS.md \
    "- [ ] Port \`${fam}Widget\` to the new shell"
done

# A declaration in a non-.cs file does not count: the guard resolves against compiled source.
run_case "declaration only in markdown does not resolve" 1 README.md \
  "public static IServiceCollection Add${f5}Ghost(this IServiceCollection s) => s;"

echo "$n cases, $fails failures"
[ $fails -eq 0 ]
