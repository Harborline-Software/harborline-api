#!/bin/bash
set -euo pipefail
umask 077
# PID 1 is docker-init. Both admission refusal and docker stop reach this trap.
trap 'exit 143' TERM INT
mkdir -p /runner/home
cp -R /opt/actions-runner/. /runner/
chmod -R u+rwX /runner
touch /runner/READY
# timeout bounds container life even if the host controller disappears.
timeout --signal=TERM --kill-after=5s 3600 bash -c '
  while [ ! -f /runner/START ]; do sleep 1; done
  cd /runner
  exec ./run.sh
' &
wait "$!"
