# CodeQL cache capability triage (T-679)

Rule: `actions/cache-poisoning/poisonable-step`, high / security severity 7.5.
This finding is not dismissed or suppressed. No external attacker-controlled
full-main execution path was demonstrated. The draft now refuses
unmerged full-run producer pins before checkout execution.

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

The initial native `cache-mode: none` experiment was not accepted as proof:
App's hosted guard did not observe the advertised effective `none` mode and
failed closed before mutation. That unsupported guard/declaration is removed,
not weakened by fabricating an environment value or dismissing the alert.

The final trust check is independent of cache-mode availability. Checkout first
fetches the fixed Platform `main` and its complete history without running code.
`select-platform-pin.mjs` validates schema, fixed repository and exact 40-hex SHA;
full mutation requires that SHA to be an ancestor of fetched `origin/main` before
detaching to it, importing modules or invoking MSBuild. Unmerged, missing or
mutable refs fail closed. Thus future full-run pins cannot silently select an
unreviewed Platform branch even if a consumer pin review misses that distinction.
The build still uses the recorded immutable version, not the newest main tree.

Hosted PR feedback retains proposed immutable producer pins: it is a PR-trigger
job in that PR's cache scope, and original fork/draft guards still apply. There
is no exception for schedule or dispatch. Checkout credentials are never
persisted; contents permission remains read-only and no secrets are added.
Trusted main code can still access its normal cache capability. This change
does not claim that dependency execution is sandboxed or that all caches are
disabled; it enforces the source-code trust boundary before that execution.

Full mutation's NuGet directory is under runner temp with run/attempt identity;
its feed is rebuilt in the clean job workspace and is not uploaded/restored as
an Actions cache. These controls do not isolate arbitrary writes to persistent
self-hosted disk or the shared tool cache; trusted-code restrictions and dormant
Mac qualification remain mandatory. Artifact report upload is not a cache.

## Regression and review evidence

`select-platform-pin.test.mjs` uses a real temporary Git repository with an
approved main commit and a divergent unmerged commit. It proves full mode
rejects the divergent commit without checking it out; allows the approved SHA;
rejects mutable/invalid refs and a different repository; and preserves hosted PR
proposed-pin behavior. The fixture's module throws if executed: no build or
mutation is needed to test this trust boundary. A wiring regression checks the
literal main checkout/full history, validation ordering and no persisted token.
App's preparation/failure tests remain in place.

The current CodeQL cache-write model checks event/default-branch scope. Static
results for the final trust check are still subject to independent review; keep
the check and alert visible, with no suppression or permission exception.

Sources:

- [CodeQL rule](https://codeql.github.com/codeql-query-help/actions/actions-cache-poisoning-poisonable-step/)
- [Query capability model](https://github.com/github/codeql/blob/main/actions/ql/lib/codeql/actions/security/CachePoisoningQuery.qll)
- [Native cache-mode enforcement](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#controlling-cache-access-with-cache-mode)
- [Workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#cache-mode)
- [Schedule/dispatch semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)
