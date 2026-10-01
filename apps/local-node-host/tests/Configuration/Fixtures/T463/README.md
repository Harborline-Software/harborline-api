# T-463 candidate configuration fixtures

This extends the existing candidate verification path. It introduces no interpreter,
posting engine, persistence engine or alternate configuration grammar.

`asset.candidate.json` and `ledger.candidate.json` are complete request-only form
documents in the existing `SaveFormDefinitionRequest` format accepted by
`PackFormDefinitionContent`. They use the supported legacy request-only pack body,
as the pre-existing invoice tests do. The second is a journal **intake form**, not a
ledger posting definition. These candidates exercise field authorization and a
computed total. `fieldsMeta` explicitly types quantity, unitPrice and total as
numbers, and declares supplier as text. These are numeric rule examples, not a
money primitive contract. They do not represent replacement asset or ledger packs.

The existing test store installs each document unchanged as `FormDefinition`
content at version `1.0.0`, with stable keys `records/asset` and `records/journal`
in packages `verification.asset` and `verification.ledger`. Filenames locate test
inputs; the explicit content keys determine runtime identity. The existing
`finance.access` fixture supplies the declared `invoice.author`/`invoice.approver`
domain roles and `records:write` publisher binding. We reuse that authority fixture
rather than invent a new taxonomy. The cases prepare and re-read a real candidate
through `ConfigurationActivationTarget`, then run `VerificationRunner`. They do
not review, release or activate it; test installation uses the existing test gate.

The `.suite.json` documents use the Platform verification-suite/v1 contract. They
declare actors, grants, a virtual instant, locale, ordering, identifier seed,
simulated ports, initial records, actions and literal typed expectations. The
existing invoice case IDs are retained so the current independent expected-value
oracle can be reused: 10 x 100 = 1000, 3 x 400 = 1200 and 2 x 100 = 200. The clerk
cannot set `status` to `approved`: expected refusal is
`records-authority-insufficient` at `/values/status`. These literals are not
derived from the rule engine's output or production constants.

Initial records and simulated ports are staged suite inputs, not evidence that
the current host consumes those inputs. The current runner evaluates submitted
values and authority without durable record writes. Passing outcomes therefore
do not prove asset creation, journal posting, recovery or no-mutation on those
durable stores. `expectations.json` separately records future commands and
independent expected record/posting/audit deltas, including rejected outcomes.
No current receipt claims those deltas were observed.

## Lightweight executable checks

Run with PowerShell 7 on a compatible .NET runtime and an already built Platform
or pinned local-feed directory containing `Harborline.Blocks.BuilderDefinitions.dll`:

```powershell
pwsh -NoProfile -File eng/test-t463-fixture-admission.ps1 -AssemblyDirectory <existing-feed-directory>
```

This calls the production `VerificationSuite.Parse`, including semantic admission;
it performs no restore or build. It checks both suites, then probes empty
assertions, a wrongly typed boolean, duplicate stable case IDs, malformed initial
record JSON and the unregistered posting action. It prints the producer assembly
SHA-256 and admitted suite digests. Build provenance must be supplied by the caller:
a cached build is admission evidence only, never proof for the API source revision.

`ledger-post.future.suite.json` is an inadmissible negative catalog input; its
production admission refusal is a required regression, not posting acceptance.
Future scenarios in `expectations.json` are explicitly **NON-EXECUTABLE pending
specifications**. There is no future-acceptance switch or permanently failing
test marker. When a production posting adapter exists, replace the catalog
boundary regression with real admission and runner-outcome acceptance that can
turn green. Do not substitute `records.create` for posting. There are no skipped
tests, CI-wide red tests, waived failures or mock execution evidence.

When the heavy slot is available, run the existing host project with filter
`FullyQualifiedName~VerificationRunnerTests.File_authored_candidates`. Its two
new cases check real candidate parsing, production schema synthesis/validation of
every accepted row, refusal of wrong numeric types and undeclared fields,
preparation, production rule/authorization execution, exact results and
preservation of the effective-generation digest. They remain unrun; production
schema acceptance and host execution are not claimed from suite admission alone.

## Missing capabilities and owners

- T-463: catalog/runner bindings for posting and durable observation channels;
  meaningful expected/actual observations for no-mutation claims.
- T-549: existing asset replacement-pack work; production record write path is
  `Data/AssetRegistry/PackBoundRegistryRecordWriter.cs`, not this harness.
- T-544: existing ledger replacement-pack work; use
  `blocks-financial-ledger/Services/JournalPostingService.cs` and its governed
  store, and the existing `kernel-ledger/PostingEngine.cs` where appropriate.
- T-975: existing asset write-authorization ownership; the staged refusal cases
  do not replace its production write-gate evidence.
- T-460/T-461: existing atomic activation and proposed-change lifecycle. This
  increment supplies candidate test inputs, not full lifecycle certification.

The inspected `VerificationSuite` admission imposes structural/semantic refusals
(unique IDs, valid fact JSON, meaningful well-typed assertions, registered actions)
but no numeric size cap. No arbitrary size or complexity budget is asserted.
Forms compose existing fields/rules; no new layout taxonomy or branding resource
contract is introduced here.
