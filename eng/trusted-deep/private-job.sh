#!/bin/bash
# Future immutable entrypoint; absent binding is always a refusal.
set -euo pipefail
kind=$(python3 -I /opt/trusted/job.py private-kind)
exec bash /opt/trusted/run.sh "$kind"
