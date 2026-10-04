# Host test diagnostics

Exact-clone host tests retain their filter, TRX authority, coverage settings,
exit handling and existing time budget. Plain VSTest `--blame` plus an Info
`--diag` trace supplies test start/end events without a hang timer, dump or
abort. Raw trace files stay under scratch, outside the uploaded evidence path.

The existing independent heartbeat worker reads at most sixteen trace files
and one MiB per file per sample. It publishes only cumulative start/end counts,
observation timestamps, a completeness flag and numeric trace-writer PIDs into
the existing durable progress journal. It never publishes raw trace, test
names/data, arguments or environment. Windows CPU probes read only those PIDs'
total processor time, with a three-second probe limit. Other hosts explicitly
report CPU unavailable.

An advancing elapsed timer alone proves observer liveness. Increasing test-end
counts demonstrate completed work; an unchanged last-test-activity timestamp
with increasing CPU suggests active work between events. Stationary counts and
CPU warrant investigation, not an automatic stall verdict: trace writers are
not a complete process-tree sample. Compare samples only when `caughtUp` is
true and CPU is available. Timestamps are observation times, not exact event
times. Final child completion does not drain unsampled trace events.

All observer failures remain non-gating. The controlled hanging-child test
checks that elapsed time advances while test completion counts and test
activity stay stationary, and that the journal survives external cancellation.
No timeout increase, retry, baseline or roster change follows from telemetry.
