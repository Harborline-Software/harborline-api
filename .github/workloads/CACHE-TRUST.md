# CodeQL cache capability triage (T-679)

Rule: `actions/cache-poisoning/poisonable-step`, high / security severity 7.5.
This finding is not dismissed or suppressed. No external attacker-controlled
full-main execution path was demonstrated. Cache capability is nevertheless
removed in the draft, with a runner-enforced fail-closed check.

## Source, sink and inheritance

[Alert 50](https://github.com/Harborline-Software/harborline-api/security/code-scanning/50)
reported the full feed/mutation step at `mutation.yml:92-99` on reviewed head
`dfa38d78a60fd1a3a76ff71d5840a6836530ee08`. The checkout action was introduced
by this draft, but [the base workflow](https://github.com/Harborline-Software/harborline-api/blob/f087710b8d838c577b704a3b059fa6b11dfb3343/.github/workflows/mutation.yml)
already read the same pin, fetched/checked it out in a shared clone, and executed
the feed build on winbox with persistent `C:\nuget-runner`. Thus the static alert
is new; the dependency-execution trust assumption is inherited. [Feed execution](https://github.com/Harborline-Software/harborline-api/blob/f087710b8d838c577b704a3b059fa6b11dfb3343/eng/build-local-feed.mjs).

The source is `steps.platform_pin.outputs.commit`, read from the checked-out
consumer's committed `eng/platform-pin.json`. The fixed checkout repository is
`Harborline-Software/harborline-platform`; neither the repository nor ref is a
dispatch input. API dispatch's `slice` only selects mutation scope. `schedule`
runs the default-branch tree; routing preflight refuses non-main full dispatch.
App additionally has a direct main-ref dispatch guard. Fork and draft PR guards
remain intact. PR mutation uses hosted Ubuntu and the pull-request cache scope.

The sink does execute Platform code: `tooling/package-version.mjs` is imported
and MSBuild packs platform projects/targets before consuming the resulting feed.
An immutable SHA identifies bytes but is not by itself proof of trust. On
2026-10-01, both selected pins were confirmed ancestors of fetched Platform main:
API `db469dc92a7a6a1b67a9dcfba0fa9f7e700cbd7d`, App
`eda5e62976c1e6d8aafe12e06ba0d3bcd2cbc26e`. This observation does not qualify
future pins, native machines, dependency integrity or host cancellation.

For a contributor to alter the full-run pin, the changed consumer pin would have
to reach trusted main; ordinary fork/feature dispatch cannot choose it. A merged
malicious dependency change or compromised trusted maintainer remains a supply
chain threat. There is no claim that a SHA or `contents: read` prevents that.

## Cache and credential boundary

`schedule` and `workflow_dispatch` normally have main-scope cache-write access,
separate from GITHUB_TOKEN `contents: read`. Absence of `actions/cache` in this
mutation job does not prevent hostile code using the runtime cache service.
API package workflows also declare pnpm caches, making repository cache reuse
relevant. No declared Actions-cache consumer was found in App's workflows during
this focused scan; that is not a guarantee against future consumers.

The draft now sets native workflow `cache-mode: none`, which denies cache reads
and writes to both routing and mutation jobs. Before either job proceeds,
`cache-isolation.mjs` requires the runner's `ACTIONS_CACHE_MODE` to equal `none`;
missing, read, write and write-only modes fail closed. A shell environment change
by subsequently executed code cannot grant a scoped cache-token capability.
All checkout steps in these workflows use `persist-credentials: false`.
The contents permission stays read-only; no secrets or permissions are added.

Full mutation's NuGet directory is under runner temp with run/attempt identity;
its feed is rebuilt in the clean job workspace and is not uploaded/restored as
an Actions cache. These controls do not isolate arbitrary writes to persistent
self-hosted disk or the shared tool cache; trusted-code restrictions and dormant
Mac qualification remain mandatory. Artifact report upload is not a cache.

## Regression and review evidence

`cache-isolation.test.mjs` checks denial of every non-none mode and the workflow
guard placement. Existing routing tests retain trusted-main/fork behavior.
App's extra workflow tests retain pinned Python ordering and feed/restore
fail-fast behavior. Hosted preflight checks actual native `ACTIONS_CACHE_MODE`;
no mutation or Mac qualification is required to exercise that assertion.

The current public CodeQL `CachePoisoningQuery.qll` predicate
`hasDefaultBranchCacheWriteAccess` checks trigger/default-branch scope without a
cache-mode test. Therefore static findings may persist despite the native
capability restriction. Keep the security check and alert open for independent
review; do not claim CodeQL clearance solely from the source reasoning.

Sources:

- [CodeQL rule](https://codeql.github.com/codeql-query-help/actions/actions-cache-poisoning-poisonable-step/)
- [Query capability model](https://github.com/github/codeql/blob/main/actions/ql/lib/codeql/actions/security/CachePoisoningQuery.qll)
- [Native cache-mode enforcement](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#controlling-cache-access-with-cache-mode)
- [Workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#cache-mode)
- [Schedule/dispatch semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)
