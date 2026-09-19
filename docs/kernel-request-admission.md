# Kernel request admission (T-642)

The host consumes `Harborline.Kernel.WorkItems` through the platform feed. The
platform pin is `f90829c0e2c825754aa795d2a104df2af27bd4d1`; its package-input content
version is `0.0.0-alpha.0.hdf039dca1ada`. The host serializes the kernel's refusal
and uses its status code. It does not implement the count or time-window policy.

## Command envelope

JSON POST, PUT, PATCH, and DELETE requests under `/api/local-node` may wrap their
ordinary route body in `{ "commands": [body] }`. The envelope has only the
`commands` property. Each command targets the requested route; the envelope
does not introduce arbitrary route or executor selection.

The shared listener admits callers, then calls the kernel before dispatching
the envelope to any route. More than one command returns 400 with
`code: "kernel.multi-command-batch"`, `statusCode`, and `commandCount`. One command
dispatches its body once. An empty envelope returns 204 without dispatching.
Existing unwrapped route bodies keep their shape. Idempotency runs after envelope
admission against the dispatched body.

## Workflow definition window

`PUT /api/local-node/workflows/definitions/{key}` accepts an optional declaration:

```json
{
  "contractWindow": {
    "opensAt": "2026-09-19T12:00:00Z",
    "closesAt": "2026-09-19T13:00:00Z"
  }
}
```

The declaration accompanies the ordinary workflow definition fields. The route
authorizes the write, reads the current published definition, and calls the
kernel before interpreting or persisting the incoming definition. Both the
stored and incoming windows apply. Omitting or widening the incoming window
cannot bypass the stored window. Definitions without a declared window retain
their existing authoring behavior.

The host supplies the route key as the definition identity and samples its
`TimeProvider` for the observation instant. The kernel admits the opening instant
and refuses the closing instant. Its 422 response carries `code`, `statusCode`,
`definitionId`, `opensAt`, `closesAt`, and `observedAt`. Malformed declarations
return 400 with `code: "definition.invalid-contract-window"`.

The envelope shape, optional window declaration, and initial scope to workflow
authoring are implementation choices because the brief supplied no wire contract
or definition-family selection. They are not recorded as an owner's design verdict.

## Focused verification

`KernelRefusalRouteTests` drives the production shared listener and workflow
routes over HTTP. It covers command count and dispatch, authentication ordering,
before/open/inside/close/after writes, malformed windows, member interpretation,
and replacement attempts that omit or widen a stored window. The existing
`WorkflowDefinitionRouteTests` and `WorkflowDefinitionRefusalRouteTests` cover
unwindowed authoring and authorization refusals. Run these classes with a
`dotnet test --filter` expression; no full gate is needed for this focused run.

The focused run passed 27 route tests (17 new and 10 existing), 21 shared-listener
and idempotency regressions, and 6 tests in `eng/tests/platform-feed.test.mjs`.
The listener regressions ran with `Logging__EventLog__LogLevel__Default=None`
because the sandbox account cannot write to the Windows Event Log. Restore used
the local feed and package caches; vulnerability auditing was disabled for that
offline restore only. No full gate was run.
