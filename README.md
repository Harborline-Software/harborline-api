Raw Stryker.NET reports and manual-mutation logs for docs/evidence/ck4-candidate-mutation-2026-09-29.md (api branch codex/ck4-candidate-evidence), the T-984 re-run of the DES-0029 kernel-core-ck-4 items that are stale at api candidate 9076915d476a3096e78f643c92300fff5e11eb15 and platform pin 0f30804948db81590c1032b88da9e18ad1dea0d6.

- reports/t946-fence.*: scoped run, mutate **/Health/CallerTenantIdentifierFence.cs, filter FullyQualifiedName~CallerTenantIdentifierFenceTests (T-946 items).
- reports/pr248-fence-walk.*: scoped run, same file, filter adds RouteAudienceGraphTests.Every_production_route_refuses_caller_supplied_tenant_identifiers (api #248 item).
- reports/m1m2-active-team.*, reports/m3-selected-session.*: T-537 M1/M2 and M3 scoped runs under Holds=kernel-core-ck-4, re-run because api #286 (T-537 S1) changed their files after 675fbc36.
- reports/platform-tenancy.*: node tooling/stryker.mjs full hlp.foundation.tenancy.tests at platform 0f308049 (tenancy floor).
- manual/fence-use-removed.*: both CallerTenantIdentifierFence.Use calls in SharedHostedWebApp.cs commented out; green control, then 4/4 red; source restored.
- manual/hand-{m2,m3,switch}.*: T-537 hand mutations M2, M3 and the disabled switch candidate check; hand.green-control.log is the unmutated run; source restored after each.
- reports/summary.txt: every tested mutant with its killing tests. reports/SHA256SUMS: file hashes.
