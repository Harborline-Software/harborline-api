# Host-test global mutation preflight

Run `dotnet run --project eng/test-isolation/test-isolation.csproj -- .` with the pinned SDK.
The existing boundary preflight runs the same command. This builds one SDK-only executable,
not the API or Platform graph; Roslyn comes from the SDK and there are no NuGet dependencies.
It runs planted refusal controls and diagnostic self-tests before checking host test source.

Scope: `apps/local-node-host/tests/**/*.cs`, excluding bin/obj. Roslyn syntax nodes identify
invocations named ClearAllPools, SetEnvironmentVariable or SetCurrentDirectory, and assignments
to DefaultThreadCurrentCulture, DefaultThreadCurrentUICulture or CurrentDirectory. Receiver names
are deliberately unrestricted, so aliases and static imports do not bypass those member checks.
Comments and strings do not count. CurrentCulture/CurrentUICulture are thread/async-flow state
and are outside this process-wide fence. ClearPool is scoped to one pool and is outside it.

A new site passes only in a test type with a Collection attribute whose definition has literal
DisableParallelization=true. Collection names may be string literals or unique class-qualified
string constants. Private nested helpers stay within their test owner; externally accessible
helpers cannot be isolated by decorating the helper class and require caller review. Legacy debt
must match path, containing type, operation and count exactly: adding a site in an existing type
fails, and resolved debt must be removed. Each debt row includes a SHA-256 of the declaring type's
code tokens, an existing review/disposition owner and a reason. Changing that type's code at the
same site count also fails; comments and formatting do not affect the fingerprint. This freezes
the local legacy type, not callers elsewhere in the assembly.

This is a syntax ratchet, not a semantic call graph or proof of restoration/concurrency safety.
It does not follow indirect calls, delegates/reflection, production mutations invoked by fixtures,
conditional-compilation branches inactive under default parsing, collection inheritance, or other
global APIs. The operation list is deliberately small and conservative: a similarly named custom
method is also flagged. An indirect caller can change without changing the frozen helper type;
that remains outside this guard and needs review. No general isolation claim follows from green.

## Legacy debt and ownership

`legacy-debt.json` records inherited source debt at API main f087710b, not accepted-safe exceptions
or permitted test failures. Keeping these existing sites is a bounded rollout choice: repairing
all callers and fixture cleanup would broaden this PR and require host validation. Existing
T-1031 owns review/disposition of inherited findings; this draft does not claim that ticket or
close its control. T-284 retains retry/registry ownership and T-965 retains baseline ownership.

- Pool/file cleanup: calendar, financial reversal/unapply, encryption, node store, maintenance,
  branding, rehost, DEK pairing and identity fixtures currently clear every process pool to release
  temporary stores. Their exact sites remain visible; none is certified safe for overlap.
- Shared helpers: SqliteEntityStore, DurableStore, CutoverProofStore and SqliteLegacyStore cannot
  be fixed by decorating their helper classes. Their callers need review before removing debt.
- CalendarDevSeederTests and VolatileBlobStoreProductionGuardTests temporarily set host environment
  values. Restoration alone does not establish isolation; their exact sites remain debt.
- AccessNavigationUpgradeCompositionTests: API #320 already owns the isolation correction. Its
  one debt row must be removed after that correction reaches this branch; do not duplicate it here.

No automatic inventory update exists. Changes to debt require an explicit reviewed reason.
The earlier exact-package SQLite diagnostic was bounded stress, not deterministic. There is no
stress retry loop or vendor pool-race regression in this preflight.

## Failure diagnostics

The document attribution fixture retains a bounded 64-entry structural buffer. It records request
sequence/status/timing and Kestrel level/event ID/exception type. Unexpected HTTP status or a
transport exception writes the buffer to xUnit output; matching statuses (including expected 400)
emit nothing. Log formatters, messages, event names, scopes, bodies, headers, URLs/query strings,
environment values and principal/session identifiers are never recorded. This deliberately
trades message detail for secret exclusion. Assertions, server limits and client timeouts stay
unchanged. Body buffering remains owned by #320. There is no claim about the original CI cause.
