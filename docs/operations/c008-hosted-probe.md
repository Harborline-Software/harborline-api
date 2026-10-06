# C-008 hosted Windows causal diagnostic

This draft diagnostic targets GitHub-hosted windows-2025 and does not reserve a
desktop or registered runner. It changes no required checks, production code,
baseline entries, retry limits, SDK selection or normal host gate.

Review the exact PR head before applying c008-diagnostic-reviewed as ctwoodwa.
Only that label event can run this workflow. A later source change requires a new
exact-head review and remove/reapply of the label; synchronization does not run it.
These guards are admission coordination, not a security boundary against an
arbitrary same-repository workflow author. The job is ephemeral, read-only and
uses no repository secrets, private checkout or cache write.

The base is f7664a1c75bd051fb39ff312f62b492ff18e0362, SDK pinned by global.json.
The unmodified HostBootSmokeTests blob is a41b43ea76fe02622d7908f419d735c81fee9dc1.
Mac's 35d2cefc negative-fixture patch is not included: it qualifies .NET10;
this Windows job uses pinned .NET11. It preserves the original negative DI-cycle
fixture byte-for-byte and does not claim cross-SDK equivalence.

After building a fresh pinned public platform feed, the driver instruments only
the positive test in the disposable checkout, hashes both source versions and
restores the original afterward. Normal samples retain the production graph and
all existing assertions. Five quiet and five two-worker-CPU-load repetitions run
with the original shared budget, then with separate 30-second shutdown budgets.
The controlled expiry pair alone adds a cancellation-observing hosted service:
the shared canceled token must fail with its exact sentinel, while the fresh
shutdown token must pass. The original negative DI-cycle case runs quiet and
loaded. Every invocation must produce exactly one executed, unskipped TRX test.

Raw logs, phase timing, cancellation flags, TRX and identity-bound JSON are
uploaded. CPU load is one fixed profile, not exhaustive historical reproduction.
Passing controls demonstrate the budget mechanism, not that it caused C-008's
historical startup error. No outcome automatically renews or retires the registry.
