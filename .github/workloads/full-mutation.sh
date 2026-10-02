set -euo pipefail
node --test eng/tests/platform-feed.test.mjs
node eng/build-local-feed.mjs
dotnet tool restore
node eng/mutation-report.mjs --full ${SLICE:+--slice "$SLICE"}
