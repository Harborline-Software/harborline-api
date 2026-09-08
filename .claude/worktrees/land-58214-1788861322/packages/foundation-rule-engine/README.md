# Harborline.Api.Foundation.RuleEngine (SPINE-1)

The **Tier-2 rule engine** — the shared spine of the form + workflow keystones
(ADR 0140 D2; the `Harborline.Api.Foundation.RuleEngine` package ADR-0055 forward-references as
"separate package, forthcoming"). A dependency-graph evaluator over a closed, versioned
JsonLogic operator set with fail-closed resource bounds and authoring-time cycle detection.

This is the **.NET integrity tier** (server-side, synchronous, gates writes + workflow
transitions). The **TS reactive tier** is `@harborline-software/rule-engine` (sibling package); both emit
structurally-identical `RuleOutcome` values, proven byte-for-byte by the **shared conformance
corpus** (`conformance/corpus/*.json`, loaded by both suites — design §6.1, §6.4).

## Tier model (ADR 0140 + CIC 2026-06-30)

- **Tier-1** = JSON-Schema constraints / `pattern` (free; the kernel registry; ReDoS-bounded).
- **Tier-2** = this engine — ONE portable `shipyard-jsonlogic/v1` doing **both logic AND compute**.
- **Power Fx is DEMOTED** — no longer a tier; reserved as a future advanced-function provider
  behind this same contract. The v1 evaluator rejects `RuleTier.PowerFx`.

## The neutral contract

- **Authoring shape** = the existing `RuleDefinition` in `Harborline.Api.Foundation.Forms` (extended
  additively by SPINE-1: `RuleActionKind.Presentation`, `RuleScope.Row`/`Table`, the
  `PresentationHint` payload; `Compute` lifted to Tier-2).
- **Result shape** = the net-new `RuleOutcome { Value | Validity | Visibility | Presentation }`
  (`Model/RuleOutcome.cs`). `Value → ComputedValue { Resolved | Error | Pending }`; errors are
  **localizable codes + params**, never English prose.

### Scope-addressing grammar (lowered to a canonical AST at publish — design §1.2)

| Prefix | Resolves to | Canonical lowering |
|---|---|---|
| `self` | the rule's own target cell | `field.<target>` / `row.<rowField>` |
| `field.<name>` / bare `<name>` | a top-level field | `field.<name>` |
| `row.<field>` | the same row's sibling (Row scope only) | `row.<field>` |
| `parent.<field>` | the parent instance's field (from a row) | `field.<field>` |
| `section.<id>.<field>` | a field via its section | `field.<field>` |
| `table.<fn>(<col>)` / `table.<fn>(<section>.<col>)` | a child-table aggregate | `{"agg":[fn,section,col]}` |

### `shipyard-jsonlogic/v1` — the normative operator set (design §1.4)

Both tiers implement EXACTLY this set, identically. The conformance corpus has ≥1 case per operator.

- **Data:** `var`, `missing`, `missing_some`
- **Logic:** `==`, `!=`, `===`, `!==`, `!`, `!!`, `and`, `or`, `if`
- **Compare:** `>`, `>=`, `<`, `<=`
- **Arithmetic:** `+`, `-`, `*`, `/`, `%`, `min`, `max`
- **Membership / string:** `in`, `cat`
- **Harborline extensions:** `agg` (`sum`/`count`/`avg`/`min`/`max`/`any`/`all`), `money.add`/`money.sub`/`money.mul`
  (exact decimal — BigInteger, NO IEEE-754 drift), `date.add`/`date.diff`/`date.today` (deterministic,
  injected clock), `coding.is` (taxonomy membership over `{system,code}`).
- **Deliberately EXCLUDED in v1:** any **regex/pattern** operator — the ReDoS class is structurally
  absent (Decision DG; pattern validation stays Tier-1). `map`/`filter`/`reduce`/`merge` are deferred
  (child-table folds use `agg`).

> **Decision DI — own interpreter, not a third-party lib.** SPINE-1 ships a self-contained bounded
> interpreter in BOTH tiers (not the json-everything `JsonLogic` / a `json-logic-js` fork). This is
> what guarantees byte-identical operator semantics, precise step-budget accounting, and zero
> dependence on two libraries agreeing on truthiness/coercion. The AST stays JsonLogic-shaped, so a
> later delegation to those libs remains possible.

### The operator set is CLOSED — the erosion guard (D1 ratification 2026-07-01, fix 3)

The set above is closed and version-pinned. A STANDING arch-test enforces it on both tiers
(`tests/OperatorCatalogArchTests.cs`; `packages/rule-engine/src/__tests__/operator-catalog.test.ts`):
each source-scans its evaluator's operator dispatch and fails if the implemented set drifts from the
frozen catalog, and fails if any regex construct appears in the evaluator (ReDoS-exclusion, Decision
DG). A new "convenience" operator (or a regex/pattern operator) therefore cannot slip in at a version
bump without a deliberate, reviewed edit to the frozen catalog in BOTH tests — closing the "A erodes
into B one PR at a time" (Form.io) trajectory.

### Numeric & collation determinism (D1 ratification 2026-07-01, fix 2)

Cross-tier determinism is achieved deliberately per value kind, and PROVEN byte-for-byte by
`conformance/corpus/numeric-collation-determinism.json` (both tiers assert byte-identical outcomes):

- **General number arithmetic (`+`,`-`,`*`,`/`,`%`,`min`,`max`, number aggregates) uses IEEE-754
  double — IDENTICALLY on both tiers** (.NET `double` ≡ JS `number`, both binary64). Determinism comes
  from using the *same* IEEE ops on both tiers plus a canonical ECMAScript `Number::toString`
  serializer (`CanonicalNumber` / `String(n)`), NOT from a decimal type. (The D1 council's "pin
  decimal, not double" prescription is satisfied *in effect*: the risky value kind — money — IS
  decimal; general numbers are cross-tier-deterministic by identical-IEEE + canonical serialization,
  which is cheaper and matches the JS `number` the reactive tier already uses.)
- **Money (`money.add`/`money.sub`/`money.mul`, decimal-string aggregates) uses EXACT decimal**
  (BigInteger / BigInt mantissa + base-10 scale) — never IEEE. There is **no rounding mode** because
  v1 is **exact-or-fail-closed**: add/sub/mul are exact (scale grows), and the one operation that would
  need inexact decimal division — `avg` over a money column — **fails closed** (`rule.money_agg_unsupported`)
  rather than silently rounding or falling back to double.
- **String comparison & membership (`==`/`===`/`in`/`cat`) is ORDINAL / code-unit** on both tiers —
  locale-independent, case-sensitive, and applies NO Unicode normalization (composed U+00E9 ≠
  decomposed U+0065 U+0301). There is no locale-sensitive collation anywhere; relational `<`/`>` on
  strings coerces to number (a non-numeric string is a type error), so no string ordering is exposed.

## Resource bounds (fail-closed — the security crux, design §4)

| Bound | Default | Enforced | Failure code |
|---|---|---|---|
| Graph nodes | 5 000 | instance | `rule.graph_too_large` |
| Rows per aggregate | 2 000 | instance | `rule.table_too_large` |
| Dependency depth | 64 | publish (static) | `rule.compile.depth_exceeded` |
| References / rule | 64 | publish (static) | `rule.compile.too_many_refs` |
| AST nodes / rule | 256 | publish (static) | `rule.compile.ast_too_large` |
| Literal length | 4 096 | publish (static) | `rule.compile.literal_too_long` |
| Step budget | 250 000 ops | runtime | `rule.budget_exceeded` (fail-closed **outcome** — authoritative) |
| Wall-clock ceiling | 250 ms | runtime (both tiers) | `rule.timeout` **infra exception — NOT an outcome** (see below) |
| Cycles | — | publish (+ defensive per-row) | `rule.compile.cycle` / `rule.cycle` |

`RuleEngineLimits` numbers are CIC-tunable (Decision DF); the mechanism (fail-closed, static where
possible) is fixed.

### Determinism & the two runtime bounds (D1 ratification 2026-07-01, fix 1)

The op-budget and the wall-clock are NOT symmetric — only one may affect an outcome:

- **Op-budget (`StepBudget`) — the SOLE authoritative, outcome-affecting bound.** It is deterministic
  (the same input runs the same op count on both tiers), so exceeding it yields the SAME fail-closed
  `rule.budget_exceeded` result on both tiers. It stays a `RuleEvaluationResult` (`IsSaveBlocked`).
- **Wall-clock ceiling — a NON-authoritative liveness guard.** It is time/hardware-dependent: it can
  trip on the slower .NET tier but not the faster TS tier (or differently on replay / future hardware).
  If it produced an *outcome*, the two tiers would emit divergent `RuleOutcome` values for the same
  input, breaking replay-determinism and the byte-identical conformance corpus. So it does NOT become
  an outcome — it **throws an infrastructure exception that PROPAGATES**: `RuleEngineTimeoutException`
  (.NET) / `RuleTimeout` (TS). Callers treat it as an infrastructure fault (fail the write closed /
  retry / degrade the render — e.g. `FormEngine.ApplyRuleProjection` degrades to the unchanged view),
  never as a rule verdict. The TS tier also accepts an optional `AbortSignal` (the analog of the .NET
  `CancellationToken`) for explicit, deterministic cancellation.

> **Consumer note:** a reactive TS consumer that drives eval on every keystroke (e.g.
> `@harborline-software/ui-react`'s `useFormRuleGraph`) should wrap eval in an error boundary / `try` if it wants
> graceful UI degradation on the (rare) wall-clock trip — the op-budget fires first for real runaways.

## Seams (design §5)

- `IFormRuleGraph` (via `IFormRuleGraphFactory`) — the form orchestrator: compile a definition's
  rules → graph; evaluate a whole instance → outcomes + computed values + merged visibility; reactive
  `Reevaluate` / `AddRow` / `RemoveRow`. Consumed by `IFormEngine` (ADR 0055).
- `IGuardEvaluator` — the workflow orchestrator: a transition guard / action condition (a one-node
  graph) over a flat context bag → `Validity` / `ComputedValue`.
- The `foundation-rule-engine-event-bridge` reactive-trigger seam is EXTENDED for workflow triggers.

```csharp
services.AddHarborlineRuleEngine();           // IGuardEvaluator + IFormRuleGraphFactory
var compiled = RuleCompiler.Compile(rules); // throws RuleCompilationException on a bad/ cyclic def
var graph = factory.Create(compiled);
var result = graph.EvaluateInstance(instance, ct);
if (result.IsSaveBlocked) { /* fail closed: validity failure, errored value, or pending value */ }
```

## CP-safety (deferred, named)

v1 evaluates **first-party** definitions only. A `Compute` into a CP-locked field is subject to the
deferred ADR-0135 A1 fail-closed CP-reachability validator (a load-time admission check, not an eval
change) — SPINE-1's contract is identical either way; that gate is NOT built here.
