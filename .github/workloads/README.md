# Background workload routing (T-679)

`workload-policy.json` records eligible routes and the UTC schedule. An unset
operator route preserves Windows; mac16 remains disabled pending qualification.
The hosted routing job explains pauses and reservations before local dispatch.

Operator contract and activation prerequisites:
https://github.com/Harborline-Software/harborline-control/blob/codex/t679-workload-routing/docs/workload-routing.md

Focused checks (no mutation or gate execution):

```sh
node --test .github/workloads/workload-route.test.mjs
python .github/workloads/host-workload-lock.test.py
```

Vendored helpers are identical in API, App and Platform. Keep them synchronized.
macOS exclusion and cancellation require native qualification before activation.
Keep one heavy agent per machine; Windows multi-agent cancellation is unsupported.
