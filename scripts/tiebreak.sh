#!/usr/bin/env bash
# Probe the two pre-insert tie-break survivors (RosterCrdtProjection.cs:1082) by hand, three test runs each.
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
T=apps/local-node-host/tests/tests.csproj
S=apps/local-node-host/Data/Roster/RosterCrdtProjection.cs
F='FullyQualifiedName~RosterPreInsertVerificationTests'
for pair in 'nonce-ne:s/\(r\.Signed\.Nonce == nonce &&/(r.Signed.Nonce != nonce \&\&/' 'ordinal-gt:s/candidate\.SignatureB64Url\) < 0\)\)\)\);/candidate.SignatureB64Url) > 0))));/'; do
  id=tiebreak-${pair%%:*}; expr=${pair#*:}
  perl -pi -e "\$_ = do { my \$l = \$_; \$l =~ $expr; \$l } if \$. == 1082" $S
  git diff -- $S > $OUT/$id.diff
  if [ -s $OUT/$id.diff ] && timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$id.build.log 2>&1; then
    for i in 1 2 3; do timeout 900 dotnet test $T --no-build -nodeReuse:false --filter "$F" --logger "trx;LogFileName=$OUT/$id-$i.trx" > $OUT/$id-$i.test.log 2>&1
      echo "$(date -Is) $id run$i $(grep -E 'Passed!|Failed!' $OUT/$id-$i.test.log | tail -1)" >> $OUT/status.log; done
  else echo "$(date -Is) $id not-applied-or-build-failed" >> $OUT/status.log; fi
  git checkout -- $S
done
echo "$(date -Is) DONE-TIEBREAK" >> $OUT/status.log
