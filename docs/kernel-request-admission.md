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

## Definition windows

The five definition-write routes in the form, scheduling, and workflow
definition route maps consume the same pinned `DefinitionWriteBoundary` through
`NodeDefinitionWrites`. Authorization and capability declarations are unchanged;
authorization runs before window admission. The host parses the declaration and
serializes the kernel refusal without implementing its time policy.

```json
{
  "contractWindow": {
    "opensAt": "2026-09-19T12:00:00Z",
    "closesAt": "2026-09-19T13:00:00Z"
  }
}
```

A declaration is optional. The kernel admits the opening instant and refuses the
closing instant. Its 422 response carries exactly `code`, `statusCode`,
`definitionId`, `opensAt`, `closesAt`, and `observedAt`, with
`code: "kernel.definition-contract-window"`. Malformed declarations, including
explicit `null`, return 400 with `code: "definition.invalid-contract-window"`.
The host supplies the observation instant from its `TimeProvider`.

The following paths are relative to `/api/local-node`. Each row has HTTP tests
against its production route map; no row relies on a helper-only test.

| Route | Declaration and governing stored windows | Wired and tested |
|---|---|---|
| `PUT /forms/definitions/{formId}` | Top-level declaration; published head and any newer draft also apply | Yes |
| `POST /forms/definitions/{formId}/restore` | Optional top-level request declaration; source, published head, and any newer draft also apply | Yes |
| `PUT /scheduling/definitions/{definitionId}/draft` | `definition.contractWindow`; current head also applies | Yes |
| `POST /scheduling/definitions/{definitionId}/restore` | Optional top-level request declaration; source and current head also apply | Yes |
| `PUT /workflows/definitions/{key}` | Top-level declaration; current published definition also applies | Yes |

Stored declarations prevent a replacement or restore from bypassing a closed
window by omitting or widening its incoming declaration. Form declarations survive
JSON persistence, authoring reads, and restoration into a new draft. Scheduling
and workflow definitions retain their declarations in their existing JSON bodies.
A restore request's own declaration bounds that operation; the restored definition
retains the source declaration. Unwindowed requests keep their existing behavior.

| Family | Applicability |
|---|---|
| DataExchange | not applicable: its definition route map declares no POST, PUT, PATCH or DELETE |
| Report | not applicable: its definition route map declares no write route. The six POSTs in `ReportsRoutes` run a report and do not author one |
| View | not applicable: its definition route map declares no POST, PUT, PATCH or DELETE |

Read that as no definition-write route, not as no POST. The distinction is the
one this document exists to hold: a POST in a file named for definitions is not
evidence of a definition write, and counting them that way is what produced the
original nine-route denominator.

Four scheduling routes live in the definition route map and carry no window.
Three are instance-plane acts rather than definition writes: they accept no
declaration and cannot be refused for one. A closed authoring window freezes a
definition; it must not stop callers operating the definition it froze. The
fourth, validation, is definition-plane but is not a write.

| Route | Applicability |
|---|---|
| `POST /scheduling/definitions/validate` | not applicable: definition-plane, but reports on a definition rather than writing one |
| `POST /scheduling/appointments` | not applicable: books against a definition, does not write one |
| `POST /scheduling/events` | not applicable: creates an instance record |
| `POST /scheduling/resources/availability` | not applicable: sets operational availability |

## Implementation choices for owner review

The original brief carried a nine-route denominator produced by counting every
write route in files named for definitions. Four of those carry no window: three
instance-plane acts and validation. The denominator is five, and every one of the
five is a definition write. Restore requests accept an optional top-level
declaration; every route uses the addressed definition ID as `definitionId` in the
refusal, since no windowed route lacks one.

`POST /scheduling/definitions/validate` is the one definition-plane route that
carries no window. It reports on a definition rather than writing one, and you
validate in order to prepare for a window, so a shut window must not put
validation out of reach. Owner ruling, 2026-09-19. A definition carrying a
`contractWindow` can therefore be validated at any instant, and a malformed one
is no longer rejected by this route because the declaration is never parsed here.

For forms, the published head and a newer draft both govern writes; a draft older
than the published head no longer governs. Both restore routes check the selected
historical source in addition to current definitions. These are implementation
choices to close bypasses, not approvals or design verdicts in an owner's name.

## Focused verification

The focused route run covers `KernelRefusalRouteTests`,
`FormDefinitionRouteTests`, `SchedulingDefinitionRouteTests`,
`WorkflowDefinitionRouteTests`, and `WorkflowDefinitionRefusalRouteTests`.
The new form and scheduling cases live in partial class files named
`FormDefinitionWindowRouteTests.cs` and `SchedulingDefinitionWindowRouteTests.cs`.

The run passed **244 tests, 0 failed, 0 skipped**, including **109 added cases**:

| Test class | Passed | Added cases |
|---|---:|---:|
| FormDefinitionRouteTests | 104 | 29 |
| SchedulingDefinitionRouteTests | 112 | 79 |
| KernelRefusalRouteTests | 17 | 0 (refusal-shape assertions strengthened) |
| WorkflowDefinitionRouteTests | 7 | 0 |
| WorkflowDefinitionRefusalRouteTests | 4 | 1 |

Every route is exercised before, at opening, inside, at closing, and after its
window. Tests assert the same six-field kernel refusal, malformed-window behavior,
authorization precedence, and absence of definition/calendar/availability writes
on refusal. Scheduling snapshots include revision audit rows. Stored-window tests
cover omission, widening, and restoring an older source. Existing tests cover
unwindowed behavior, permissions, tenancy, conflicts, and ordinary route validation.

Run only these classes:

```powershell
$env:Logging__EventLog__LogLevel__Default = 'None'
dotnet test apps/local-node-host/tests/tests.csproj --no-restore --filter 'FullyQualifiedName~KernelRefusalRouteTests|FullyQualifiedName~FormDefinitionRouteTests|FullyQualifiedName~SchedulingDefinitionRouteTests|FullyQualifiedName~WorkflowDefinitionRouteTests|FullyQualifiedName~WorkflowDefinitionRefusalRouteTests'
```

The test run uses the existing restored package cache. Event Log output is disabled
because the sandbox account cannot write the Windows Event Log. No full gate or
pull request is part of this work. No control ticket, design record, or ledger row
is changed.
