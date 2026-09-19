# Kernel request admission: the command envelope (T-642)

The host consumes `Harborline.Kernel.WorkItems` through the platform feed. The
platform pin is `f90829c0e2c825754aa795d2a104df2af27bd4d1`; its package-input content
version is `0.0.0-alpha.0.hdf039dca1ada`. The host serializes the kernel's refusal
and uses its status code. It does not implement the count policy.

This document covers the command envelope only. The definition contract window is
deliberately not consumed here: see T-648 and T-649, which unwind a temporal window
that the corpus never asked for.

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

## Why this is proved twice

The boundary is middleware, not a route, so there is no route list to enumerate and
its generality cannot be proved by counting. It is registered in both
`SharedHostedWebApp` pipelines, and each constructor has its own setup, so a single
route-level test would leave one registration site unexercised.

It is therefore sampled at two unrelated routes across both pipelines:

| Pipeline | Route exercised | Test |
|---|---|---|
| `WebApplication` constructor | the workflow definition PUT | `KernelRefusalRouteTests` |
| service-provider constructor | a journal-entries stub | `SharedHostedWebAppCallerAuthTests` |

Each pairs a refused two-command envelope with an admitted one-command envelope,
because a refusal-only assertion cannot tell a counting boundary from one that
refuses everything. Dropping either registration turns a test red rather than
passing silently.

The applicable surface is every mutating JSON route under the prefix and is
deliberately not enumerated. A non-JSON body is not inspected, and a body that is
not an object carrying a `commands` property passes through untouched.
